using Beneflow.Api.Utils;

namespace Beneflow.Tests.Unit;

/// <summary>
/// 档案优惠的生效判定与成交价换算。纯函数，重点覆盖「哪些情况不该生效」——
/// 边界写错会让过期促销继续按促销价卖，是这类功能最容易出的账目问题。
/// </summary>
public class PromoHelperTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0);

    [Fact]
    public void IsActive_SwitchOff_NeverActive()
    {
        Assert.False(PromoHelper.IsActive(PromoHelper.TypePrice, false, null, null, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("满减")]
    public void IsActive_UnknownOrEmptyType_NotActive(string type)
    {
        Assert.False(PromoHelper.IsActive(type, true, null, null, Now));
    }

    [Fact]
    public void IsActive_NoTimeLimit_Active()
    {
        Assert.True(PromoHelper.IsActive(PromoHelper.TypePrice, true, null, null, Now));
    }

    [Fact]
    public void IsActive_WithinRange_Active()
    {
        Assert.True(PromoHelper.IsActive(PromoHelper.TypeRate, true, Now.AddDays(-1), Now.AddDays(1), Now));
    }

    [Fact]
    public void IsActive_NotStartedYet_NotActive()
    {
        Assert.False(PromoHelper.IsActive(PromoHelper.TypeRate, true, Now.AddMinutes(1), null, Now));
    }

    [Fact]
    public void IsActive_AlreadyEnded_NotActive()
    {
        Assert.False(PromoHelper.IsActive(PromoHelper.TypeRate, true, null, Now.AddMinutes(-1), Now));
    }

    [Fact]
    public void IsActive_ExactlyOnBoundaries_Active()
    {
        // 含边界：开始时刻当秒生效，结束时刻当秒仍然生效
        Assert.True(PromoHelper.IsActive(PromoHelper.TypeRate, true, Now, Now, Now));
    }

    [Fact]
    public void NormalizeEnd_CoversWholeEndDate()
    {
        // 归一化成结束日 23:59:59，否则「促销设到 9-30」会变成 9-30 零点就失效
        var end = PromoHelper.NormalizeEnd(new DateTime(2026, 9, 30));
        Assert.Equal(new DateTime(2026, 9, 30, 23, 59, 59), end);
        Assert.True(PromoHelper.IsActive(PromoHelper.TypePrice, true, null, end, new DateTime(2026, 9, 30, 22, 0, 0)));
        Assert.False(PromoHelper.IsActive(PromoHelper.TypePrice, true, null, end, new DateTime(2026, 10, 1, 0, 0, 1)));
    }

    [Fact]
    public void NormalizeStart_IsMidnight()
    {
        Assert.Equal(new DateTime(2026, 9, 1), PromoHelper.NormalizeStart(new DateTime(2026, 9, 1, 15, 30, 0)));
    }

    [Fact]
    public void EffectivePrice_PromoNotActive_ReturnsSalePrice()
    {
        // 已过期 ⇒ 回到挂牌价
        Assert.Equal(10.00m, PromoHelper.EffectivePrice(10.00m, PromoHelper.TypePrice, 7.5m, 8m,
            true, null, Now.AddDays(-1), Now));
    }

    [Fact]
    public void EffectivePrice_PricePromo_ReturnsPromoPrice()
    {
        Assert.Equal(7.50m, PromoHelper.EffectivePrice(10.00m, PromoHelper.TypePrice, 7.5m, 8m, true, null, null, Now));
    }

    [Fact]
    public void EffectivePrice_PromoPriceAboveSalePrice_FallsBackToSalePrice()
    {
        // 挂牌价后来被改低、促销没跟着改：按挂牌价卖，不让顾客多付钱
        Assert.Equal(7.00m, PromoHelper.EffectivePrice(7.00m, PromoHelper.TypePrice, 7.5m, 0m, true, null, null, Now));
    }

    [Theory]
    [InlineData(10.00, 8, 8.00)]
    [InlineData(9.90, 8, 7.92)]
    [InlineData(3.33, 6.66, 2.22)]
    public void EffectivePrice_RatePromo_RoundsToCent(decimal salePrice, decimal rate, decimal expected)
    {
        Assert.Equal(expected, PromoHelper.EffectivePrice(salePrice, PromoHelper.TypeRate, 0m, rate, true, null, null, Now));
    }

    [Fact]
    public void Describe_RendersChineseText()
    {
        Assert.Equal("特价 ¥3.90", PromoHelper.Describe(PromoHelper.TypePrice, 3.90m, 0m));
        Assert.Equal("8.8 折", PromoHelper.Describe(PromoHelper.TypeRate, 0m, 8.8m));
        Assert.Equal("", PromoHelper.Describe(PromoHelper.TypeNone, 3.90m, 8.8m));
    }

    [Theory]
    [InlineData("")]
    [InlineData("特价")]
    [InlineData("折扣")]
    public void IsValidType_AcceptsKnownTypes(string type)
    {
        Assert.True(PromoHelper.IsValidType(type));
    }

    [Fact]
    public void IsValidType_RejectsUnknown()
    {
        Assert.False(PromoHelper.IsValidType("买一送一"));
    }
}
