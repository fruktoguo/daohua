using System.Text.Json;
using DaoHua.Simulator;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Protocol.Simulator;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 战斗预演 profile 协议边界回归：capture→JSON→restore 白名单往返必须保住
/// double 原精度、条件三态（缺失键/空数组/有内容）与全部时钟字段，且绝不携带账号/行动队列等越界数据。
/// </summary>
public sealed class CombatSimulatorProfileTests
{
    private const double CapturedAt = 1_790_841_600.5;
    private static readonly Guid PlayerIdValue = Guid.Parse("30000000-0000-0000-0000-00000000bb01");

    private static PlayerAggregateState BuildState(GameDataCatalog catalog)
    {
        var baseState = PlayerAggregate.Create(new PlayerId(PlayerIdValue), new AccountId(PlayerIdValue),
            "预演档案", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), catalog.Fingerprint).ExportState();
        var trigger = JsonDocument.Parse("""
            {"dependency":"self","condition":"current-hp","comparator":"less-than","value":50,"buffId":null}
            """).RootElement;
        return baseState with
        {
            Progression = baseState.Progression with
            {
                RealmLevel = 20,
                Experience = 12000.25,
                Foundation = 800.5,
                CombatExperience = 50000.125,
                LifetimeExperience = 20000,
                AgeSeconds = 3600.75,
                TraitIds = ["trait.chujian", "trait.dantiaowang"],
                UnlockedTraits =
                [
                    new TraitProgressState("trait.chujian", 3, 2),
                    new TraitProgressState("trait.dantiaowang", 2, 1)
                ],
                HeavenGateConfirmed = new LinggenValues(10, 20, 30, 5, 15),
                HeavenGateEntered = true
            },
            Combat = baseState.Combat with
            {
                Hitpoints = 220.25,
                MaxHitpoints = 400,
                Manapoints = 36.5,
                MaxManapoints = 90,
                RegenCarrySeconds = 1.5,
                Skills = new PlayerCombatSkillState(
                    [new CombatSkillCooldown("ability.heavy-slash", 3.5)], "ability.heavy-slash", 1.2, 4),
                Buffs = new PlayerCombatBuffState(
                    [new CombatBuffCarry("buff.zhixue", 12.5, 2, "止血散",
                        0.5, [new BuffEffect(BuffEffectChannel.HpRegen, 36.25)])])
            },
            Buffs =
            [
                new ActiveBuff("buff.zhixue", CapturedAt + 300.25, 1, "止血散增益", "item.zhixue-powder",
                    [new BuffEffect(BuffEffectChannel.HpRegen, 36.25)],
                    new Dictionary<string, double> { ["armor"] = 0.05 })
            ],
            Loadout = new PlayerLoadoutDocument(
                new PlayerCoreAttributes(5000.25, 3000, 4000, 2000, 1000, 800),
                [
                    new PlayerAbilitySlot("ability.tuna-gong", 8),
                    new PlayerAbilitySlot("ability.heavy-slash", 5),
                    new PlayerAbilitySlot(null, 1),
                    new PlayerAbilitySlot(null, 1)
                ],
                [
                    new PlayerEquipmentSlot("equipment.qingfeng-sword", 3),
                    new PlayerEquipmentSlot("equipment.shufa-scarf", 2)
                ],
                new Dictionary<string, double>
                {
                    ["ability.tuna-gong"] = 8000.5,
                    ["ability.heavy-slash"] = 2000.25
                },
                null,
                new Dictionary<string, JsonElement[]> { ["ability.heavy-slash"] = [] },
                new Dictionary<string, JsonElement[]> { ["item.zhixue-powder"] = [trigger.Clone()] }),
            Settings = baseState.Settings with
            {
                AutoUseSlots =
                [
                    new AutoUseSlotState(AutoUseBar.Combat, 0, "item.zhixue-powder", 3, true,
                        7.5, CapturedAt + 12.25, AutoUseSlotState.SlotGroupPotion)
                ]
            }
        };
    }

    [Fact]
    public void Capture_JsonRoundTrip_PreservesDoublesAndTriggerTriState()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var state = BuildState(catalog);
        var assets = new[]
        {
            new SimulatorAssetDto("item.zhixue-powder", "", 3.5),
            new SimulatorAssetDto("item.qingdan", "frost", 1)
        };
        var profile = SimulatorProfileMapper.Capture(state, assets, catalog);
        var json = JsonSerializer.Serialize(profile, SimulatorJson.Options);
        var roundTripped = JsonSerializer.Deserialize<SimulatorProfileDto>(json, SimulatorJson.Options);
        Assert.NotNull(roundTripped);

        // double 原精度：经验/carry/时钟/效果值不允许任何舍入。
        Assert.Equal(12000.25, roundTripped!.Experience);
        Assert.Equal(50000.125, roundTripped.CombatExperience);
        Assert.Equal(1.5, roundTripped.RegenCarrySeconds);
        Assert.Equal(220.25, roundTripped.Hitpoints);
        Assert.Equal(8000.5, roundTripped.AbilityExperiences!["ability.tuna-gong"]);
        Assert.Equal(12.5, roundTripped.CombatBuffCarry!.Single().RemainingSeconds);
        Assert.Equal(36.25, roundTripped.Buffs!.Single().Effects!.Single().Value);
        Assert.Equal(7.5, roundTripped.CombatAutoUseSlots!.Single().RemainingDurationSeconds);
        Assert.Equal(CapturedAt + 12.25, roundTripped.CombatAutoUseSlots!.Single().CooldownUntilUnixSeconds);

        // 条件三态：空数组键保留，缺失键不补，对象数组逐项仍是 Object。
        Assert.True(roundTripped.AbilityTriggers!.ContainsKey("ability.heavy-slash"));
        Assert.Empty(roundTripped.AbilityTriggers["ability.heavy-slash"]);
        Assert.False(roundTripped.AbilityTriggers.ContainsKey("ability.tuna-gong"));
        var consumableTrigger = Assert.Single(roundTripped.ConsumableTriggers!["item.zhixue-powder"]);
        Assert.Equal(JsonValueKind.Object, consumableTrigger.ValueKind);
        Assert.Equal("current-hp", consumableTrigger.GetProperty("condition").GetString());

        // 资产 variant 与数量原样往返。
        var variantAsset = Assert.Single(roundTripped.Assets!, asset => asset.Variant == "frost");
        Assert.Equal("item.qingdan", variantAsset.ItemId);
        Assert.Equal(3.5, roundTripped.Assets.Single(asset => asset.ItemId == "item.zhixue-powder").Quantity);
    }

    [Fact]
    public void Capture_WhitelistJson_ExcludesAccountAndRuntimeSurfaces()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var profile = SimulatorProfileMapper.Capture(BuildState(catalog),
            [new SimulatorAssetDto("item.zhixue-powder", "", 1)], catalog);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(profile, SimulatorJson.Options));
        var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        foreach (var forbidden in new[] { "accountId", "actions", "actionQueue", "duel", "duelHistory", "password", "email" })
            Assert.DoesNotContain(forbidden, propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(PlayerIdValue.ToString("D"), profile.SourcePlayerId);
    }

    [Fact]
    public void Capture_DoesNotShareMutableCollectionsWithSourceState()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var state = BuildState(catalog);
        var profile = SimulatorProfileMapper.Capture(state,
            [new SimulatorAssetDto("item.zhixue-powder", "", 1)], catalog);
        Assert.False(ReferenceEquals(profile.AbilityTriggers, state.Loadout.AbilityTriggers));
        Assert.False(ReferenceEquals(profile.ConsumableTriggers, state.Loadout.ConsumableTriggers));
        Assert.False(ReferenceEquals(profile.AbilityExperiences, state.Loadout.AbilityExperiences));
        if (profile.AbilityTriggers is Dictionary<string, JsonElement[]> mutable)
        {
            mutable["injected"] = [];
            Assert.False(state.Loadout.AbilityTriggers!.ContainsKey("injected"));
        }
    }
}
