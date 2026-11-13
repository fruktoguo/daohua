namespace Domain.Social;

/// <summary>斗地主牌型标识。</summary>
public enum DouDiZhuPatternKind : byte
{
    /// <summary>无法识别的牌型。</summary>
    Invalid,
    /// <summary>单张。</summary>
    SingleCard,
    /// <summary>对子。</summary>
    Pair,
    /// <summary>三张。</summary>
    Triple,
    /// <summary>三带一。</summary>
    TripleWithSingle,
    /// <summary>三带二。</summary>
    TripleWithPair,
    /// <summary>顺子。</summary>
    Straight,
    /// <summary>连对。</summary>
    ConsecutivePairs,
    /// <summary>飞机不带翅膀。</summary>
    Airplane,
    /// <summary>飞机带单牌。</summary>
    AirplaneWithSingles,
    /// <summary>飞机带对子。</summary>
    AirplaneWithPairs,
    /// <summary>四带两张单牌。</summary>
    FourWithTwoSingles,
    /// <summary>四带两对。</summary>
    FourWithTwoPairs,
    /// <summary>炸弹。</summary>
    Bomb,
    /// <summary>王炸。</summary>
    Rocket
}

/// <summary>一手合法斗地主牌型及其比较属性。</summary>
/// <param name="Kind">牌型类别。</param>
/// <param name="PrimaryRank">用于同类比较的主牌点数。</param>
/// <param name="CardCount">本手牌张数。</param>
/// <param name="SequenceLength">顺子、连对或飞机的连续组数。</param>
public readonly record struct DouDiZhuPattern(
    DouDiZhuPatternKind Kind,
    int PrimaryRank,
    int CardCount,
    int SequenceLength = 1);

/// <summary>斗地主牌型识别、比较及倍率规则。</summary>
public static class DouDiZhuRules
{
    /// <summary>完整牌副包含的牌张数。</summary>
    public const int DeckSize = 54;
    /// <summary>单手出牌的最大张数。</summary>
    public const int MaxPlaySize = 20;

    /// <summary>读取牌张的点数：3 至 A 为 3~14，2 为 15，小王为 16，大王为 17。</summary>
    public static int RankOf(int cardId) => cardId switch
    {
        >= 0 and < 52 => cardId / 4 + 3,
        52 => 16,
        53 => 17,
        _ => throw new ArgumentOutOfRangeException(nameof(cardId), "牌张编号必须在 0~53 之间。")
    };

    /// <summary>
    /// 识别一手牌。重复牌张、空牌、超长牌或不符合标准牌型结构时返回 false。
    /// 飞机可不带翅膀、带等量单牌或带等量对子；四带二支持两张单牌或两对。
    /// </summary>
    public static bool TryEvaluate(IReadOnlyCollection<int> cardIds, out DouDiZhuPattern pattern)
    {
        pattern = default;
        if (cardIds.Count is < 1 or > MaxPlaySize) return false;

        var counts = new int[18];
        var seen = new HashSet<int>();
        foreach (var cardId in cardIds)
        {
            if (cardId is < 0 or >= DeckSize || !seen.Add(cardId)) return false;
            counts[RankOf(cardId)]++;
        }

        var groups = counts.Count(count => count != 0);
        var maxRank = HighestRank(counts);
        var count = cardIds.Count;
        if (count == 1) return Set(DouDiZhuPatternKind.SingleCard, maxRank, count, out pattern);
        if (count == 2 && groups == 1) return Set(DouDiZhuPatternKind.Pair, maxRank, count, out pattern);
        if (count == 2 && counts[16] == 1 && counts[17] == 1)
            return Set(DouDiZhuPatternKind.Rocket, 17, count, out pattern);
        if (count == 3 && groups == 1) return Set(DouDiZhuPatternKind.Triple, maxRank, count, out pattern);
        if (count == 4 && groups == 1 && counts[maxRank] == 4)
            return Set(DouDiZhuPatternKind.Bomb, maxRank, count, out pattern);

        if (TryFindGroupWithWings(counts, 3, 1, pairWings: false, out var tripleRank))
            return Set(DouDiZhuPatternKind.TripleWithSingle, tripleRank, count, out pattern);
        if (TryFindGroupWithWings(counts, 3, 2, pairWings: true, out tripleRank))
            return Set(DouDiZhuPatternKind.TripleWithPair, tripleRank, count, out pattern);

        if (TrySequence(counts, count, 1, minimumGroups: 5, out var sequenceHigh))
            return Set(DouDiZhuPatternKind.Straight, sequenceHigh, count, out pattern, count);
        if (TrySequence(counts, count, 2, minimumGroups: 3, out sequenceHigh))
            return Set(DouDiZhuPatternKind.ConsecutivePairs, sequenceHigh, count, out pattern, count / 2);

        if (TryAirplane(counts, count, out var airplaneKind, out var airplaneHigh, out var airplaneLength))
            return Set(airplaneKind, airplaneHigh, count, out pattern, airplaneLength);

        if (TryFourWithWings(counts, pairWings: false, out var fourRank))
            return Set(DouDiZhuPatternKind.FourWithTwoSingles, fourRank, count, out pattern);
        if (TryFourWithWings(counts, pairWings: true, out fourRank))
            return Set(DouDiZhuPatternKind.FourWithTwoPairs, fourRank, count, out pattern);

        return false;
    }

