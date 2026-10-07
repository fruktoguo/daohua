using Domain.Common;
using Domain.Economy;
using Domain.Player;
using Domain.Simulation;
using SimulationEventTail = DaoHua.Ecs.SimulationEngine.SimulationEventTail;

namespace DaoHua.Ecs.Systems;

/// <summary>
/// 新手引导里程碑推进器（总纲 §22）：纯静态规则，内嵌在行动轮结算、旅行到达与战斗
/// 波次清空三个边界内调用；步骤权威存于 settings 分段（<see cref="PlayerSettingsState.TutorialStep"/>），
/// 赠送与终奖经同一结算窗口的资产增量通道入账，与行动产出共享事务和差量推送。
/// 步骤单调递增、不可回退，因此每次推进天然幂等。
/// </summary>
internal static class TutorialRules
{
    // 引导目标锚点（配表 ID，与前端步骤配置同源；改动需同步 docs/design/总纲/22 与 frontend 步骤表）
    private static readonly ActionId GatherAction = new("explore.gather-ningxue-grass");
    private static readonly ActionId AlchemyAction = new("alchemy.refine-qi-pill");
    private static readonly ActionId ForgeAction = new("forge.qingfeng-sword");
    private static readonly ActionId CultivateAction = new("cultivation.draw-qi");
    private static readonly ActionId CombatAction = new("combat.hunt-qi-beast");
    private const string PeakNodeId = "node.cloud-peak";
    private const string VillageNodeId = "node.yunxi-village";

    /// <summary>进入炼丹步时赠送的炼丹材料（引气丹单轮输入）。</summary>
    private static readonly (ItemId ItemId, double Quantity)[] AlchemyGrant =
        [(new ItemId("item.ningxue-grass"), 3d)];
    /// <summary>进入炼器步时赠送的炼器材料（青锋剑单轮输入）。</summary>
    private static readonly (ItemId ItemId, double Quantity)[] ForgeGrant =
        [(new ItemId("item.refined-iron"), 7d), (new ItemId("item.beast-bone"), 1d)];
    /// <summary>引导完成奖励（功德 ×1000 + 灵石 ×10000）。</summary>
    private static readonly (ItemId ItemId, double Quantity)[] CompletionReward =
        [(new ItemId("item.gongde"), 1000d), (new ItemId("currency.spirit-stone"), 10000d)];

    /// <summary>
    /// 行动型里程碑结算点：计时行动单轮完成（action.completed）与讨伐行动波次清空（combat.won）共用。
    /// 只认当前步骤对应的行动 ID；其他行动的完成不改变引导状态。
    /// </summary>
    /// <returns>是否发生了里程碑推进。推进后调用方须把该行动 RemainingRepeats 置 0 并以
    /// loopable=false 结算——引导期的行动默认无限循环会驻留队首，不截断则后续阶段行动永
    /// 远轮不到执行（师门验收即收工）。</returns>
    internal static bool OnActionRoundCompleted(IPlayerResourceContext player, ActionId actionId,
        DateTimeOffset at, ref Dictionary<AssetKey, double>? deltas, ref SimulationEventTail? events)
    {
        var next = player.Settings.TutorialStep switch
        {
            TutorialSteps.Gather when actionId == GatherAction => TutorialSteps.Alchemy,
            TutorialSteps.Alchemy when actionId == AlchemyAction => TutorialSteps.Forge,
            TutorialSteps.Forge when actionId == ForgeAction => TutorialSteps.TravelToPeak,
            TutorialSteps.Cultivate when actionId == CultivateAction => TutorialSteps.TravelToVillage,
            TutorialSteps.Combat when actionId == CombatAction => TutorialSteps.Done,
            _ => -1
        };
        if (next < 0) return false;
        Advance(player, next, at, ref deltas, ref events);
        return true;
    }

