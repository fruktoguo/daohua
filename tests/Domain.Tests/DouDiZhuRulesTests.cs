using Domain.Social;

namespace Domain.Tests;

public sealed class DouDiZhuRulesTests
{
    [Theory]
    [InlineData(3, 5, DouDiZhuPatternKind.Straight)]
    [InlineData(4, 3, DouDiZhuPatternKind.ConsecutivePairs)]
    [InlineData(5, 2, DouDiZhuPatternKind.Airplane)]
    [InlineData(6, 2, DouDiZhuPatternKind.AirplaneWithSingles)]
    [InlineData(7, 2, DouDiZhuPatternKind.AirplaneWithPairs)]
    public void TryEvaluate_RecognizesConsecutivePattern(int startRank, int groups, DouDiZhuPatternKind expected)
    {
        var cards = expected switch
        {
            DouDiZhuPatternKind.Straight => Enumerable.Range(startRank, groups).Select(rank => Card(rank)).ToArray(),
            DouDiZhuPatternKind.ConsecutivePairs => Enumerable.Range(startRank, groups).SelectMany(rank => Cards(rank, 2)).ToArray(),
            DouDiZhuPatternKind.Airplane => Enumerable.Range(startRank, groups).SelectMany(rank => Cards(rank, 3)).ToArray(),
            DouDiZhuPatternKind.AirplaneWithSingles => Enumerable.Range(startRank, groups).SelectMany(rank => Cards(rank, 3))
                .Append(Card(12)).Append(Card(13)).ToArray(),
            DouDiZhuPatternKind.AirplaneWithPairs => Enumerable.Range(startRank, groups).SelectMany(rank => Cards(rank, 3))
                .Concat(Cards(12, 2)).Concat(Cards(13, 2)).ToArray(),
            _ => throw new InvalidOperationException()
        };

        Assert.True(DouDiZhuRules.TryEvaluate(cards, out var pattern));
        Assert.Equal(expected, pattern.Kind);
        Assert.Equal(groups, pattern.SequenceLength);
    }

    [Fact]
    public void CanBeat_UsesSameShapeAndBombHierarchy()
    {
        Assert.True(DouDiZhuRules.TryEvaluate(Cards(8, 3), out var tripleEight));
        Assert.True(DouDiZhuRules.TryEvaluate(Cards(9, 3), out var tripleNine));
        Assert.True(DouDiZhuRules.TryEvaluate(Cards(6, 4), out var bombSix));
        Assert.True(DouDiZhuRules.TryEvaluate([52, 53], out var rocket));

        Assert.True(DouDiZhuRules.CanBeat(tripleNine, tripleEight));
        Assert.False(DouDiZhuRules.CanBeat(bombSix, rocket));
        Assert.True(DouDiZhuRules.CanBeat(bombSix, tripleNine));
        Assert.True(DouDiZhuRules.CanBeat(rocket, bombSix));
        Assert.False(DouDiZhuRules.CanBeat(tripleNine, bombSix));
    }

    [Fact]
    public void TryEvaluate_RejectsDuplicateAndInvalidCards()
    {
        Assert.False(DouDiZhuRules.TryEvaluate([0, 0], out _));
        Assert.False(DouDiZhuRules.TryEvaluate([54], out _));
        Assert.False(DouDiZhuRules.TryEvaluate([Card(3), Card(5), Card(6), Card(7), Card(8)], out _));
        Assert.False(DouDiZhuRules.TryEvaluate([Card(3), Card(4), Card(6), Card(7), Card(8), Card(15)], out _));
    }

    [Theory]
    [InlineData(0, 0, false, 1)]
    [InlineData(1, 0, false, 2)]
    [InlineData(2, 1, false, 8)]
    [InlineData(2, 1, true, 16)]
    public void CalculateMultiplier_ComposesRobBombAndSpring(int robs, int bombs, bool spring, int expected)
    {
        Assert.Equal(expected, DouDiZhuRules.CalculateMultiplier(robs, bombs, spring));
    }

    [Fact]
    public void IsSpring_DistinguishesSpringAndAntiSpring()
    {
        Assert.True(DouDiZhuRules.IsSpring(landlordWon: true, farmerPlayCount: 0, landlordPlayCount: 4));
        Assert.True(DouDiZhuRules.IsSpring(landlordWon: false, farmerPlayCount: 5, landlordPlayCount: 1));
        Assert.False(DouDiZhuRules.IsSpring(landlordWon: true, farmerPlayCount: 1, landlordPlayCount: 4));
        Assert.False(DouDiZhuRules.IsSpring(landlordWon: false, farmerPlayCount: 5, landlordPlayCount: 2));
    }

    private static int Card(int rank, int suit = 0) => (rank - 3) * 4 + suit;

    private static int[] Cards(int rank, int count) => Enumerable.Range(0, count).Select(suit => Card(rank, suit)).ToArray();
}
