using GuildhestQueue.Core;

static class Tests
{
    private static int count;
    private static readonly Observation World = new() {
        LoggedIn = true, Ready = true, CharacterId = 42, JobId = 24, CanQueue = true
    };
    private static readonly Observation Queued = World with { Queue = QueuePhase.Queued, QueueMatches = true };
    private static readonly Observation Pop = Queued with {
        Queue = QueuePhase.Ready, PoppedDutyId = 101, PoppedIsGuildhest = true, CanCommence = true
    };
    private static readonly Observation Duty = World with {
        InDuty = true, DutyId = 101, DutyIsGuildhest = true, Queue = QueuePhase.InContent, CanLeave = true
    };
    private static void Assert(bool value, string message)
    { if (!value) throw new Exception(message); count++; }
    private static QueueLoop Start(LoopOptions? options = null, bool roulette = false)
    {
        var loop = new QueueLoop();
        Assert(loop.Start(new(roulette ? 4u : 101u, roulette), options ?? new(), World, 0), "Start an idle session");
        Assert(loop.Step(World, 0) == LoopAction.Register, "First registration");
        return loop;
    }
    private static QueueLoop Enter(LoopOptions? options = null)
    {
        var loop = Start(options);
        loop.Step(Queued, 1);
        Assert(loop.Step(Pop, 2) == LoopAction.Commence, "Confirm own guildhest");
        loop.Step(Duty, 3);
        Assert(loop.Phase == LoopPhase.InDuty, "Detect entry");
        return loop;
    }

