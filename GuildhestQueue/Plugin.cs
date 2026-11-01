using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GuildhestQueue.Core;
using Lumina.Excel.Sheets;

namespace GuildhestQueue;

public sealed class Plugin : IDalamudPlugin
{
    private sealed record DutyOption(DutyTarget Target, uint InstanceId, string Name, int Level);
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IPlayerState player;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly IPartyList party;
    private readonly IGameGui gui;
    private readonly IDutyState duties;
    private readonly ICommandManager commands;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly QueueLoop loop = new();
    private readonly List<DutyOption> options = [];
    private readonly HashSet<uint> guildhests = [];
    private readonly HashSet<uint> dutyTerritories = [];
    private bool visible = true, disposed;
    private string search = "", selectionError = "";
    private int requested, completedDuty;
    private double nextTick;
    private Observation last = new();
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public Plugin(IDalamudPluginInterface pi, IFramework framework, IClientState client,
        IPlayerState player, IObjectTable objects, ICondition condition, IPartyList party,
        IGameGui gui, IDutyState duties, ICommandManager commands, IPluginLog log, IDataManager data)
    {
        this.pi = pi; this.framework = framework; this.client = client; this.player = player;
        this.objects = objects; this.condition = condition; this.party = party; this.gui = gui;
        this.duties = duties; this.commands = commands; this.log = log;
        config = pi.GetPluginConfig() as Configuration ?? new();

        foreach (var row in data.GetExcelSheet<ContentFinderCondition>())
        {
            if (row.ContentType.RowId != 0 && row.TerritoryType.RowId != 0)
                dutyTerritories.Add(row.TerritoryType.RowId);
            if (row.ContentType.RowId != 3 || row.RowId == 0 || string.IsNullOrWhiteSpace(row.Name.ToString()))
                continue;
            guildhests.Add(row.RowId);
            options.Add(new(new(row.RowId, false), row.Content.RowId, row.Name.ToString(), row.ClassJobLevelRequired));
        }
        options.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : a.Target.Id.CompareTo(b.Target.Id));
        if (data.GetExcelSheet<Lumina.Excel.Sheets.ContentRoulette>().TryGetRow(4, out var roulette) && roulette.ContentType.RowId == 3)
            options.Insert(0, new(new(4, true), 0, roulette.Name.ToString(), roulette.RequiredLevel));
        commands.AddHandler("/ghq", new CommandInfo(OnCommand) {
            HelpMessage = "公會令循環：/ghq 開啟設定，/ghq start 開始，/ghq stop 停止。"
        });
        framework.Update += Tick;
        duties.DutyCompleted += OnCompleted;
        pi.UiBuilder.Draw += Draw;
        pi.UiBuilder.OpenConfigUi += Open;
        pi.UiBuilder.OpenMainUi += Open;
    }

    private void Open() => visible = true;
    private void OnCommand(string _, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "start": Interlocked.Exchange(ref requested, 1); visible = true; break;
            case "stop": Interlocked.Exchange(ref requested, 2); break;
            default: visible = true; break;
        }
    }
    private void OnCompleted(IDutyStateEventArgs _) =>
        Interlocked.Exchange(ref completedDuty, checked((int)duties.ContentFinderCondition.RowId));

    private DutyOption? Selected() => options.FirstOrDefault(o =>
        o.Target.Id == config.DutyId && o.Target.Roulette == config.Roulette);
    private LoopOptions Settings() => new LoopOptions(config.MaxRuns, config.ExitDelaySeconds,
        config.RepeatDelaySeconds, config.AutoEnter, config.AutoLeave).Normalize();
    private void Save() => pi.SavePluginConfig(config);

    // All game pointer access and actions run on the framework thread. Pointers are reacquired per action.
    private void Tick(IFramework _)
    {
        if (disposed) return;
        var request = Interlocked.Exchange(ref requested, 0);
        if (request == 2) { loop.Stop(); Interlocked.Exchange(ref completedDuty, 0); }
        var now = Now;
        if (request == 0 && now < nextTick) return;
        nextTick = now + 0.2;
        try
        {
            var selected = loop.Running ? options.FirstOrDefault(o => o.Target == loop.Target) : Selected();
            last = Observe(selected);
            if (request == 1)
            {
                Interlocked.Exchange(ref completedDuty, 0);
                selectionError = "";
                if (selected == null) { selectionError = "請先選擇公會令。"; return; }
                loop.Start(selected.Target, Settings(), last, now);
            }
            var completed = Interlocked.Exchange(ref completedDuty, 0);
            if (completed != 0 && last.DutyId == (uint)completed)
                loop.MarkCompleted(last, now);
            var action = loop.Step(last, now);
            if (action != LoopAction.None) Execute(action);
        }
        catch (Exception error)
        {
            log.Error(error, "Guildhest Queue stopped after an error.");
            loop.Stop("發生錯誤，循環已停止；請查看 Dalamud 記錄。");
            visible = true;
        }
    }

    private unsafe Observation Observe(DutyOption? selected)
    {
        bool transition = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
        bool inDuty = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] ||
            condition[ConditionFlag.BoundByDuty95];
        uint currentDuty = inDuty ? duties.ContentFinderCondition.RowId : 0;
        var finder = ContentsFinder.Instance();
        var hud = AgentHUD.Instance();
        bool ready = client.IsLoggedIn && player.IsLoaded && objects.LocalPlayer != null &&
            finder != null && hud != null && (!inDuty || currentDuty != 0);
        if (!ready)
            return new() { LoggedIn = client.IsLoggedIn, Transitioning = transition, InDuty = inDuty };

        bool busy = transition || condition[ConditionFlag.InCombat] || condition[ConditionFlag.Casting] ||
            condition[ConditionFlag.Occupied] || condition[ConditionFlag.Occupied30] ||
            condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] ||
            condition[ConditionFlag.Occupied33] || condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78] ||
            condition[ConditionFlag.OccupiedSummoningBell];
        var group = GroupManager.Instance();
        bool leader = party.Length <= 1 || (group != null && group->MainGroup.IsEntityIdPartyLeader(player.EntityId));
        bool partyReady = !party.Any(p => dutyTerritories.Contains(p.Territory.RowId));
        string block = "";
        bool fatal = false;
        var instance = FFXIVClientStructs.FFXIV.Client.Game.UI.InstanceContent.Instance();
        if (!inDuty && instance != null && instance->GetPenaltyRemainingInMinutes(0) > 0)
        { block = "目前有任務登記懲罰，請稍後重新開始。"; fatal = true; }
        else if (!inDuty && (finder->IsUnrestrictedParty || finder->IsExplorerMode))
        { block = "請先在任務搜尋器關閉「解除限制」和「探索模式」。"; fatal = true; }
        else if (!inDuty && selected == null) { block = "請先選擇公會令。"; fatal = true; }
        else if (!inDuty && selected != null && player.Level < selected.Level)
        { block = "目前職業等級不足，請更換選擇或職業後重新開始。"; fatal = true; }
        else if (!inDuty && selected is { Target.Roulette: false } && !UIState.IsInstanceContentUnlocked(selected.InstanceId))
        { block = "尚未解鎖這個公會令。"; fatal = true; }
        else if (!leader) block = "等待成為隊長；插件只由隊長登記任務。";
        else if (!partyReady && !inDuty) block = "等待其他隊員退出任務。";
        else if (busy || !hud->IsMainCommandEnabled(33)) block = "等待角色可登記任務。";

        var queue = &finder->QueueInfo;
        var phase = (QueuePhase)(int)queue->QueueState;
        var popped = queue->PoppedQueueEntry;
        uint poppedId = popped.ContentType == ContentsType.Regular ? popped.Id : 0;
        var addon = gui.GetAddonByName<AddonContentsFinderConfirm>("ContentsFinderConfirm");
        bool canCommence = addon != null && addon->AtkUnitBase.IsVisible &&
            addon->CommenceButton != null && addon->CommenceButton->AtkComponentBase.OwnerNode != null &&
            addon->CommenceButton->IsEnabled && !transition && !busy;
        bool canLeave = inDuty && loop.Phase == LoopPhase.Completed && !busy &&
            EventFramework.Instance() != null && EventFramework.CanLeaveCurrentContent();
        return new() {
            LoggedIn = client.IsLoggedIn, Ready = ready, Transitioning = transition,
            CharacterId = player.ContentId, JobId = player.ClassJob.RowId,
            InDuty = inDuty, DutyId = currentDuty, DutyIsGuildhest = guildhests.Contains(currentDuty),
            Queue = phase, QueueMatches = selected != null && QueueMatches(queue, selected.Target),
            PoppedDutyId = poppedId, PoppedIsGuildhest = guildhests.Contains(poppedId),
            CanQueue = !inDuty && !fatal && block.Length == 0, QueueBlockReason = block,
            FatalQueueBlock = fatal, CanCommence = canCommence, CanLeave = canLeave
        };
    }

    private unsafe bool QueueMatches(ContentsFinderQueueInfo* queue, DutyTarget target)
    {
        int count = 0;
        foreach (var entry in queue->QueuedEntries)
        {
            if (entry.Id == 0) continue;
            count++;
            if (target.Roulette)
            {
                if (entry.ContentType == ContentsType.Roulette && entry.Id == target.Id) continue;
                if (entry.ContentType == ContentsType.Regular && guildhests.Contains(entry.Id)) continue;
                return false;
            }
            if (entry.ContentType != ContentsType.Regular || entry.Id != target.Id) return false;
        }
        return target.Roulette ? queue->QueuedContentRouletteId == target.Id :
            count == 1 && queue->QueuedContentRouletteId == 0;
    }

    private unsafe void Execute(LoopAction action)
    {
        if (!loop.Running || loop.Target == null) return;
        var option = options.FirstOrDefault(o => o.Target == loop.Target);
        var state = Observe(option);
        var finder = ContentsFinder.Instance();
        if (finder == null || !state.Ready || state.Transitioning)
            throw new InvalidOperationException("The game changed state before an action.");
        switch (action)
        {
            case LoopAction.Register:
                if (!state.CanQueue || state.InDuty || state.Queue != QueuePhase.None || !loop.Target.IsValid)
                    throw new InvalidOperationException("Registration conditions changed.");
                if (loop.Target.Roulette) finder->QueueInfo.QueueRoulette(checked((byte)loop.Target.Id));
                else { uint id = loop.Target.Id; finder->QueueInfo.QueueDuties(&id, 1); }
                log.Information("Guildhest Queue registered {Kind} {DutyId}.",
                    loop.Target.Roulette ? "roulette" : "guildhest", loop.Target.Id);
                break;
            case LoopAction.Commence:
                if (state.Queue != QueuePhase.Ready || !state.QueueMatches || !state.CanCommence ||
                    !loop.Target.Matches(state.PoppedDutyId, state.PoppedIsGuildhest))
                    throw new InvalidOperationException("Duty confirmation no longer matches this session.");
                var addon = gui.GetAddonByName<AddonContentsFinderConfirm>("ContentsFinderConfirm");
                if (addon == null) throw new InvalidOperationException("Duty confirmation is not available.");
                AtkValue value = default;
                value.SetInt(8);
                addon->AtkUnitBase.FireCallback(1, &value, true);
                break;
            case LoopAction.Leave:
                // Completion is recorded by IDutyState, not inferred from combat, elapsed time, or UI text.
                if (loop.Phase != LoopPhase.Leaving || !state.InDuty ||
                    !loop.Target.Matches(state.DutyId, state.DutyIsGuildhest) ||
                    EventFramework.Instance() == null || !EventFramework.CanLeaveCurrentContent())
                    throw new InvalidOperationException("Completed duty is not ready to leave.");
                EventFramework.LeaveCurrentContent();
                break;
        }
    }

    private void Draw()
    {
        if (!visible || disposed) return;
        ImGui.SetNextWindowSize(new Vector2(540, 440), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("公會令自動排隊 / Guildhest Queue", ref visible))
        {
            ImGui.TextWrapped("選擇公會令，按開始後自動排隊與確認進場。戰鬥由你操作。");
            ImGui.BeginDisabled(loop.Running);
            var selected = Selected();
            if (ImGui.BeginCombo("公會令", selected?.Name ?? "請選擇"))
            {
                ImGui.InputTextWithHint("##search", "搜尋公會令", ref search, 128);
                foreach (var option in options.Where(o => search.Length == 0 ||
                    o.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    if (ImGui.Selectable($"{option.Name}  Lv.{option.Level}##{option.Target.Roulette}:{option.Target.Id}",
                        option == selected))
                    {
                        config.DutyId = option.Target.Id; config.Roulette = option.Target.Roulette; Save();
                    }
                }
                ImGui.EndCombo();
            }
            var max = config.MaxRuns;
            if (ImGui.InputInt("完成次數（0＝持續）", ref max)) { config.MaxRuns = Math.Clamp(max, 0, 99999); Save(); }
            var autoEnter = config.AutoEnter;
            if (ImGui.Checkbox("配對成功後自動確認進場", ref autoEnter)) { config.AutoEnter = autoEnter; Save(); }
            var autoLeave = config.AutoLeave;
            if (ImGui.Checkbox("完成公會令後自動退場", ref autoLeave)) { config.AutoLeave = autoLeave; Save(); }
            if (config.AutoLeave)
            {
                var delay = config.ExitDelaySeconds;
                if (ImGui.SliderInt("完成後等待（秒）", ref delay, 0, 60)) { config.ExitDelaySeconds = delay; Save(); }
            }
            var repeat = config.RepeatDelaySeconds;
            if (ImGui.SliderInt("退場後再排間隔（秒）", ref repeat, 2, 60)) { config.RepeatDelaySeconds = repeat; Save(); }
            ImGui.EndDisabled();
            ImGui.Separator();
            if (!loop.Running)
            {
                if (ImGui.Button("開始循環")) Interlocked.Exchange(ref requested, 1);
            }
            else if (ImGui.Button("停止循環")) Interlocked.Exchange(ref requested, 2);
            ImGui.SameLine();
            ImGui.TextUnformatted($"已完成：{loop.CompletedRuns} 次");
            ImGui.TextWrapped(selectionError.Length > 0 ? selectionError : loop.Status);
            ImGui.TextWrapped("停止後不再自動操作；現有排隊可在遊戲內取消。");
            if (ImGui.CollapsingHeader("狀態資訊"))
            {
                ImGui.TextUnformatted($"階段：{loop.Phase} / Queue：{last.Queue}");
                ImGui.TextUnformatted($"Duty：{last.DutyId} / Pop：{last.PoppedDutyId} / Matches：{last.QueueMatches}");
                ImGui.TextWrapped(last.QueueBlockReason);
            }
        }
        ImGui.End();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        loop.Stop();
        framework.Update -= Tick;
        duties.DutyCompleted -= OnCompleted;
        pi.UiBuilder.Draw -= Draw;
        pi.UiBuilder.OpenConfigUi -= Open;
        pi.UiBuilder.OpenMainUi -= Open;
        commands.RemoveHandler("/ghq");
    }
}
