// ==============================================================================
// 文件名称: ExperienceConstants.cs
// 主要职责: 集中定义修仙领域系统中全系技能神通与全系生活技艺（炼丹、炼器等）通用的 1~200 级经验表、等级换算及掉落概率常数。
// 核心逻辑 / 定义内容:
//   1. 1~200 级通用平滑取整累计经验表（UniversalCumulativeExp）。
//   2. 经验 -> 等级正反向快速查询与升级进度计算方法。
//   3. 物品掉落率阶梯与技能秘籍/残卷数值常量（DropRateConstants, SkillTomeConstants）。
// ==============================================================================

namespace Domain.Constants;

using System;
using System.Collections.Frozen;

/// <summary>
/// 全系技能神通与全系生活技艺通用 1~200 级经验体系与等级换算常量。
/// </summary>
public static class ExperienceConstants
{
    /// <summary>通用技能与技艺最高等级（200 级）。</summary>
    public const int MaxLevel = 200;

    /// <summary>
    /// 1~200 级通用累计经验表（以 1 为起始下标，索引即对应等级）。
    /// </summary>
    public static readonly double[] UniversalCumulativeExp =
    [
        0.0, // 0 占位
                    0.0, // Lv.  1
                   35.0, // Lv.  2
                   80.0, // Lv.  3
                  130.0, // Lv.  4
                  200.0, // Lv.  5
                  290.0, // Lv.  6
                  390.0, // Lv.  7
                  500.0, // Lv.  8
                  640.0, // Lv.  9
                  790.0, // Lv. 10
                  960.0, // Lv. 11
                 1150.0, // Lv. 12
                 1400.0, // Lv. 13
                 1600.0, // Lv. 14
                 1900.0, // Lv. 15
                 2200.0, // Lv. 16
                 2500.0, // Lv. 17
                 2900.0, // Lv. 18
                 3300.0, // Lv. 19
                 3750.0, // Lv. 20
                 4250.0, // Lv. 21
                 4800.0, // Lv. 22
                 5400.0, // Lv. 23
                 6100.0, // Lv. 24
                 6800.0, // Lv. 25
                 7600.0, // Lv. 26
                 8500.0, // Lv. 27
                 9500.0, // Lv. 28
                10600.0, // Lv. 29
                11800.0, // Lv. 30
                13200.0, // Lv. 31
                14600.0, // Lv. 32
                16300.0, // Lv. 33
                18100.0, // Lv. 34
                20000.0, // Lv. 35
                22500.0, // Lv. 36
                24500.0, // Lv. 37
                27500.0, // Lv. 38
                30500.0, // Lv. 39
                33500.0, // Lv. 40
                37500.0, // Lv. 41
                41500.0, // Lv. 42
                46000.0, // Lv. 43
                51000.0, // Lv. 44
                56000.0, // Lv. 45
                62000.0, // Lv. 46
                69000.0, // Lv. 47
                76000.0, // Lv. 48
                84500.0, // Lv. 49
                93500.0, // Lv. 50
               105000.0, // Lv. 51
               115000.0, // Lv. 52
               125000.0, // Lv. 53
               140000.0, // Lv. 54
               155000.0, // Lv. 55
               170000.0, // Lv. 56
               190000.0, // Lv. 57
               205000.0, // Lv. 58
               230000.0, // Lv. 59
               255000.0, // Lv. 60
               280000.0, // Lv. 61
               305000.0, // Lv. 62
               340000.0, // Lv. 63
               375000.0, // Lv. 64
               410000.0, // Lv. 65
               455000.0, // Lv. 66
               500000.0, // Lv. 67
               550000.0, // Lv. 68
               605000.0, // Lv. 69
               665000.0, // Lv. 70
               730000.0, // Lv. 71
               805000.0, // Lv. 72
               885000.0, // Lv. 73
               970000.0, // Lv. 74
              1050000.0, // Lv. 75
              1150000.0, // Lv. 76
              1300000.0, // Lv. 77
              1400000.0, // Lv. 78
              1550000.0, // Lv. 79
              1700000.0, // Lv. 80
              1850000.0, // Lv. 81
              2050000.0, // Lv. 82
              2250000.0, // Lv. 83
              2450000.0, // Lv. 84
              2650000.0, // Lv. 85
              2900000.0, // Lv. 86
              3200000.0, // Lv. 87
              3500000.0, // Lv. 88
              3800000.0, // Lv. 89
              4200000.0, // Lv. 90
              4550000.0, // Lv. 91
              5000000.0, // Lv. 92
              5450000.0, // Lv. 93
              5950000.0, // Lv. 94
              6500000.0, // Lv. 95
              7100000.0, // Lv. 96
              7700000.0, // Lv. 97
              8400000.0, // Lv. 98
              9200000.0, // Lv. 99
             10000000.0, // Lv.100
             11500000.0, // Lv.101
             13000000.0, // Lv.102
             14500000.0, // Lv.103
             16000000.0, // Lv.104
             18000000.0, // Lv.105
             20000000.0, // Lv.106
             22000000.0, // Lv.107
             24500000.0, // Lv.108
             27000000.0, // Lv.109
             29500000.0, // Lv.110
             32500000.0, // Lv.111
             35500000.0, // Lv.112
             38500000.0, // Lv.113
             42000000.0, // Lv.114
             46000000.0, // Lv.115
             49500000.0, // Lv.116
             54000000.0, // Lv.117
             58500000.0, // Lv.118
             63500000.0, // Lv.119
             68500000.0, // Lv.120
             74000000.0, // Lv.121
             80000000.0, // Lv.122
             86000000.0, // Lv.123
             93000000.0, // Lv.124
            100000000.0, // Lv.125
            115000000.0, // Lv.126
            130000000.0, // Lv.127
            145000000.0, // Lv.128
            165000000.0, // Lv.129
            185000000.0, // Lv.130
            210000000.0, // Lv.131
            235000000.0, // Lv.132
            260000000.0, // Lv.133
            290000000.0, // Lv.134
            325000000.0, // Lv.135
            360000000.0, // Lv.136
            395000000.0, // Lv.137
            440000000.0, // Lv.138
            485000000.0, // Lv.139
            535000000.0, // Lv.140
            590000000.0, // Lv.141
            650000000.0, // Lv.142
            715000000.0, // Lv.143
            785000000.0, // Lv.144
            865000000.0, // Lv.145
            950000000.0, // Lv.146
           1050000000.0, // Lv.147
           1150000000.0, // Lv.148
           1250000000.0, // Lv.149
           1400000000.0, // Lv.150
           1500000000.0, // Lv.151
           1650000000.0, // Lv.152
           1800000000.0, // Lv.153
           2000000000.0, // Lv.154
           2150000000.0, // Lv.155
           2350000000.0, // Lv.156
           2600000000.0, // Lv.157
           2850000000.0, // Lv.158
           3100000000.0, // Lv.159
           3400000000.0, // Lv.160
           3700000000.0, // Lv.161
           4050000000.0, // Lv.162
           4400000000.0, // Lv.163
           4800000000.0, // Lv.164
           5250000000.0, // Lv.165
           5700000000.0, // Lv.166
           6250000000.0, // Lv.167
           6800000000.0, // Lv.168
           7400000000.0, // Lv.169
           8050000000.0, // Lv.170
           8800000000.0, // Lv.171
           9600000000.0, // Lv.172
          10500000000.0, // Lv.173
          11500000000.0, // Lv.174
          12500000000.0, // Lv.175
          13500000000.0, // Lv.176
          14500000000.0, // Lv.177
          16000000000.0, // Lv.178
          17500000000.0, // Lv.179
          19000000000.0, // Lv.180
          20500000000.0, // Lv.181
          22500000000.0, // Lv.182
          24500000000.0, // Lv.183
          26500000000.0, // Lv.184
          29000000000.0, // Lv.185
          31500000000.0, // Lv.186
          34000000000.0, // Lv.187
          37000000000.0, // Lv.188
          40500000000.0, // Lv.189
          44000000000.0, // Lv.190
          47500000000.0, // Lv.191
          51500000000.0, // Lv.192
          56000000000.0, // Lv.193
          61000000000.0, // Lv.194
          66500000000.0, // Lv.195
          72000000000.0, // Lv.196
          78000000000.0, // Lv.197
          85000000000.0, // Lv.198
          92000000000.0, // Lv.199
         100000000000.0, // Lv.200
    ];

