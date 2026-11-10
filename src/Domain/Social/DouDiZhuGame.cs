using System.Security.Cryptography;
using Domain.Common;

namespace Domain.Social;

/// <summary>斗地主牌局阶段。</summary>
public enum DouDiZhuGamePhase : byte
{
    /// <summary>叫地主与抢地主阶段。</summary>
    Bidding,
    /// <summary>地主与农民轮流出牌。</summary>
    Playing,
    /// <summary>本局已结算。</summary>
    Finished,
    /// <summary>本局已取消且未产生输赢。</summary>
    Cancelled
}

/// <summary>叫地主阶段的玩家选择。</summary>
public enum DouDiZhuBidAction : byte
{
    /// <summary>不叫或不抢。</summary>
    Pass,
    /// <summary>首次叫地主。</summary>
    Call,
    /// <summary>抢夺当前地主位。</summary>
    Rob
}

/// <summary>牌桌中一条公开出牌或过牌记录。</summary>
/// <param name="Seat">执行操作的固定座位。</param>
/// <param name="Cards">公开出的牌张编号；过牌时为空数组。</param>
/// <param name="IsPass">是否为不出。</param>
/// <param name="Version">此操作提交后的牌局版本。</param>
public sealed record DouDiZhuPlayRecord(int Seat, int[] Cards, bool IsPass, long Version);

/// <summary>公开叫抢记录；用于回放操作反馈，不含私有手牌。</summary>
public sealed record DouDiZhuBidRecord(int Seat, string Action, long Version);

/// <summary>可直接序列化到牌局检查点的完整权威状态。</summary>
public sealed record DouDiZhuGameState
{
    /// <summary>牌局唯一标识。</summary>
    public Guid GameId { get; init; }
    /// <summary>所属队伍标识。</summary>
    public Guid PartyId { get; init; }
    /// <summary>开局时冻结的三名玩家，数组下标即座位号。</summary>
    public Guid[] PlayerIds { get; init; } = [];
    /// <summary>开局冻结的灵石底注。</summary>
    public double BaseStake { get; init; }
    /// <summary>当前洗牌后的牌序，用于持久化后恢复。</summary>
    public int[] Deck { get; init; } = [];
    /// <summary>各座位当前手牌。</summary>
    public int[][] Hands { get; init; } = [[], [], []];
    /// <summary>各座位是否已开启托管。</summary>
    public bool[] HostedSeats { get; init; } = [false, false, false];
    /// <summary>当前操作回合的服务端截止时刻。</summary>
    public DateTimeOffset TurnDeadlineAt { get; init; }
    /// <summary>当前操作开放时刻，动画期间不消耗操作时限。</summary>
    public DateTimeOffset ActionAvailableAt { get; init; }
    /// <summary>本次洗发牌开始时刻，重连按此恢复表现进度。</summary>
    public DateTimeOffset DealStartedAt { get; init; }
    /// <summary>定地主时刻，供三张底牌连续派发。</summary>
    public DateTimeOffset? LandlordConfirmedAt { get; init; }
    /// <summary>本轮初叫地主者。</summary>
    public int? CallerSeat { get; init; }
    /// <summary>初叫者是否已经获得最终抢回机会。</summary>
    public bool RobReturn { get; init; }
    /// <summary>本次发牌中的公开叫抢记录。</summary>
    public DouDiZhuBidRecord[] BidHistory { get; init; } = [];
    /// <summary>尚未归地主的底牌。</summary>
    public int[] BottomCards { get; init; } = [];
    /// <summary>当前叫/抢地主或出牌操作座位。</summary>
    public int CurrentSeat { get; init; }
    /// <summary>当前牌局阶段。</summary>
    public DouDiZhuGamePhase Phase { get; init; }
    /// <summary>当前地主座位；尚无人叫地主时为空。</summary>
    public int? LandlordSeat { get; init; }
    /// <summary>叫地主阶段剩余操作次数。</summary>
    public int BidsRemaining { get; init; }
    /// <summary>本次发牌是否已有玩家叫地主。</summary>
    public bool HasCalled { get; init; }
    /// <summary>成功抢地主的次数。</summary>
    public int RobCount { get; init; }
    /// <summary>本局炸弹或王炸的出牌数。</summary>
    public int BombCount { get; init; }
    /// <summary>本局农民出牌手数，用于春天判定。</summary>
    public int FarmerPlayCount { get; init; }
    /// <summary>本局地主出牌手数，用于反春判定。</summary>
    public int LandlordPlayCount { get; init; }
    /// <summary>上一手非过牌的玩家座位。</summary>
    public int? LastPlayedSeat { get; init; }
    /// <summary>当前桌面待压制的牌型；自由领出时为空。</summary>
    public DouDiZhuPattern? LastPattern { get; init; }
    /// <summary>当前桌面待压制的牌张。</summary>
    public int[] CurrentTrickCards { get; init; } = [];
    /// <summary>当前轮连续不出次数。</summary>
    public int ConsecutivePasses { get; init; }
    /// <summary>全部公开出牌历史。</summary>
    public DouDiZhuPlayRecord[] History { get; init; } = [];
    /// <summary>牌局版本；每次有效操作递增。</summary>
    public long Version { get; init; }
    /// <summary>重新发牌次数。</summary>
    public int DealNumber { get; init; }
    /// <summary>终局获胜座位。</summary>
    public int? WinnerSeat { get; init; }
    /// <summary>地主方是否获胜。</summary>
    public bool? LandlordWon { get; init; }
    /// <summary>终局是否满足春天或反春。</summary>
    public bool Spring { get; init; }
    /// <summary>终局灵石结算倍率。</summary>
    public int FinalMultiplier { get; init; }
    /// <summary>三个座位的最终灵石净变动；牌局结束后由经济事务写入。</summary>
    public double[] SettlementDeltas { get; init; } = [0, 0, 0];
    /// <summary>牌局创建或最后一次有效操作的 UTC 时刻。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>斗地主发牌与回合状态转移。</summary>
public static class DouDiZhuGame
{
    /// <summary>叫牌或出牌的默认操作时限。</summary>
    public static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(30);
    /// <summary>托管每次只思考并执行一步，禁止一次推进完整牌局。</summary>
    public static readonly TimeSpan HostedDelay = TimeSpan.FromSeconds(1.1);
    private static readonly TimeSpan DealDuration = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan PlayDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan RevealDuration = TimeSpan.FromSeconds(2.4);

