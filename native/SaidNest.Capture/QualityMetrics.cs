using System.Text;
using System.Text.RegularExpressions;

namespace SaidNest.Capture;

sealed record WordScore(int ReferenceWords,int HypothesisWords,int Edits,double? WordErrorRate);
static partial class QualityMetrics
{
    public const string Normalization="nfkc-lower-apostrophe-words-v1";
    [GeneratedRegex(@"[\p{L}\p{Nd}]+(?:'[\p{L}\p{Nd}]+)?")]
    private static partial Regex WordPattern();
    public static string[] Words(string text)=>WordPattern().Matches(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('’','\'')).Select(m=>m.Value).ToArray();
    public static WordScore Score(string reference,string hypothesis)
    {
        var expected=Words(reference); var actual=Words(hypothesis);
        if(expected.Length>10000 || actual.Length>10000) throw new ArgumentException("Review shorter reference passages.");
        var previous=Enumerable.Range(0,actual.Length+1).ToArray();
        for(int i=0;i<expected.Length;i++) {
            int[] row=new int[actual.Length+1]; row[0]=i+1;
            for(int j=0;j<actual.Length;j++) row[j+1]=Math.Min(Math.Min(previous[j+1]+1,row[j]+1),previous[j]+(expected[i]==actual[j]?0:1));
            previous=row;
        }
        int edits=previous[^1];
        return new(expected.Length,actual.Length,edits,expected.Length==0?null:edits/(double)expected.Length);
    }
    public static double Disagreement(string a,string b)
    {
        var score=Score(a,b); return score.Edits/(double)Math.Max(1,Math.Max(score.ReferenceWords,score.HypothesisWords));
    }
}
