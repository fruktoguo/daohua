namespace Domain.Social;

/// <summary>托管按标准牌型生成候选，保留炸弹、减少拆牌；所有候选仍经领域规则裁决。</summary>
internal static class DouDiZhuMoveSelector
{
    private sealed record Move(int[] Cards, DouDiZhuPattern Pattern);

    public static int[]? Choose(DouDiZhuGameState state)
    {
        var hand = state.Hands[state.CurrentSeat];
        var groups = hand.GroupBy(DouDiZhuRules.RankOf).ToDictionary(group => group.Key, group => group.ToArray());
        var ranks = groups.Keys.Order().ToArray();
        var candidates = new List<Move>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(IEnumerable<int> source)
        {
            var cards = source.Order().ToArray();
            if (!seen.Add(string.Join(',', cards)) || !DouDiZhuRules.TryEvaluate(cards, out var pattern)
                || state.LastPattern is { } current && !DouDiZhuRules.CanBeat(pattern, current)) return;
            candidates.Add(new Move(cards, pattern));
        }
        foreach (var rank in ranks)
        {
            var cards = groups[rank];
            for (var count = 1; count <= cards.Length; count++) Add(cards.Take(count));
            if (cards.Length >= 3)
                foreach (var other in ranks.Where(value => value != rank))
                {
                    Add(cards.Take(3).Concat(groups[other].Take(1)));
                    if (groups[other].Length >= 2) Add(cards.Take(3).Concat(groups[other].Take(2)));
                }
            if (cards.Length != 4) continue;
            var others = ranks.Where(value => value != rank).ToArray();
            foreach (var wings in Combinations(others.SelectMany(value => groups[value].Take(2)).ToArray(), 2))
                Add(cards.Concat(wings));
            foreach (var wings in Combinations(others.Where(value => groups[value].Length >= 2).ToArray(), 2))
                Add(cards.Concat(wings.SelectMany(value => groups[value].Take(2))));
        }
        if (groups.ContainsKey(16) && groups.ContainsKey(17)) Add([52, 53]);
        for (var copies = 1; copies <= 3; copies++)
        {
            var minimum = copies == 1 ? 5 : copies == 2 ? 3 : 2;
            for (var start = 3; start <= 14; start++)
            {
                var core = new List<int>();
                for (var end = start; end <= 14 && groups.TryGetValue(end, out var cards) && cards.Length >= copies; end++)
                {
                    core.AddRange(cards.Take(copies));
                    var length = end - start + 1;
                    if (length < minimum) continue;
                    Add(core);
                    if (copies != 3) continue;
                    var remainder = groups.ToDictionary(pair => pair.Key,
                        pair => pair.Value.Skip(pair.Key >= start && pair.Key <= end ? 3 : 0).ToArray());
                    foreach (var wings in Combinations(ranks.Where(value => remainder[value].Length > 0).ToArray(), length))
                        Add(core.Concat(wings.Select(value => remainder[value][0])));
                    foreach (var wings in Combinations(ranks.Where(value => remainder[value].Length >= 2).ToArray(), length))
                        Add(core.Concat(wings.SelectMany(value => remainder[value].Take(2))));
                }
            }
        }
        var winning = candidates.FirstOrDefault(move => move.Cards.Length == hand.Length);
        if (winning is not null) return winning.Cards;
        if (state.LastPattern is not null && state.LastPlayedSeat is { } last
            && (state.CurrentSeat == state.LandlordSeat) == (last == state.LandlordSeat)) return null;

        return candidates.OrderBy(move => Score(hand, groups, move)).ThenBy(move => move.Pattern.PrimaryRank)
            .FirstOrDefault()?.Cards;
    }

    private static double Score(int[] hand, Dictionary<int, int[]> original, Move move)
    {
        var used = move.Cards.ToHashSet();
        var remainder = hand.Where(card => !used.Contains(card)).GroupBy(DouDiZhuRules.RankOf)
            .ToDictionary(group => group.Key, group => group.Count());
        double turns = remainder.Values.Sum(count => count == 1 ? 1.1 : 1);
        for (var copies = 1; copies <= 3; copies++)
        {
            var run = 0;
            for (var rank = 3; rank <= 15; rank++)
                if (rank <= 14 && remainder.GetValueOrDefault(rank) >= copies) run++;
                else
                {
                    if (run >= (copies == 1 ? 5 : copies == 2 ? 3 : 2)) turns -= (run - 1) * .5;
                    run = 0;
                }
        }
        turns -= Math.Min(remainder.Values.Count(count => count == 3), remainder.Values.Count(count => count <= 2)) * .55;
        var score = Math.Max(0, turns) * 25 + remainder.Values.Sum() * .6 + move.Pattern.PrimaryRank * .3;
        if (move.Pattern.Kind == DouDiZhuPatternKind.Bomb) score += 42;
        if (move.Pattern.Kind == DouDiZhuPatternKind.Rocket) score += 58;
        foreach (var group in move.Cards.GroupBy(DouDiZhuRules.RankOf))
            score += (original[group.Key].Length - group.Count()) * 4;
        return score;
    }

    /// <summary>只组合可用翅膀，不扫描整手牌的全部子集。</summary>
    private static IEnumerable<int[]> Combinations(int[] values, int count)
    {
        var picked = new int[count];
        return Walk(0, 0);
        IEnumerable<int[]> Walk(int start, int depth)
        {
            if (depth == count) { yield return picked.ToArray(); yield break; }
            for (var i = start; i <= values.Length - (count - depth); i++)
            {
                picked[depth] = values[i];
                foreach (var combination in Walk(i + 1, depth + 1)) yield return combination;
            }
        }
    }
}
