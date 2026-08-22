using Daoxuan.Domain.Models;

namespace Daoxuan.Domain.Market;

/// <summary>
/// 市场价格档位与步长策略类。
/// 档位随价格数量级使用 1/2/5 阶梯步长，避免高价订单用 1 灵石微小价差恶意抢单刷单。
/// </summary>
public static class DaoxuanMarketPricePolicy
{
    /// <summary>坊市挂单绝对最低底价（2 灵石）。</summary>
    public const long AbsoluteMinimumPrice = 2;
    /// <summary>订单在盘口的有效存活期（30 天）。</summary>
    public static readonly TimeSpan ListingLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// 根据当前价格所处的数量级区间，计算对应的最小价格变动步长（Tick Size）。
    /// </summary>
    /// <param name="price">价格数值。</param>
    /// <returns>该价位下的最小变动步长。</returns>
    public static long GetIncrement(long price)
    {
        if (price <= 1) return 1;

        var firstDigit = price;
        var magnitude = 1L;
        while (firstDigit >= 10)
        {
            firstDigit /= 10;
            magnitude = checked(magnitude * 10);
        }

        return firstDigit switch
        {
            1 or 2 when magnitude >= 100 => checked(5 * (magnitude / 100)),
            3 or 4 when magnitude >= 10 => magnitude / 10,
            >= 5 when magnitude >= 10 => checked(2 * (magnitude / 10)),
            _ => 1
        };
    }

    /// <summary>
    /// 将任意输入价格按对应步长与商店收购底价规范化为合法档位价格。
    /// </summary>
    /// <param name="price">原始输入价格。</param>
    /// <param name="vendorPrice">系统商店回收保底价。</param>
    /// <param name="roundUp">是否向上取整到下一个合法档位。</param>
    /// <returns>归一化后的合法价格。</returns>
    public static long Normalize(long price, long vendorPrice, bool roundUp)
    {
        if (vendorPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vendorPrice));
        }

        if (price <= 1) return AbsoluteMinimumPrice;

        var increment = GetIncrement(price);
        var remainder = price % increment;
        var normalized = price - remainder;
        if (normalized < vendorPrice)
        {
            roundUp = true;
        }

        if (roundUp && remainder > 0)
        {
            normalized = checked(normalized + increment);
        }

        return Math.Max(AbsoluteMinimumPrice, normalized);
    }

    /// <summary>
    /// 校验指定价格是否满足最小底价限制且完全对齐到合法步长档位。
    /// </summary>
    /// <param name="price">待校验价格。</param>
    /// <param name="vendorPrice">商店回收底价。</param>
    /// <returns>合法返回 true，否则返回 false。</returns>
    public static bool IsValid(long price, long vendorPrice)
    {
        if (vendorPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vendorPrice));
        }

        return price >= Math.Max(AbsoluteMinimumPrice, vendorPrice)
            && price % GetIncrement(price) == 0;
    }
}

