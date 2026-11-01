namespace GuildhestQueue.Core;

public enum LoopPhase { Idle, WaitingToQueue, Registering, Queued, Entering, InDuty, Completed, Leaving, BetweenRuns, Stopped }
public enum LoopAction { None, Register, Commence, Leave }
public enum QueuePhase { None, Pending, Queued, Ready, Accepted, InContent }

public sealed record DutyTarget(uint Id, bool Roulette, uint ContentType = 3)
{
    public bool IsValid => ContentType == 3 && Id != 0 && (!Roulette || Id == 4);
    public bool Matches(uint id, bool guildhest) => guildhest && id != 0 && (Roulette || id == Id);
}
public sealed record LoopOptions(int MaxRuns = 0, int ExitDelaySeconds = 5, int RepeatDelaySeconds = 3,
    bool AutoEnter = true, bool AutoLeave = true)
{
    public LoopOptions Normalize() => this with {
        MaxRuns = Math.Clamp(MaxRuns, 0, 99999),
        ExitDelaySeconds = Math.Clamp(ExitDelaySeconds, 0, 60),
        RepeatDelaySeconds = Math.Clamp(RepeatDelaySeconds, 2, 60)
    };
}
public sealed record Observation
{
    public bool LoggedIn { get; init; }
    public bool Ready { get; init; }
    public bool Transitioning { get; init; }
    public ulong CharacterId { get; init; }
    public uint JobId { get; init; }
    public bool InDuty { get; init; }
    public uint DutyId { get; init; }
    public bool DutyIsGuildhest { get; init; }
    public QueuePhase Queue { get; init; }
    public bool QueueMatches { get; init; }
    public uint PoppedDutyId { get; init; }
    public bool PoppedIsGuildhest { get; init; }
    public bool CanQueue { get; init; }
    public string QueueBlockReason { get; init; } = "";
    public bool FatalQueueBlock { get; init; }
    public bool CanCommence { get; init; }
    public bool CanLeave { get; init; }
}

/// <summary>A session exists only after Start. No running state is persisted or resumed on login.</summary>
public sealed class QueueLoop
{
    public LoopPhase Phase { get; private set; }
    public bool Running => Phase is not (LoopPhase.Idle or LoopPhase.Stopped);
    public string Status { get; private set; } = "尚未開始。";
    public int CompletedRuns { get; private set; }
    public DutyTarget? Target { get; private set; }
    public LoopOptions Options { get; private set; } = new();
    private ulong owner;
    private uint job;
    private double since, due;
    private bool accepted;

    public bool Start(DutyTarget target, LoopOptions options, Observation state, double now)
    {
        if (Running) return false;
        if (!target.IsValid) { Stop("只能選擇公會令或公會令隨機任務。"); return false; }
        if (!state.LoggedIn || !state.Ready || state.Transitioning || state.CharacterId == 0 || state.JobId == 0)
        { Stop("請登入角色並等待載入完成。"); return false; }
        if (state.InDuty || state.Queue != QueuePhase.None)
        { Stop("請先退出目前任務或取消原有排隊，再按開始。"); return false; }
        if (state.FatalQueueBlock) { Stop(state.QueueBlockReason); return false; }
        Target = target; Options = options.Normalize(); owner = state.CharacterId; job = state.JobId;
        CompletedRuns = 0; accepted = false; since = now; due = now;
        Phase = LoopPhase.WaitingToQueue; Status = "等待登記公會令。";
        return true;
    }

    public void Stop(string reason = "已停止。現有排隊可在遊戲內取消。")
    {
        Phase = LoopPhase.Stopped;
        Status = reason;
        accepted = false;
    }

    public bool MarkCompleted(Observation state, double now)
    {
        if (Target == null || Phase is not (LoopPhase.InDuty or LoopPhase.Entering or LoopPhase.Queued or LoopPhase.Registering) ||
            !state.InDuty || state.CharacterId != owner || !Target.Matches(state.DutyId, state.DutyIsGuildhest))
            return false;
        CompletedRuns++;
        Phase = LoopPhase.Completed; since = now; due = now + Options.ExitDelaySeconds;
        Status = $"已完成 {CompletedRuns} 次，" + (Options.AutoLeave ? "等待退場。" : "請手動退場。");
        return true;
    }