    public static void Main()
    {
        var idle = new QueueLoop();
        Assert(idle.Step(Pop, 10) == LoopAction.None, "Disabled plugin never confirms unrelated duties");
        Assert(!idle.MarkCompleted(Duty, 10), "Disabled plugin ignores completions");
        Assert(!idle.Start(new(1, true), new(), World, 0), "Reject other roulettes");
        Assert(!idle.Start(new(99, false, 2), new(), World, 0), "Reject non-guildhest content");
        Assert(!idle.Start(new(101, false), new(), Queued, 0), "Do not adopt a pre-existing queue");
        Assert(!idle.Start(new(101, false), new(), Duty, 0), "Do not adopt a pre-existing duty");
        Assert(!idle.Start(new(101, false), new(), World with { CharacterId = 0 }, 0), "Require character identity");

        var loop = Start();
        Assert(loop.Step(World, 1) == LoopAction.None, "Registration is sent once");
        Assert(loop.Step(World, 16) == LoopAction.None && !loop.Running, "Failed registration stops without retry spam");

        loop = Start();
        loop.Step(Queued, 1);
        loop.Step(World, 2);
        Assert(!loop.Running, "Manual queue cancellation stops the loop");

        loop = Start();
        loop.Step(Queued, 1);
        Assert(loop.Step(Pop with { PoppedIsGuildhest = false }, 2) == LoopAction.None && !loop.Running,
            "Never confirm a non-guildhest");
        loop = Start();
        loop.Step(Queued, 1);
        Assert(loop.Step(Pop with { PoppedDutyId = 102 }, 2) == LoopAction.None && !loop.Running,
            "Never confirm a different specified guildhest");
        loop = Start();
        loop.Step(Queued, 1);
        Assert(loop.Step(Pop with { QueueMatches = false }, 2) == LoopAction.None && !loop.Running,
            "Do not confirm a replaced queue");

        loop = Start(roulette: true);
        loop.Step(Queued, 1);
        Assert(loop.Step(Pop with { PoppedDutyId = 102 }, 2) == LoopAction.Commence,
            "Guildhests roulette accepts any verified guildhest");

        loop = Start();
        loop.Step(Queued, 1);
        Assert(loop.Step(Pop with { PoppedDutyId = 0 }, 2) == LoopAction.None && loop.Running,
            "Wait until popup data is populated");
        Assert(loop.Step(Pop with { CanCommence = false }, 3) == LoopAction.None,
            "Do not click a disabled confirmation button");
        Assert(loop.Step(Pop, 4) == LoopAction.Commence, "Confirm when ready");
        Assert(loop.Step(Pop, 5) == LoopAction.None, "Do not repeatedly click confirmation");
        loop.Step(Queued, 6);
        Assert(loop.Step(Pop, 7) == LoopAction.Commence, "Handle a second matching popup");

        loop = Start(new(AutoEnter: false));
        loop.Step(Queued, 1);
        Assert(loop.Step(Pop, 2) == LoopAction.None && loop.Running, "Manual entry option");
        loop.Step(Queued with { Queue = QueuePhase.Accepted }, 3);
        loop.Step(Duty, 4);
        Assert(loop.Phase == LoopPhase.InDuty, "Manual acceptance still tracks this run");

        loop = Enter();
        Assert(loop.Step(Duty, 10000) == LoopAction.None, "Time alone never triggers exit");
        Assert(!loop.MarkCompleted(Duty with { DutyIsGuildhest = false }, 4), "Ignore foreign completion");
        Assert(loop.MarkCompleted(Duty, 4), "Record completion");
        Assert(!loop.MarkCompleted(Duty, 5) && loop.CompletedRuns == 1, "Deduplicate completion");
        Assert(loop.Step(Duty, 8) == LoopAction.None, "Respect completion delay");
        Assert(loop.Step(Duty with { CanLeave = false }, 9) == LoopAction.None, "Wait until leaving is allowed");
        Assert(loop.Step(Duty, 10) == LoopAction.Leave, "Exit only a completed duty");
        Assert(loop.Step(Duty, 11) == LoopAction.None, "Send leave once");
        loop.Step(World, 12);
        Assert(loop.Step(World, 14) == LoopAction.None, "Respect repeat delay");
        Assert(loop.Step(World, 15) == LoopAction.Register, "Register again after completion and exit");

        loop = Enter(new(MaxRuns: 1, ExitDelaySeconds: 0));
        loop.MarkCompleted(Duty, 4);
        Assert(loop.Step(Duty, 4) == LoopAction.Leave, "Last run exits normally");
        loop.Step(World, 5);
        Assert(!loop.Running && loop.CompletedRuns == 1, "Stop at requested number of completions");

        loop = Enter(new(AutoLeave: false));
        loop.MarkCompleted(Duty, 4);
        Assert(loop.Step(Duty, 1000) == LoopAction.None, "Manual exit option never auto-leaves");
        loop.Step(World, 1001);
        Assert(loop.Step(World, 1004) == LoopAction.Register, "Manual exit can still repeat");

        loop = Enter();
        loop.Step(World, 4);
        Assert(!loop.Running && loop.CompletedRuns == 0, "Leaving unfinished content stops instead of repeating");

        loop = Start();
        loop.Step(Queued, 1);
        loop.Stop();
        Assert(loop.Step(Pop, 2) == LoopAction.None && !loop.Running, "Stop immediately blocks auto-entry");
        Assert(!loop.MarkCompleted(Duty, 3), "Stop blocks completion-driven exit");

        loop = Start();
        loop.Step(World with { CharacterId = 43 }, 1);
        Assert(!loop.Running, "Character switch stops the session");
        loop = Start();
        loop.Step(World with { JobId = 25 }, 1);
        Assert(!loop.Running, "Job switch stops the session");
        loop = Start();
        loop.Step(World with { LoggedIn = false, Ready = false }, 1);
        Assert(!loop.Running, "Logout stops and never auto-resumes");

        loop = new();
        loop.Start(new(101, false), new(), World with { CanQueue = false, QueueBlockReason = "Waiting for party" }, 0);
        Assert(loop.Step(World with { CanQueue = false }, 1) == LoopAction.None && loop.Running, "Wait for queue conditions");
        Assert(loop.Step(World, 2) == LoopAction.Register, "Queue when party is ready");
        loop = Start();
        loop.Step(Queued, 1);
        Assert(loop.Step(Pop with { Transitioning = true }, 2) == LoopAction.None, "No actions while loading");

        loop = Enter();
        loop.MarkCompleted(Duty, 4);
        loop.Step(Duty, 10);
        loop.Step(Duty, 31);
        Assert(!loop.Running, "Failed exit stops instead of spamming");

        var normalized = new LoopOptions(-1, 1000, 0).Normalize();
        Assert(normalized.MaxRuns == 0 && normalized.ExitDelaySeconds == 60 && normalized.RepeatDelaySeconds == 2,
            "Clamp persisted settings");

        Console.WriteLine($"PASS: {count} assertions covering guildhest queue lifecycle and action boundaries.");
    }
}