    /// <summary>创建一局牌局并完成首轮洗牌发牌。</summary>
    public static DouDiZhuGameState Create(Guid gameId, Guid partyId, IReadOnlyList<Guid> playerIds,
        double baseStake, DateTimeOffset now)
    {
        ValidateStart(gameId, partyId, playerIds, baseStake);
        var deck = ShuffleDeck();
        var firstBidder = RandomNumberGenerator.GetInt32(3);
        return Deal(gameId, partyId, [.. playerIds], baseStake, deck, firstBidder, now, 0);
    }

    /// <summary>按牌序创建牌局，仅供确定性测试使用。</summary>
    internal static DouDiZhuGameState CreateWithDeck(Guid gameId, Guid partyId, IReadOnlyList<Guid> playerIds,
        double baseStake, IReadOnlyList<int> deck, int firstBidder, DateTimeOffset now)
    {
        ValidateStart(gameId, partyId, playerIds, baseStake);
        ValidateDeck(deck);
        if (firstBidder is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(firstBidder));
        return Deal(gameId, partyId, [.. playerIds], baseStake, [.. deck], firstBidder, now, 0);
    }

    /// <summary>应用当前座位的一次叫地主、不叫、抢地主或不抢操作。</summary>
    public static DouDiZhuGameState ApplyBid(DouDiZhuGameState state, Guid playerId,
        long expectedVersion, DouDiZhuBidAction action, DateTimeOffset now)
    {
        RequireVersion(state, expectedVersion);
        if (state.Phase != DouDiZhuGamePhase.Bidding) throw Invalid("当前不在叫地主阶段。");
        var seat = SeatOf(state, playerId);
        RequireAvailable(state, now);
        if (seat != state.CurrentSeat) throw Invalid("尚未轮到该玩家叫地主。");
        if (action is not (DouDiZhuBidAction.Pass or DouDiZhuBidAction.Call or DouDiZhuBidAction.Rob))
            throw Invalid("叫地主操作无效。");
        if (state.HasCalled ? action == DouDiZhuBidAction.Call : action == DouDiZhuBidAction.Rob)
            throw Invalid("叫地主阶段操作与当前状态不符。");

        var landlordSeat = state.LandlordSeat;
        var robCount = state.RobCount;
        var hasCalled = state.HasCalled;
        if (action == DouDiZhuBidAction.Call)
        {
            landlordSeat = seat;
            hasCalled = true;
        }
        else if (action == DouDiZhuBidAction.Rob)
        {
            landlordSeat = seat;
            robCount++;
        }

        // 一旦有人叫地主，另外两人都应各有一次抢地主或放弃机会，包含此前已不叫的人。
        var remaining = action == DouDiZhuBidAction.Call ? 2 : state.BidsRemaining - 1;
        var callerSeat = action == DouDiZhuBidAction.Call ? seat : state.CallerSeat;
        var robReturn = state.RobReturn;
        if (remaining == 0 && robCount > 0 && landlordSeat != callerSeat && !robReturn)
        {
            remaining = 1;
            robReturn = true;
        }
        var version = state.Version + 1;
        var updated = state with
        {
            LandlordSeat = landlordSeat,
            HasCalled = hasCalled,
            RobCount = robCount,
            BidsRemaining = remaining,
            CurrentSeat = robReturn && !state.RobReturn ? callerSeat!.Value : (seat + 1) % 3,
            CallerSeat = callerSeat,
            RobReturn = robReturn,
            BidHistory = [.. state.BidHistory, new DouDiZhuBidRecord(seat, action.ToString().ToLowerInvariant(), version)],
            Version = version,
            ActionAvailableAt = now + PlayDelay,
            TurnDeadlineAt = now + PlayDelay + TurnTimeout,
            UpdatedAt = now
        };

        if (remaining > 0) return updated;
        if (landlordSeat is null)
        {
            var deck = ShuffleDeck();
            return Deal(state.GameId, state.PartyId, state.PlayerIds, state.BaseStake, deck,
                RandomNumberGenerator.GetInt32(3), now, state.DealNumber + 1) with
            {
                Version = version,
                HostedSeats = state.HostedSeats
            };
        }

        var hands = updated.Hands.Select(hand => hand.ToArray()).ToArray();
        hands[landlordSeat.Value] = SortCards([.. hands[landlordSeat.Value], .. updated.BottomCards]);
        return updated with
        {
            Phase = DouDiZhuGamePhase.Playing,
            Hands = hands,
            CurrentSeat = landlordSeat.Value,
            LandlordConfirmedAt = now,
            ActionAvailableAt = now + RevealDuration,
            TurnDeadlineAt = now + RevealDuration + TurnTimeout
        };
    }

