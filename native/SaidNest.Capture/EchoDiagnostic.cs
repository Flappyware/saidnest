using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SaidNest.Capture;

static class EchoDiagnostic
{
    public static object Analyze(Vault vault,string id)
    {
        if(Archive.Inspect(vault,id).State!="complete")throw new InvalidDataException("Echo analysis requires a verified stopped recording.");
        var session=Archive.ReadSession(vault,id);
        var mic=new AudioWindows(vault,session,session.Tracks.Single(t=>t.Id=="mic"));mic.Poll();
        var output=new AudioWindows(vault,session,session.Tracks.Single(t=>t.Id=="output"));output.Poll();
        var rows=new List<object>();
        foreach(double start in Evaluation.ReadSet(vault,id).Cases.Select(c=>c.Start).Distinct().Order()) {
            var a=mic.Render(start,8);var b=output.Render(start,8);
            try {
                double[] Reduce(byte[] wav) {
                    int count=(wav.Length-44)/40;var values=new double[count];
                    for(int i=0;i<count;i++) {double sum=0;for(int n=0;n<20;n++)sum+=BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44+(i*20+n)*2));values[i]=sum/(20*32768.0);}
                    double mean=values.Average();for(int i=0;i<count;i++)values[i]-=mean;return values;
                }
                var near=Reduce(a.Wav);var far=Reduce(b.Wav);
                double energy=Math.Sqrt(near.Sum(x=>x*x)*far.Sum(x=>x*x)),best=0;int delay=0;
                if(energy>1e-9) for(int lag=-240;lag<=720;lag++) {
                    double sum=0;int begin=Math.Max(0,lag),end=Math.Min(near.Length,far.Length+lag);
                    for(int i=begin;i<end;i++)sum+=near[i]*far[i-lag];
                    double correlation=Math.Abs(sum)/energy;if(correlation>best){best=correlation;delay=lag;}
                }
                rows.Add(new {startSeconds=start,micPeakDbfs=a.PeakDbfs,outputPeakDbfs=b.PeakDbfs,maxWaveformCorrelation=best,relativeLagMs=delay/2.4});
            }finally{CryptographicOperations.ZeroMemory(a.Wav);CryptographicOperations.ZeroMemory(b.Wav);}
        }
        return new {sessionId=id,method="Box-downsampled 2.4kHz waveform correlation, lag search -100 to +300ms; similarity diagnostic only, not AEC or an authoritative acoustic delay estimate.",windows=rows};
    }
}
