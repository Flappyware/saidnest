using System.Text.Json;

namespace SaidNest.Capture;

sealed record CaptureCondition(bool SpeakerBleedReported,string Source,DateTimeOffset UpdatedUtc,string Notice);
static class CaptureConditions
{
    public static CaptureCondition? Read(Vault vault,string id)=>File.Exists(Path.Combine(vault.SessionPath(id),"capture-conditions.sna"))?vault.Read($"sessions/{id}/capture-conditions.sna").Metadata.Deserialize<CaptureCondition>(Vault.Json):null;
    public static void ReportSpeakerBleed(Vault vault,string id)
    {
        _=Archive.ReadSession(vault,id);
        vault.Replace($"sessions/{id}/capture-conditions.sna",new CaptureCondition(true,"Owner observation",DateTimeOffset.UtcNow,"Speaker audio was audible on the microphone. Microphone passages may include remote voices. Reference checks should include all audible words; source tracks are not speaker identities."));
    }
}