    /// <summary>
    /// 旅行到达结算点（world.arrived 之后调用）：驻留型里程碑只认当前驻留节点。
    /// 旅行步不产生赠送，不触碰资产增量通道。
    /// </summary>
    internal static void OnTravelArrived(IPlayerResourceContext player, DateTimeOffset at,
        ref SimulationEventTail? events)
    {
        Dictionary<AssetKey, double>? deltas = null;
        TrySatisfyResidenceStep(player, at, ref deltas, ref events);
    }

    /// <summary>推进到下一里程碑：写步骤、标脏分段、按奖励游标入账本步赠送，然后重估驻留型步骤。</summary>
    private static void Advance(IPlayerResourceContext player, int nextStep, DateTimeOffset at,
        ref Dictionary<AssetKey, double>? deltas, ref SimulationEventTail? events)
    {
        player.Settings.TutorialStep = nextStep;
        player.MarkDirty(PlayerSectionKind.Settings);
        // 奖励游标：只发未领过的里程碑赠送——重学（restart）回到低步骤时游标更高，不再重复发放。
        if (nextStep > player.Settings.TutorialRewardsStep && GrantsFor(nextStep) is { } grants)
        {
            player.Settings.TutorialRewardsStep = nextStep;
            var items = new Dictionary<ItemId, double>(grants.Length);
            foreach (var (itemId, quantity) in grants)
            {
                ActionExecutionRules.AddDelta(deltas ??= new Dictionary<AssetKey, double>(), itemId, quantity);
                items[itemId] = quantity;
            }
            events ??= new SimulationEventTail();
            events.Add(new SimulationEvent(
                at,
                nextStep == TutorialSteps.Done ? "tutorial.completed" : "tutorial.grant",
                nextStep == TutorialSteps.Done ? "新手引导全部完成，师门赠礼" : "新手引导赠予材料",
                null,
                items));
        }
        TrySatisfyResidenceStep(player, at, ref deltas, ref events);
    }

    /// <summary>
    /// 驻留型步骤重估：激活旅行步时玩家已驻留目标节点（先旅行后补步骤的交错）立即续推进；
    /// 也作为旅行到达事件的判定入口。
    /// </summary>
    private static void TrySatisfyResidenceStep(IPlayerResourceContext player, DateTimeOffset at,
        ref Dictionary<AssetKey, double>? deltas, ref SimulationEventTail? events)
    {
        var step = player.Settings.TutorialStep;
        var target = step switch
        {
            TutorialSteps.TravelToPeak => PeakNodeId,
            TutorialSteps.TravelToVillage => VillageNodeId,
            _ => null
        };
        // 旅行步两两目标互异，不存在连续驻留链，单次判定即可。
        if (target is null || player.World.NodeId != target) return;
        Advance(player, step + 1, at, ref deltas, ref events);
    }

    /// <summary>进入指定里程碑时入账的赠送/奖励；无赠送步骤返回 null。</summary>
    private static (ItemId ItemId, double Quantity)[]? GrantsFor(int step) => step switch
    {
        TutorialSteps.Alchemy => AlchemyGrant,
        TutorialSteps.Forge => ForgeGrant,
        TutorialSteps.Done => CompletionReward,
        _ => null
    };

    /// <summary>
    /// 跳过引导的补发清单：游标+1..Done 之间所有未领赠送/终奖按物品合并。
    /// 必须在 SkipTutorial 推进游标之前调用；已达终态返回空表（幂等）。
    /// </summary>
    internal static IReadOnlyDictionary<ItemId, double> PendingSkipGrants(PlayerSettingsState settings)
    {
        var merged = new Dictionary<ItemId, double>();
        if (settings.TutorialStep >= TutorialSteps.Done) return merged;
        for (var step = settings.TutorialRewardsStep + 1; step <= TutorialSteps.Done; step++)
        {
            if (GrantsFor(step) is not { } grants) continue;
            foreach (var (itemId, quantity) in grants)
                merged[itemId] = merged.GetValueOrDefault(itemId) + quantity;
        }
        return merged;
    }
}
