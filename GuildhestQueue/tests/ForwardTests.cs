using GuildhestQueue.Core;

static class ForwardTests
{
    private static int count;
    private static readonly ForwardObservation Ready = new(true, true, true, true, false, false);
    private static readonly ForwardObservation Moving = Ready with { AutoRunning = true };
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); count++; }
    private static EntryForward Arm(float delay = 0.5f, float duration = 1f)
    { var f = new EntryForward(); f.Arm(new(true, delay, duration)); return f; }
    private static EntryForward Start()
    {
        var f = Arm(0);
        Check(f.Step(Ready, 0) == ForwardAction.Start, "Start when controllable and delay is zero");
        return f;
    }
    public static int Run()
    {
        var f = new EntryForward();
        Check(f.Step(Ready, 0) == ForwardAction.None, "Default is no movement");
        f.Arm(new());
        Check(f.Step(Ready, 1) == ForwardAction.None, "Existing users opt in to entry movement");

        f = Arm();
        Check(f.Step(Ready with { InSessionDuty = false }, 0) == ForwardAction.None, "Never move in the overworld or queue");
        Check(f.Step(Ready with { DutyStarted = false }, 1) == ForwardAction.None, "Wait for duty start and spawn barrier");
        Check(f.Step(Ready with { ControlReady = false }, 2) == ForwardAction.None, "Wait through loading and dialogue locks");
        Check(f.Step(Ready, 3) == ForwardAction.None, "Delay starts once character control is ready");
        Check(f.Step(Ready, 3.4) == ForwardAction.None, "Do not start before ready delay");
        Check(f.Step(Ready, 3.5) == ForwardAction.Start, "Start after continuous ready delay");
        Check(f.Step(Moving, 4.4) == ForwardAction.None, "Continue only for configured duration");
        Check(f.Step(Moving, 4.5) == ForwardAction.Stop, "Stop exactly at duration limit");
        Check(f.Step(Ready, 100) == ForwardAction.None, "No second movement in the same run");

        f = Arm();
        f.Step(Ready, 0);
        f.Step(Ready with { ControlReady = false }, 0.3);
        Check(f.Step(Ready, 0.6) == ForwardAction.None, "A new control lock resets the delay");
        Check(f.Step(Ready, 1.1) == ForwardAction.Start, "Move only after control has stayed available");

        f = Arm();
        Check(f.Step(Ready with { AutoRunning = true }, 0) == ForwardAction.None, "Do not take over existing auto-run");
        Check(f.Phase == ForwardPhase.Finished, "Skip the run when somebody else controls auto-run");
        Check(f.Cancel() == ForwardAction.None, "Never stop auto-run not started by this feature");

        f = Arm();
        Check(f.Step(Ready with { InCombat = true }, 0) == ForwardAction.None, "Skip when combat has already started");
        Check(f.Step(Ready, 1) == ForwardAction.None, "Do not move later after combat finishes");

        f = Start();
        Check(f.Step(Moving with { InCombat = true }, 0.1) == ForwardAction.Stop, "Combat immediately releases movement");
        f = Start();
        Check(f.Step(Moving with { ControlReady = false }, 0.1) == ForwardAction.Stop, "Casting or a cutscene releases movement");
        f = Start();
        Check(f.Step(Moving with { InSessionDuty = false }, 0.1) == ForwardAction.Stop, "Zoning or a mismatched duty releases movement");
        f = Start();
        Check(f.Step(Moving with { SessionActive = false }, 0.1) == ForwardAction.Stop, "Stop, logout and completion release movement");
        f = Start();
        Check(f.Step(Ready, 0.1) == ForwardAction.Stop, "Manual cancellation releases ownership");
        Check(f.Step(Ready, 0.2) == ForwardAction.None, "Never re-enable movement after manual cancellation");

        f = Start();
        Check(f.Cancel() == ForwardAction.Stop, "Unload cancellation releases movement");
        Check(f.Cancel() == ForwardAction.None, "Repeated cleanup is idempotent");
        Check(f.Step(Ready, 10) == ForwardAction.None, "Cancellation does not re-arm");
        f.Arm(new(true, 0, 0.2f));
        Check(f.Step(Ready, 20) == ForwardAction.Start, "A new registration re-arms one movement");
        Check(f.Step(Moving, 20.3) == ForwardAction.Stop, "The new run has its own duration");
        Check(f.Step(Ready with { DutyStarted = false }, 21) == ForwardAction.None, "Wipe never re-arms the movement");
        Check(f.Step(Ready, 22) == ForwardAction.None, "Recommence never re-arms the movement");

        f = Arm();
        f.Step(Ready, 0);
        Check(f.Step(Ready with { SessionActive = false }, 0.1) == ForwardAction.None, "Cancel before movement sends no stop command");
        Check(f.Step(Ready, 1) == ForwardAction.None, "Cancelled pending movement never starts later");

        var options = new ForwardOptions(true, float.NaN, float.PositiveInfinity).Normalize();
        Check(options.DelaySeconds == 0.5f && options.DurationSeconds == 1f, "Invalid persisted times use defaults");
        options = new ForwardOptions(true, -1, 99).Normalize();
        Check(options.DelaySeconds == 0f && options.DurationSeconds == 5f, "Bound delay and duration");
        Console.WriteLine($"PASS: {count} entry-forward assertions.");
        return count;
    }
}