    /// <summary>判定候选牌型能否压过当前牌型。</summary>
    public static bool CanBeat(DouDiZhuPattern candidate, DouDiZhuPattern current)
    {
        if (candidate.Kind is DouDiZhuPatternKind.Invalid || current.Kind is DouDiZhuPatternKind.Invalid)
            return false;
        if (candidate.Kind == DouDiZhuPatternKind.Rocket) return current.Kind != DouDiZhuPatternKind.Rocket;
        if (current.Kind == DouDiZhuPatternKind.Rocket) return false;
        if (candidate.Kind == DouDiZhuPatternKind.Bomb)
            return current.Kind != DouDiZhuPatternKind.Bomb || candidate.PrimaryRank > current.PrimaryRank;
        if (current.Kind == DouDiZhuPatternKind.Bomb) return false;
        return candidate.Kind == current.Kind
            && candidate.CardCount == current.CardCount
            && candidate.SequenceLength == current.SequenceLength
            && candidate.PrimaryRank > current.PrimaryRank;
    }

    /// <summary>依抢地主次数、炸弹/王炸數量及春天判定计算终局倍率。</summary>
    public static int CalculateMultiplier(int robCount, int bombCount, bool spring)
    {
        if (robCount < 0 || robCount > 3) throw new ArgumentOutOfRangeException(nameof(robCount));
        if (bombCount < 0 || bombCount > 14) throw new ArgumentOutOfRangeException(nameof(bombCount));
        var exponent = robCount + bombCount + (spring ? 1 : 0);
        return 1 << exponent;
    }

    /// <summary>判定一局是否满足春天或反春条件。</summary>
    public static bool IsSpring(bool landlordWon, int farmerPlayCount, int landlordPlayCount) =>
        landlordWon ? farmerPlayCount == 0 : landlordPlayCount == 1;

    private static bool Set(DouDiZhuPatternKind kind, int rank, int count, out DouDiZhuPattern pattern, int sequenceLength = 1)
    {
        pattern = new DouDiZhuPattern(kind, rank, count, sequenceLength);
        return true;
    }

    private static int HighestRank(int[] counts)
    {
        for (var rank = counts.Length - 1; rank >= 3; rank--)
            if (counts[rank] != 0) return rank;
        return 0;
    }

    private static bool TryFindGroupWithWings(int[] counts, int groupSize, int wingCards,
        bool pairWings, out int groupRank)
    {
        groupRank = 0;
        if (counts.Sum() != groupSize + wingCards) return false;
        for (var rank = 3; rank <= 15; rank++)
        {
            if (counts[rank] != groupSize) continue;
            var validWings = true;
            for (var other = 3; other <= 17; other++)
            {
                if (other == rank || counts[other] == 0) continue;
                if (pairWings ? counts[other] != 2 : counts[other] != 1)
                {
                    validWings = false;
                    break;
                }
            }
            if (!validWings || counts[rank] + counts.Where((_, i) => i != rank).Sum() != groupSize + wingCards)
                continue;
            groupRank = rank;
            return true;
        }
        return false;
    }

    private static bool TrySequence(int[] counts, int cardCount, int copies, int minimumGroups, out int high)
    {
        high = 0;
        if (cardCount % copies != 0) return false;
        var length = cardCount / copies;
        if (length < minimumGroups || length > 12) return false;
        var first = -1;
        for (var rank = 3; rank <= 17; rank++)
        {
            if (counts[rank] == 0) continue;
            if (rank > 14 || counts[rank] != copies) return false;
            if (first < 0) first = rank;
            high = rank;
        }
        return first >= 0 && high - first + 1 == length;
    }

    private static bool TryAirplane(int[] counts, int cardCount,
        out DouDiZhuPatternKind kind, out int high, out int length)
    {
        kind = DouDiZhuPatternKind.Invalid;
        high = 0;
        length = 0;
        for (var groupCount = 2; groupCount <= 6; groupCount++)
        {
            if (cardCount != groupCount * 3 && cardCount != groupCount * 4 && cardCount != groupCount * 5)
                continue;
            var wingSize = cardCount - groupCount * 3;
            if (wingSize != 0 && wingSize != groupCount && wingSize != groupCount * 2) continue;
            for (var start = 3; start + groupCount - 1 <= 14; start++)
            {
                var remainder = counts.ToArray();
                var validCore = true;
                for (var rank = start; rank < start + groupCount; rank++)
                {
                    if (remainder[rank] < 3) { validCore = false; break; }
                    remainder[rank] -= 3;
                }
                if (!validCore) continue;
                if (wingSize == 0 && remainder.All(value => value == 0))
                    kind = DouDiZhuPatternKind.Airplane;
                else if (wingSize == groupCount && remainder.Sum() == groupCount
                    && remainder.All(value => value <= 1))
                    kind = DouDiZhuPatternKind.AirplaneWithSingles;
                else if (wingSize == groupCount * 2 && remainder.Sum() == wingSize
                    && remainder.All(value => value is 0 or 2))
                    kind = DouDiZhuPatternKind.AirplaneWithPairs;
                else
                    continue;
                high = start + groupCount - 1;
                length = groupCount;
                return true;
            }
        }
        return false;
    }

    private static bool TryFourWithWings(int[] counts, bool pairWings, out int fourRank)
    {
        fourRank = 0;
        var wingSize = pairWings ? 4 : 2;
        if (counts.Sum() != 4 + wingSize) return false;
        for (var rank = 3; rank <= 15; rank++)
        {
            if (counts[rank] != 4) continue;
            var remainder = counts.ToArray();
            remainder[rank] = 0;
            if (pairWings
                ? remainder.Count(value => value == 2) == 2 && remainder.All(value => value is 0 or 2)
                : remainder.Sum() == 2)
            {
                fourRank = rank;
                return true;
            }
        }
        return false;
    }
}
