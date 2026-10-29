using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;

namespace SaidNest.Capture;

sealed record EvaluationCase(string Id,string Track,double Start,double Seconds);
sealed record EvaluationSet(int Version,string Id,string SessionId,string ManifestHash,DateTimeOffset CreatedUtc,EvaluationCase[] Cases);
sealed record ReferenceText(int Version,string CaseId,string Text,bool Verified,DateTimeOffset UpdatedUtc,string Normalization);
sealed record EvaluationResult(string CaseId,string Track,double Start,int PhaseSeconds,double WallSeconds,string AudioSha256,TrackTranscript Transcript);
sealed record ModelProvenance(string Name,string Sha256,string Revision,string Runtime,int BeamSize,int BestOf);
sealed record CorpusScore(string Case,string AudioSha256,double AudioSeconds,double WallSeconds,WordScore Score,int RejectedSegments);
sealed record EvaluationRun(int Version,string Id,string SetId,string SessionId,DateTimeOffset CreatedUtc,string State,ModelProvenance Model,EvaluationResult[] Results,CorpusScore[] Corpus);

static class Evaluation
{
    public static EvaluationSet Prepare(Vault vault,string id)
    {
        if(Archive.Inspect(vault,id).State!="complete") throw new InvalidDataException("Evaluation requires a finalized intact recording.");
        string hash=vault.Read($"sessions/{id}/final.snm").Hash;
        if(File.Exists(Path.Combine(vault.SessionPath(id),"evaluation-set.snq"))) {
            var existing=ReadSet(vault,id);
            if(existing.ManifestHash!=hash) throw new InvalidDataException("The evaluation audio manifest changed.");
            return existing;
        }
        var session=Archive.ReadSession(vault,id);
        var parts=Directory.GetFiles(vault.SessionPath(id),"live-*-final.snt").Select(path=>vault.Read($"sessions/{id}/{Path.GetFileName(path)}").Metadata.Deserialize<LivePart>(Vault.Json)!).ToArray();
        if(parts.Length==0) throw new InvalidDataException("Prepare an evaluation from a saved live recording first.");
        double duration=parts.Max(p=>p.End);
        var selected=new List<int>();
        for(int bucket=0;bucket<6;bucket++) {
            var groups=parts.Where(p=>p.Start>=duration*bucket/6 && p.Start<duration*(bucket+1)/6 && p.End-p.Start>=7.9).GroupBy(p=>p.Window).OrderBy(g=>g.Key).ToArray();
            var best=groups.FirstOrDefault(g=>g.Count(p=>p.Transcript.Segments.Length>0)==session.Tracks.Length) ?? groups.FirstOrDefault(g=>g.Any(p=>p.Transcript.Segments.Length>0));
            if(best!=null) selected.Add(best.Key);
        }
        var cases=selected.Distinct().SelectMany(window=>session.Tracks.Select(track=>new EvaluationCase(Guid.NewGuid().ToString("N"),track.Id,window*8,8))).ToArray();
        if(cases.Length==0) throw new InvalidDataException("No complete voiced evaluation windows were found.");
        var set=new EvaluationSet(1,Guid.NewGuid().ToString("N"),id,hash,DateTimeOffset.UtcNow,cases);
        vault.Write($"sessions/{id}/evaluation-set.snq",set); return set;
    }
    public static EvaluationSet ReadSet(Vault vault,string id)=>vault.Read($"sessions/{id}/evaluation-set.snq").Metadata.Deserialize<EvaluationSet>(Vault.Json)!;
    public static EvaluationRun[] Runs(Vault vault,string id)=>Directory.GetFiles(vault.SessionPath(id),"evaluation-run-*.snq").OrderDescending(StringComparer.Ordinal).Select(path=>vault.Read($"sessions/{id}/{Path.GetFileName(path)}").Metadata.Deserialize<EvaluationRun>(Vault.Json)!).ToArray();
    public static ReferenceText? Reference(Vault vault,string id,string caseId)=>File.Exists(Path.Combine(vault.SessionPath(id),$"reference-{caseId}.snq"))?vault.Read($"sessions/{id}/reference-{caseId}.snq").Metadata.Deserialize<ReferenceText>(Vault.Json):null;
    public static void SaveReference(Vault vault,string id,string caseId,string text,bool verified)
    {
        if(text==null || !ReadSet(vault,id).Cases.Any(c=>c.Id==caseId) || text.Length>16000) throw new ArgumentException("Invalid review passage.");
        vault.Replace($"sessions/{id}/reference-{caseId}.snq",new ReferenceText(1,caseId,text,verified,DateTimeOffset.UtcNow,QualityMetrics.Normalization));
    }
    public static object Review(Vault vault,string id)
    {
        var set=ReadSet(vault,id);
        var runs=Runs(vault,id).Where(r=>r.SetId==set.Id).GroupBy(r=>r.Model.Name).Select(g=>g.OrderByDescending(r=>r.CreatedUtc).First()).ToArray();
        if(runs.SelectMany(r=>r.Results).Where(r=>r.PhaseSeconds==8).GroupBy(r=>r.CaseId).Any(g=>g.Select(r=>r.AudioSha256).Distinct().Count()>1)) throw new InvalidDataException("Models were evaluated with different audio bytes.");
        return new {set.Id,normalization=QualityMetrics.Normalization,captureConditions=CaptureConditions.Read(vault,id),summaries=runs.Select(r=>Summary(vault,id,r)).ToArray(),
            cases=set.Cases.Select(item=> {
                string? baseline=runs.FirstOrDefault(r=>r.Model.Name=="tiny.en")?.Results.FirstOrDefault(r=>r.CaseId==item.Id && r.PhaseSeconds==8)?.Transcript.Segments.Select(s=>s.Text).Aggregate("",(a,b)=>a+" "+b);
                return new {item.Id,item.Track,item.Start,item.Seconds,reference=Reference(vault,id,item.Id),candidates=runs.Select(run=> {
                    var result=run.Results.FirstOrDefault(r=>r.CaseId==item.Id && r.PhaseSeconds==8);
                    string text=string.Join(" ",result?.Transcript.Segments.Select(s=>s.Text) ?? []);
                    var reference=Reference(vault,id,item.Id);
                    return new {model=run.Model.Name,runId=run.Id,text,words=QualityMetrics.Words(text).Length,disagreementWithTiny=baseline==null?(double?)null:QualityMetrics.Disagreement(baseline,text),
                        score=reference?.Verified==true?QualityMetrics.Score(reference.Text,text):null};
                }).ToArray()};
            }).ToArray()};
    }
    public static async Task<EvaluationRun> Run(Vault vault,string id,string installFile,string corpusFolder,CancellationToken token=default)
    {
        var set=Prepare(vault,id);
        using var lease=new FileStream(Path.Combine(vault.Root,"inference.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var install=await Transcription.Load(installFile,token);
        var session=Archive.ReadSession(vault,id);
        var indexes=session.Tracks.ToDictionary(t=>t.Id,t=>new AudioWindows(vault,session,t)); foreach(var index in indexes.Values) index.Poll();
        var results=new List<EvaluationResult>();
        foreach(var group in set.Cases.GroupBy(c=>c.Start).OrderBy(g=>g.Key)) {
            foreach(int phase in new[]{4,8}) foreach(var item in group.OrderBy(c=>c.Track,StringComparer.Ordinal)) {
                var clock=Stopwatch.StartNew();
                var audio=indexes[item.Track].Render(item.Start,phase);
                string audioHash=Convert.ToHexString(SHA256.HashData(audio.Wav)).ToLowerInvariant();
                var decoded=await Transcription.Decode(install,audio,item.Track,token);
                results.Add(new(item.Id,item.Track,item.Start,phase,clock.Elapsed.TotalSeconds,audioHash,decoded));
                Console.WriteLine($"Local evaluation {Transcription.Name(install)}: {results.Count}/{set.Cases.Length*2} jobs. No transcript text is logged.");
            }
        }
        var corpus=new List<CorpusScore>();
        var manifest=JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(corpusFolder,"manifest.json"),token));
        foreach(var entry in manifest.RootElement.GetProperty("files").EnumerateArray()) {
            string file=entry.GetProperty("file").GetString()!;
            if(Path.GetFileName(file)!=file) throw new InvalidDataException("Invalid corpus file.");
            string actual=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(corpusFolder,file),token))).ToLowerInvariant();
            if(actual!=entry.GetProperty("sha256").GetString()) throw new CryptographicException("Synthetic corpus changed.");
        }
        foreach(string name in new[]{"speech","silence"}) {
            string path=Path.Combine(corpusFolder,name+".wav");
            using var wave=new WaveFileReader(path);
            double duration=wave.TotalTime.TotalSeconds;
            byte[] bytes=await File.ReadAllBytesAsync(path,token);
            var clock=Stopwatch.StartNew();
            var decoded=await Transcription.Decode(install,new(bytes,duration,-10,-20,0,0),"synthetic",token,bypassQuietGuard:true);
            string hypothesis=string.Join(" ",decoded.Segments.Select(s=>s.Text));
            corpus.Add(new(name,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path,token))).ToLowerInvariant(),duration,clock.Elapsed.TotalSeconds,
                QualityMetrics.Score(await File.ReadAllTextAsync(Path.Combine(corpusFolder,name+".reference.txt"),token),hypothesis),decoded.RejectedSegments));
        }
        var run=new EvaluationRun(1,Guid.NewGuid().ToString("N"),set.Id,id,DateTimeOffset.UtcNow,"complete",new(Transcription.Name(install),install.ModelSha256,install.ModelRevision,install.RuntimeBuild,install.BeamSize,install.BestOf),results.ToArray(),corpus.ToArray());
        vault.Write($"sessions/{id}/evaluation-run-{run.Id}.snq",run);
        return run;
    }
    public static object Summary(Vault vault,string id,EvaluationRun run)
    {
        var scoredCases=run.Results.Where(r=>r.PhaseSeconds==8).Select(r=>new {result=r,reference=Reference(vault,id,r.CaseId)}).Where(x=>x.reference?.Verified==true).Select(x=>new {x.result.Track,score=QualityMetrics.Score(x.reference!.Text,string.Join(" ",x.result.Transcript.Segments.Select(s=>s.Text)))}).ToArray();
        var scored=scoredCases.Select(x=>x.score).ToArray();
        var pairs=run.Results.GroupBy(r=>(r.Start,r.PhaseSeconds)).Select(g=>new {g.Key.Start,g.Key.PhaseSeconds,wallSeconds=g.Sum(r=>r.WallSeconds)}).OrderBy(p=>p.Start).ThenBy(p=>p.PhaseSeconds).ToArray();
        double virtualFinish=0,maxBacklog=0,maxFinishLag=0; int missed=0,index=0;
        foreach(var group in pairs.GroupBy(p=>p.Start)) {
            foreach(var pair in group) {
                double release=index*8+pair.PhaseSeconds;
                maxBacklog=Math.Max(maxBacklog,Math.Max(0,virtualFinish-release));
                virtualFinish=Math.Max(virtualFinish,release)+pair.wallSeconds;
                maxFinishLag=Math.Max(maxFinishLag,virtualFinish-release); if(virtualFinish>release+4) missed++;
            }
            index++;
        }
        return new {run.Id,run.SetId,run.State,run.Model,cases=run.Results.Count(r=>r.PhaseSeconds==8),jobs=run.Results.Length,verifiedReferences=scored.Length,
            referenceWords=scored.Sum(s=>s.ReferenceWords),wordErrorRate=scored.Sum(s=>s.ReferenceWords)==0?(double?)null:scored.Sum(s=>s.Edits)/(double)scored.Sum(s=>s.ReferenceWords),
            maxDuplexBatchSeconds=pairs.Max(p=>p.wallSeconds),maxStartBacklogSeconds=maxBacklog,maxFinishLagSeconds=maxFinishLag,missedFourSecondDeadlines=missed,
            byTrack=scoredCases.GroupBy(x=>x.Track).Select(g=>new {track=g.Key,verifiedReferences=g.Count(),referenceWords=g.Sum(x=>x.score.ReferenceWords),wordErrorRate=g.Sum(x=>x.score.ReferenceWords)==0?(double?)null:g.Sum(x=>x.score.Edits)/(double)g.Sum(x=>x.score.ReferenceWords)}).ToArray(),
            captureConditions=CaptureConditions.Read(vault,id),performanceScope="Accelerated replay of stratified recorded windows, two sources sequentially, four/eight-second updates with model cold start. Not a full sustained live trial.",normalization=QualityMetrics.Normalization,corpus=run.Corpus};
    }
}
