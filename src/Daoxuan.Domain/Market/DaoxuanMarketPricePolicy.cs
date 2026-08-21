namespace Daoxuan.Domain.Market;

/// <summary>
/// 市场价格档位。档位随价格数量级使用 1/2/5 阶梯，避免高价订单用最小单位刷价。
/// </summary>
public static class DaoxuanMarketPricePolicy
{
    public const long AbsoluteMinimumPrice = 2;
    public static readonly TimeSpan ListingLifetime = TimeSpan.FromDays(30);

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