    /// <summary>应用当前座位的一次出牌操作；牌张验证失败时不改变原状态。</summary>
    public static DouDiZhuGameState ApplyPlay(DouDiZhuGameState state, Guid playerId,
        long expectedVersion, IReadOnlyCollection<int> cardIds, DateTimeOffset now)
    {
        RequireTurn(state, playerId, expectedVersion);
        RequireAvailable(state, now);
        if (cardIds.Count == 0) throw Invalid("请选择要出的牌。");
        if (!DouDiZhuRules.TryEvaluate(cardIds, out var pattern)) throw Invalid("所选牌不符合斗地主牌型。");
        if (state.LastPattern is { } current && !DouDiZhuRules.CanBeat(pattern, current))
            throw Invalid("所选牌不能压过桌面牌型。");

        var hand = state.Hands[state.CurrentSeat].ToHashSet();
        if (cardIds.Any(card => !hand.Remove(card))) throw Invalid("所选牌不在本人手牌中。");

        var seat = state.CurrentSeat;
        var cards = SortCards([.. cardIds]);
        var hands = state.Hands.Select(value => value.ToArray()).ToArray();
        hands[seat] = SortCards([.. hand]);
        var landlordSeat = state.LandlordSeat!.Value;
        var landlordPlayCount = state.LandlordPlayCount + (seat == landlordSeat ? 1 : 0);
        var farmerPlayCount = state.FarmerPlayCount + (seat != landlordSeat ? 1 : 0);
        var bombCount = state.BombCount + (pattern.Kind is DouDiZhuPatternKind.Bomb or DouDiZhuPatternKind.Rocket ? 1 : 0);
        var version = state.Version + 1;
        var updated = state with
        {
            Hands = hands,
            LastPlayedSeat = seat,
            LastPattern = pattern,
            CurrentTrickCards = cards,
            ConsecutivePasses = 0,
            CurrentSeat = (seat + 1) % 3,
            LandlordPlayCount = landlordPlayCount,
            FarmerPlayCount = farmerPlayCount,
            BombCount = bombCount,
            Version = version,
            ActionAvailableAt = now + PlayDelay,
            TurnDeadlineAt = now + PlayDelay + TurnTimeout,
            History = [.. state.History, new DouDiZhuPlayRecord(seat, cards, false, version)],
            UpdatedAt = now
        };
        if (hands[seat].Length != 0) return updated;

        var landlordWon = seat == landlordSeat;
        var spring = DouDiZhuRules.IsSpring(landlordWon, farmerPlayCount, landlordPlayCount);
        return updated with
        {
            Phase = DouDiZhuGamePhase.Finished,
            WinnerSeat = seat,
            LandlordWon = landlordWon,
            Spring = spring,
            FinalMultiplier = DouDiZhuRules.CalculateMultiplier(updated.RobCount, bombCount, spring)
        };
    }

