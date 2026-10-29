using System.Diagnostics;

namespace SaidNest.Capture;

sealed record ReplayBatch(double Start,double AudioSeconds,bool Final,double WallSeconds,double StartBacklogSeconds,double FinishLagSeconds);
sealed record ReplayReport(int Version,string Id,string SessionId,string State,ModelProvenance Model,double MeetingSeconds,double WallSeconds,int Jobs,int MissedDeadlines,double MaxStartBacklogSeconds,double MaxFinishLagSeconds,ReplayBatch[] Batches,string? RevisionId);

static class ReplayEvaluation
{
    public static async Task<ReplayReport> Run(Vault vault,string id,string profile,CancellationToken token=default,Func<RenderedAudio,string,CancellationToken,Task<TrackTranscript>>? testDecoder=null)
    {
        if(Archive.Inspect(vault,id).State!="complete") throw new InvalidDataException("Replay requires a complete verified recording.");
        using var lease=new FileStream(Path.Combine(vault.Root,"inference.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var install=testDecoder==null?await Transcription.Load(profile,token):new EngineInstall("fixture","fixture","fixture","fixture","fixture","synthetic-replay");
        var session=Archive.ReadSession(vault,id);
        var indexes=session.Tracks.ToDictionary(t=>t.Id,t=>new AudioWindows(vault,session,t)); foreach(var index in indexes.Values)index.Poll();
        var segments=session.Tracks.ToDictionary(t=>t.Id,_=>new List<TranscriptSegment>());
        var duration=indexes.Values.Max(index=>index.Available);
        var provenance=new ModelProvenance(Transcription.Name(install),install.ModelSha256,install.ModelRevision,install.RuntimeBuild,install.BeamSize,install.BestOf);
        string runId=Guid.NewGuid().ToString("N");
        var batches=new List<ReplayBatch>();var totalClock=Stopwatch.StartNew();
        double virtualFinish=0,maxBacklog=0,maxLag=0;int jobs=0,missed=0;
        for(double start=0;start<duration-.02;start+=8) {
            bool tail=duration-start<8;
            foreach(int phase in tail?new[]{8}:new[]{4,8}) {
                var clock=Stopwatch.StartNew();
                double length=Math.Min(phase,duration-start),release=start+length;
                foreach(var track in session.Tracks) {
                    var index=indexes[track.Id];double seconds=Math.Min(length,index.Available-start);if(seconds<=.02)continue;
                    var audio=index.Render(start,seconds);
                    var decoded=testDecoder==null?await Transcription.Decode(install,audio,track.Id,token):await testDecoder(audio,track.Id,token);jobs++;
                    if(phase==8)segments[track.Id].AddRange(decoded.Segments.Select(s=>s with {Start=s.Start+start,End=s.End+start}));
                }
                double backlog=Math.Max(0,virtualFinish-release);
                virtualFinish=Math.Max(virtualFinish,release)+clock.Elapsed.TotalSeconds;
                double lag=virtualFinish-release;maxBacklog=Math.Max(maxBacklog,backlog);maxLag=Math.Max(maxLag,lag);
                if(!tail && lag>4)missed++;
                batches.Add(new(start,length,phase==8,clock.Elapsed.TotalSeconds,backlog,lag));
                var progress=new ReplayReport(1,runId,id,"running",provenance,duration,totalClock.Elapsed.TotalSeconds,jobs,missed,maxBacklog,maxLag,batches.ToArray(),null);
                vault.Replace($"sessions/{id}/replay-progress.snq",progress);
            }
            if((int)start%80==0) Console.WriteLine($"Local replay {provenance.Name}: {Math.Min(start+8,duration):F0}/{duration:F0} seconds, {missed} deadline misses. No transcript text is logged.");
        }
        var parent=Transcription.Revisions(vault,id).LastOrDefault(r=>r.Kind=="live" || r.Runtime.Contains("rolling",StringComparison.Ordinal));
        var tracks=session.Tracks.Select(t=>new TrackTranscript(t.Id,segments[t.Id].Count>0?"transcribed":"no-text",indexes[t.Id].Available,"Eight-second offline replay of the live algorithm. This was not a new live recording.",segments[t.Id].ToArray())).ToArray();
        var revision=new TranscriptRevision(1,Guid.NewGuid().ToString("N"),id,DateTimeOffset.UtcNow,$"whisper.cpp {install.RuntimeBuild} CUDA replay",install.ModelSha256,install.ModelRevision,"en",tracks,"replay",Transcription.Name(install),parent?.Id,8,vault.Read($"sessions/{id}/final.snm").Hash,install.BeamSize,install.BestOf);
        vault.Write($"sessions/{id}/transcript-{revision.CreatedUtc:yyyyMMddTHHmmssfffffffZ}-{revision.Id}.snt",revision);
        var report=new ReplayReport(1,runId,id,"complete",provenance,duration,totalClock.Elapsed.TotalSeconds,jobs,missed,maxBacklog,maxLag,batches.ToArray(),revision.Id);
        vault.Write($"sessions/{id}/replay-{runId}.snq",report);vault.Replace($"sessions/{id}/replay-progress.snq",report);return report;
    }
}
