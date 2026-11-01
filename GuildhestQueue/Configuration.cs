using Dalamud.Configuration;

namespace GuildhestQueue;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool ForwardOnEntry { get; set; }
    public float ForwardDelaySeconds { get; set; } = 0.5f;
    public float ForwardDurationSeconds { get; set; } = 1f;
    public uint DutyId { get; set; } = 4;
    public bool Roulette { get; set; } = true;
    public int MaxRuns { get; set; }
    public int ExitDelaySeconds { get; set; } = 5;
    public int RepeatDelaySeconds { get; set; } = 3;
    public bool AutoEnter { get; set; } = true;
    public bool AutoLeave { get; set; } = true;
}