    /// <summary>应用当前座位的一次不出操作；自由领出时不能不出。</summary>
    public static DouDiZhuGameState ApplyPass(DouDiZhuGameState state, Guid playerId,
        long expectedVersion, DateTimeOffset now)
    {
        RequireTurn(state, playerId, expectedVersion);
        RequireAvailable(state, now);
        if (state.LastPattern is null || state.LastPlayedSeat == state.CurrentSeat)
            throw Invalid("当前可自由领出，不能不出。");

        var seat = state.CurrentSeat;
        var passes = state.ConsecutivePasses + 1;
        var version = state.Version + 1;
        var resetTrick = passes == 2;
        return state with
        {
            CurrentSeat = resetTrick ? state.LastPlayedSeat!.Value : (seat + 1) % 3,
            LastPattern = resetTrick ? null : state.LastPattern,
            CurrentTrickCards = resetTrick ? [] : state.CurrentTrickCards,
            ConsecutivePasses = resetTrick ? 0 : passes,
            Version = version,
            ActionAvailableAt = now + PlayDelay,
            TurnDeadlineAt = now + PlayDelay + TurnTimeout,
            History = [.. state.History, new DouDiZhuPlayRecord(seat, [], true, version)],
            UpdatedAt = now
        };
    }

    /// <summary>切换本人托管；取消当前回合托管时重新给予完整操作时限。</summary>
    public static DouDiZhuGameState SetHosted(DouDiZhuGameState state, Guid playerId,
        long expectedVersion, bool hosted, DateTimeOffset now)
    {
        RequireVersion(state, expectedVersion);
        RequireActive(state);
        var seat = SeatOf(state, playerId);
        if (state.HostedSeats.Length != 3) throw Invalid("托管座位状态无效。");
        if (state.HostedSeats[seat] == hosted) throw Invalid("托管状态没有变化。");

        var hostedSeats = state.HostedSeats.ToArray();
        hostedSeats[seat] = hosted;
        return state with
        {
            HostedSeats = hostedSeats,
            ActionAvailableAt = seat == state.CurrentSeat && now > state.ActionAvailableAt ? now : state.ActionAvailableAt,
            TurnDeadlineAt = !hosted && seat == state.CurrentSeat
                ? (now > state.ActionAvailableAt ? now : state.ActionAvailableAt) + TurnTimeout : state.TurnDeadlineAt,
            Version = state.Version + 1,
            UpdatedAt = now
        };
    }

