namespace GuildhestQueue.Core;

public enum ForwardPhase { Disabled, Waiting, Moving, Finished }
public enum ForwardAction { None, Start, Stop }
public sealed record ForwardOptions(bool Enabled = false, float DelaySeconds = 0.5f, float DurationSeconds = 1f)
{
    public ForwardOptions Normalize() => this with {
        DelaySeconds = float.IsFinite(DelaySeconds) ? Math.Clamp(DelaySeconds, 0f, 10f) : 0.5f,
        DurationSeconds = float.IsFinite(DurationSeconds) ? Math.Clamp(DurationSeconds, 0.1f, 5f) : 1f
    };
}
public sealed record ForwardObservation(
    bool SessionActive = false, bool InSessionDuty = false, bool DutyStarted = false,
    bool ControlReady = false, bool InCombat = false, bool AutoRunning = false);

/// <summary>One optional forward movement for each registration; never re-arms after a wipe.</summary>
public sealed class EntryForward
{
    public ForwardPhase Phase { get; private set; }
    public string Status { get; private set; } = "進場前進已關閉。";
    public bool Moving => Phase == ForwardPhase.Moving;
    private ForwardOptions options = new();
    private double? readySince;
    private double stopAt;

    public void Arm(ForwardOptions settings)
    {
        options = settings.Normalize();
        readySince = null;
        Phase = options.Enabled ? ForwardPhase.Waiting : ForwardPhase.Disabled;
        Status = options.Enabled ? "等待進場並可行動後前進。" : "進場前進已關閉。";
    }
    public ForwardAction Cancel(string status = "本次前進已結束。")
    {
        bool moving = Moving;
        if (Phase != ForwardPhase.Disabled) Phase = ForwardPhase.Finished;
        readySince = null;
        Status = status;
        return moving ? ForwardAction.Stop : ForwardAction.None;
    }
    public ForwardAction Step(ForwardObservation state, double now)
    {
        if (Phase is ForwardPhase.Disabled or ForwardPhase.Finished) return ForwardAction.None;
        if (!state.SessionActive) return Cancel();
        if (Moving)
        {
            if (!state.InSessionDuty || !state.DutyStarted || !state.ControlReady || state.InCombat)
                return Cancel("角色狀態已變更，已結束前進。");
            if (!state.AutoRunning)
                return Cancel("前進已由其他操作取消。");
            if (now >= stopAt) return Cancel("本次進場前進已完成。");
            return ForwardAction.None;
        }
        if (!state.InSessionDuty || !state.DutyStarted)
        { readySince = null; return ForwardAction.None; }
        if (state.InCombat) return Cancel("已開始戰鬥，略過本次前進。");
        if (!state.ControlReady) { readySince = null; return ForwardAction.None; }
        if (state.AutoRunning) return Cancel("已有自動前進，略過本次前進。");
        readySince ??= now;
        if (now - readySince.Value < options.DelaySeconds) return ForwardAction.None;
        stopAt = now + options.DurationSeconds;
        Phase = ForwardPhase.Moving;
        Status = $"進場後前進中（{options.DurationSeconds:0.0} 秒）。";
        return ForwardAction.Start;
    }
}