    /// <summary>
    /// 获取达到指定等级所需的累计经验值。
    /// </summary>
    /// <param name="level">目标等级（1~200）。</param>
    /// <returns>所需累计经验值。</returns>
    public static double GetCumulativeExp(int level)
    {
        if (level <= 1) return 0.0;
        if (level >= MaxLevel) return UniversalCumulativeExp[MaxLevel];
        return UniversalCumulativeExp[level];
    }

    /// <summary>
    /// 获取从当前等级升级到下一等级所需的经验差额。
    /// </summary>
    /// <param name="level">当前等级（1~199）。</param>
    /// <returns>升级所需经验值，达到上限返回 0。</returns>
    public static double GetExpToNextLevel(int level)
    {
        if (level < 1) level = 1;
        if (level >= MaxLevel) return 0.0;
        return UniversalCumulativeExp[level + 1] - UniversalCumulativeExp[level];
    }

    /// <summary>
    /// 根据当前累计经验值计算对应的等级（1 ~ 200）。
    /// </summary>
    /// <param name="totalExp">累计经验值。</param>
    /// <returns>计算得出的当前等级。</returns>
    public static int GetLevelFromExp(double totalExp)
    {
        if (totalExp <= 0.0) return 1;
        if (totalExp >= UniversalCumulativeExp[MaxLevel]) return MaxLevel;

        // 使用二分查找快速定位等级
        int low = 1;
        int high = MaxLevel;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (UniversalCumulativeExp[mid] <= totalExp)
            {
                if (mid == MaxLevel || UniversalCumulativeExp[mid + 1] > totalExp)
                {
                    return mid;
                }
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return 1;
    }

    /// <summary>
    /// 计算升级进度详情。
    /// </summary>
    /// <param name="totalExp">累计经验值。</param>
    /// <returns>(当前等级, 当前级内已获得经验, 当前级升到下一级所需总经验, 升级百分比 0.0~1.0)</returns>
    public static (int Level, double CurrentLevelExp, double RequiredLevelExp, double ProgressRatio) GetProgress(double totalExp)
    {
        int level = GetLevelFromExp(totalExp);
        if (level >= MaxLevel)
        {
            return (MaxLevel, 0.0, 0.0, 1.0);
        }

        double baseExp = UniversalCumulativeExp[level];
        double nextExp = UniversalCumulativeExp[level + 1];
        double required = nextExp - baseExp;
        double current = Math.Max(0.0, totalExp - baseExp);
        double ratio = required > 0.0 ? Math.Clamp(current / required, 0.0, 1.0) : 1.0;

        return (level, current, required, ratio);
    }
}

/// <summary>
/// 物品掉落率层级标准常量。
/// </summary>
public static class DropRateConstants
{
    /// <summary>普通材料基础掉落率（100%）。</summary>
    public const double CommonMaterial = 1.0;

    /// <summary>稀有材料基础掉落率（10%）。</summary>
    public const double RareMaterial = 0.10;

    /// <summary>特殊材料/Boss核心基础掉落率（1%）。</summary>
    public const double SpecialMaterial = 0.01;

    /// <summary>技能书残卷基础掉落率（2%）。</summary>
    public const double SkillFragment = 0.02;

    /// <summary>完整技能书基础掉落率（0.1%）。</summary>
    public const double SkillTome = 0.001;
}

/// <summary>
/// 技能秘籍与残卷学习研读常量。
/// </summary>
public static class SkillTomeConstants
{
    /// <summary>首次使用完整技能书获得经验（0 经验，仅解锁学会技能）。</summary>
    public const double FirstLearnExp = 0.0;

    /// <summary>已学会技能后再次使用完整技能书获得的技能经验值（10,000 点）。</summary>
    public const double SubsequentTomeExp = 10_000.0;

    /// <summary>使用技能残卷获得的技能经验值（500 点，前提是已学会该技能）。</summary>
    public const double FragmentExp = 500.0;
}