    /// <summary>截止时间到达后进入托管并立刻执行当前座位的一次合法操作。</summary>
    public static DouDiZhuGameState ApplyTimeout(DouDiZhuGameState state, long expectedVersion, DateTimeOffset now)
    {
        RequireVersion(state, expectedVersion);
        RequireActive(state);
        if (state.HostedSeats.Length != 3) throw Invalid("托管座位状态无效。");
        if (now < state.TurnDeadlineAt) throw Invalid("当前回合尚未超时。");

        if (!state.HostedSeats[state.CurrentSeat])
        {
            var hostedSeats = state.HostedSeats.ToArray();
            hostedSeats[state.CurrentSeat] = true;
            state = state with
            {
                HostedSeats = hostedSeats,
                Version = state.Version + 1,
                UpdatedAt = now
            };
        }

        return ApplyHostedTurn(state, state.Version, now);
    }

    /// <summary>执行当前已托管座位的一次标准叫牌或出牌操作。</summary>
    public static DouDiZhuGameState ApplyHostedTurn(DouDiZhuGameState state, long expectedVersion, DateTimeOffset now)
    {
        RequireVersion(state, expectedVersion);
        RequireActive(state);
        if (state.HostedSeats.Length != 3 || !state.HostedSeats[state.CurrentSeat])
            throw Invalid("当前座位尚未开启托管。");

        var playerId = state.PlayerIds[state.CurrentSeat];
        if (state.Phase == DouDiZhuGamePhase.Bidding)
            return ApplyBid(state, playerId, expectedVersion,
                state.LandlordSeat is null ? DouDiZhuBidAction.Call : DouDiZhuBidAction.Pass, now);

        if (state.Hands[state.CurrentSeat].Length == 0) throw Invalid("托管座位没有可出的手牌。");
        var move = DouDiZhuMoveSelector.Choose(state);
        return move is null ? ApplyPass(state, playerId, expectedVersion, now)
            : ApplyPlay(state, playerId, expectedVersion, move, now);
    }

    /// <summary>按终局倍率和当前钱包余额计算不产生负余额的零和灵石变动。</summary>
    public static double[] CalculateSettlementDeltas(DouDiZhuGameState state, IReadOnlyList<double> balances)
    {
        if (state.Phase != DouDiZhuGamePhase.Finished || state.LandlordSeat is not { } landlordSeat
            || state.LandlordWon is not { } landlordWon || balances.Count != 3
            || balances.Any(balance => !double.IsFinite(balance) || balance < 0))
            throw Invalid("斗地主终局与灵石余额状态无效。");

        var deltas = new double[3];
        if (state.BaseStake == 0) return deltas;
        var claim = state.BaseStake * state.FinalMultiplier;
        if (landlordWon)
        {
            foreach (var seat in Enumerable.Range(0, 3).Where(seat => seat != landlordSeat))
            {
                var paid = Math.Min(claim, balances[seat]);
                deltas[seat] = -paid;
                deltas[landlordSeat] += paid;
            }
        }
        else
        {
            var farmers = Enumerable.Range(0, 3).Where(seat => seat != landlordSeat).ToArray();
            var totalOwed = claim * farmers.Length;
            var totalPaid = Math.Min(totalOwed, balances[landlordSeat]);
            var firstPayment = Math.Min(claim, totalPaid / 2);
            var secondPayment = totalPaid - firstPayment;
            deltas[landlordSeat] = -totalPaid;
            deltas[farmers[0]] = firstPayment;
            deltas[farmers[1]] = secondPayment;
        }
        return deltas;
    }