    public LoopAction Step(Observation state, double now)
    {
        if (!Running || Target == null) return LoopAction.None;
        if (!state.LoggedIn && !state.Transitioning) { Stop("角色已登出，循環已停止。"); return LoopAction.None; }
        if (state.Transitioning || !state.Ready)
        {
            if (Phase == LoopPhase.Entering && now - since > 120) Stop("進場逾時，請檢查遊戲狀態後重新開始。");
            return LoopAction.None;
        }
        if (state.CharacterId != owner || state.JobId != job)
        { Stop("角色或職業已變更，請重新開始。"); return LoopAction.None; }

        if (state.InDuty)
        {
            if (!Target.Matches(state.DutyId, state.DutyIsGuildhest))
            { Stop("目前不是選定的公會令，循環已停止。"); return LoopAction.None; }
            if (Phase is LoopPhase.Registering or LoopPhase.Queued or LoopPhase.Entering)
            { Phase = LoopPhase.InDuty; since = now; Status = "已進入公會令，請自行操作戰鬥。"; }
            else if (Phase is LoopPhase.WaitingToQueue or LoopPhase.BetweenRuns)
            { Stop("偵測到另外進入的任務，請退場後重新開始。"); return LoopAction.None; }

            if (Phase == LoopPhase.Completed && Options.AutoLeave && now >= due && state.CanLeave)
            {
                Phase = LoopPhase.Leaving; since = now; Status = "公會令已完成，正在退場。";
                return LoopAction.Leave;
            }
            if (Phase == LoopPhase.Leaving && now - since > 20)
                Stop("退場未完成，請手動退場後重新開始。");
            return LoopAction.None;
        }

        if (Phase is LoopPhase.Completed or LoopPhase.Leaving)
        {
            if (Options.MaxRuns > 0 && CompletedRuns >= Options.MaxRuns)
            { Stop($"已完成設定的 {CompletedRuns} 次，循環已結束。"); return LoopAction.None; }
            Phase = LoopPhase.BetweenRuns; due = now + Options.RepeatDelaySeconds; accepted = false;
            Status = "已退場，準備再次排隊。";
            return LoopAction.None;
        }
        if (Phase == LoopPhase.InDuty)
        { Stop("任務尚未完成就已離開，循環已停止。"); return LoopAction.None; }

        if (Phase is LoopPhase.WaitingToQueue or LoopPhase.BetweenRuns)
        {
            if (state.Queue != QueuePhase.None)
            { Stop("偵測到另外登記的排隊，循環已停止。"); return LoopAction.None; }
            if (state.FatalQueueBlock) { Stop(state.QueueBlockReason); return LoopAction.None; }
            if (!state.CanQueue) { Status = state.QueueBlockReason; return LoopAction.None; }
            if (now < due) return LoopAction.None;
            Phase = LoopPhase.Registering; since = now; accepted = false; Status = "正在登記公會令。";
            return LoopAction.Register;
        }

        if (Phase == LoopPhase.Registering)
        {
            if (state.Queue is QueuePhase.Pending or QueuePhase.Queued or QueuePhase.Ready or QueuePhase.Accepted)
            {
                if (!state.QueueMatches)
                {
                    if (now - since > 5 || state.Queue == QueuePhase.Ready)
                        Stop("登記內容與選擇不符，循環已停止。");
                    return LoopAction.None;
                }
                Phase = LoopPhase.Queued; Status = "排隊中，等待配對。";
            }
            else
            {
                if (now - since > 15)
                    Stop("排隊未成功，請檢查解鎖、等級、隊伍及任務搜尋器設定後重新開始。");
                return LoopAction.None;
            }
        }

        if (Phase is LoopPhase.Queued or LoopPhase.Entering)
        {
            if (state.Queue == QueuePhase.Accepted)
            {
                if (Phase != LoopPhase.Entering) { Phase = LoopPhase.Entering; since = now; }
                Status = "已確認進場，等待其他隊員與載入。";
                if (now - since > 120) Stop("進場逾時，請檢查遊戲狀態後重新開始。");
                return LoopAction.None;
            }
            if (state.Queue == QueuePhase.None)
            {
                if (Phase == LoopPhase.Entering && now - since < 120) return LoopAction.None;
                Stop("排隊已取消或進場未完成，循環已停止。");
                return LoopAction.None;
            }
            if (!state.QueueMatches)
            { Stop("排隊內容已變更，循環已停止。"); return LoopAction.None; }
            if (state.Queue is QueuePhase.Pending or QueuePhase.Queued)
            {
                Phase = LoopPhase.Queued; accepted = false;
                Status = "排隊中，等待配對。";
                return LoopAction.None;
            }
            if (state.Queue == QueuePhase.Ready)
            {
                if (!Target.Matches(state.PoppedDutyId, state.PoppedIsGuildhest))
                {
                    // The confirmation addon may be visible a frame before the popped duty is populated.
                    if (state.PoppedDutyId != 0) Stop("配對到的內容不是選定的公會令，未自動進場。");
                    return LoopAction.None;
                }
                Status = Options.AutoEnter ? "配對成功，準備進場。" : "配對成功，請手動確認進場。";
                if (Options.AutoEnter && state.CanCommence && !accepted)
                {
                    accepted = true; Phase = LoopPhase.Entering; since = now;
                    return LoopAction.Commence;
                }
                if (accepted && now - since > 120) Stop("進場確認逾時，請重新開始。");
            }
        }
        return LoopAction.None;
    }
}