    private static void ValidateStart(Guid gameId, Guid partyId, IReadOnlyList<Guid> playerIds, double baseStake)
    {
        if (gameId == Guid.Empty || partyId == Guid.Empty) throw Invalid("牌局或队伍标识无效。");
        if (playerIds.Count != 3 || playerIds.Any(id => id == Guid.Empty) || playerIds.Distinct().Count() != 3)
            throw Invalid("斗地主必须由三名不同玩家参加。");
        if (!double.IsFinite(baseStake) || baseStake < 0) throw Invalid("底注必须是非负有限数值。");
    }

    private static DouDiZhuGameState Deal(Guid gameId, Guid partyId, Guid[] playerIds, double baseStake,
        int[] deck, int firstBidder, DateTimeOffset now, int dealNumber)
    {
        var hands = new[] { new List<int>(17), new List<int>(17), new List<int>(17) };
        for (var index = 0; index < 51; index++) hands[index % 3].Add(deck[index]);
        return new DouDiZhuGameState
        {
            GameId = gameId,
            PartyId = partyId,
            PlayerIds = playerIds,
            BaseStake = baseStake,
            Deck = deck,
            Hands = hands.Select(hand => SortCards([.. hand])).ToArray(),
            BottomCards = deck[51..54],
            CurrentSeat = firstBidder,
            Phase = DouDiZhuGamePhase.Bidding,
            HostedSeats = [false, false, false],
            BidsRemaining = 3,
            Version = 0,
            DealNumber = dealNumber,
            DealStartedAt = now,
            ActionAvailableAt = now + DealDuration,
            TurnDeadlineAt = now + DealDuration + TurnTimeout,
            UpdatedAt = now
        };
    }

    private static void RequireActive(DouDiZhuGameState state)
    {
        if (state.Phase is not (DouDiZhuGamePhase.Bidding or DouDiZhuGamePhase.Playing))
            throw Invalid("当前牌局已结束。");
    }

    private static void RequireAvailable(DouDiZhuGameState state, DateTimeOffset now)
    {
        if (now < state.ActionAvailableAt) throw Invalid("请等待牌面动画完成。");
    }

    private static int[] ShuffleDeck()
    {
        var deck = Enumerable.Range(0, DouDiZhuRules.DeckSize).ToArray();
        for (var index = deck.Length - 1; index > 0; index--)
        {
            var swap = RandomNumberGenerator.GetInt32(index + 1);
            (deck[index], deck[swap]) = (deck[swap], deck[index]);
        }
        return deck;
    }

    private static void ValidateDeck(IReadOnlyList<int> deck)
    {
        if (deck.Count != DouDiZhuRules.DeckSize || deck.Any(card => card is < 0 or >= DouDiZhuRules.DeckSize)
            || deck.Distinct().Count() != DouDiZhuRules.DeckSize)
            throw Invalid("牌副必须恰好包含 54 张不重复的标准牌。");
    }

    private static int[] SortCards(int[] cards) =>
        [.. cards.OrderBy(DouDiZhuRules.RankOf).ThenBy(card => card)];

    private static int SeatOf(DouDiZhuGameState state, Guid playerId)
    {
        var seat = Array.IndexOf(state.PlayerIds, playerId);
        return seat >= 0 ? seat : throw Invalid("玩家不在本局座位中。");
    }

    private static void RequireTurn(DouDiZhuGameState state, Guid playerId, long expectedVersion)
    {
        RequireVersion(state, expectedVersion);
        if (state.Phase != DouDiZhuGamePhase.Playing) throw Invalid("当前不在出牌阶段。");
        if (SeatOf(state, playerId) != state.CurrentSeat) throw Invalid("尚未轮到该玩家出牌。");
    }

    private static void RequireVersion(DouDiZhuGameState state, long expectedVersion)
    {
        if (state.Version != expectedVersion) throw Invalid("牌局状态已更新，请重新查看牌桌。");
    }

    private static DomainException Invalid(string message) => new(DomainErrorCode.InvalidArgument, message);
}
