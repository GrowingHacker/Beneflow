namespace Beneflow.Api.Utils;

/// <summary>
/// 商品档案优惠（促销）：生效判定与成交价换算。
/// 商品档案与收银结算共用同一套口径，避免「档案按一套算法算、收银按另一套算」而两边对不上账。
/// 优惠方案只在商品档案里定义，收银台不提供任何改动入口，收银员只需执行。
/// </summary>
public static class PromoHelper
{
    /// <summary>促销类型：无优惠</summary>
    public const string TypeNone = "";
    /// <summary>促销类型：特价（按促销价直接定价）</summary>
    public const string TypePrice = "特价";
    /// <summary>促销类型：折扣（按折率打折）</summary>
    public const string TypeRate = "折扣";

    /// <summary>折率下限（单位「折」），与整单折扣保持一致</summary>
    public const decimal MinRate = 0.1m;
    /// <summary>折率上限（单位「折」）</summary>
    public const decimal MaxRate = 10m;

    /// <summary>促销类型是否合法：空串（无优惠）与两种已知类型之外一律非法</summary>
    public static bool IsValidType(string? promoType) =>
        string.IsNullOrEmpty(promoType) || promoType == TypePrice || promoType == TypeRate;

    /// <summary>
    /// 促销当前是否生效：手动开关打开 + 类型有效 + 落在起止时间内（null 表示该侧不限）。
    /// 起止时间是「含边界」的：开始日当天零点起效，结束日当天全部有效（调用方须先经 Normalize 归一化）。
    /// </summary>
    public static bool IsActive(string? promoType, bool enabled, DateTime? startAt, DateTime? endAt, DateTime now)
    {
        if (!enabled) return false;
        if (promoType != TypePrice && promoType != TypeRate) return false;
        if (startAt.HasValue && now < startAt.Value) return false;
        if (endAt.HasValue && now > endAt.Value) return false;
        return true;
    }

    /// <summary>按折率折算价格：乘折率再四舍五入到分，与整单折扣的「折后金额」同口径</summary>
    public static decimal ApplyRate(decimal price, decimal rate) => Math.Round(price * rate / 10m, 2);

    /// <summary>
    /// 商品当前的成交单价：促销生效则取促销价（或折后价），否则取挂牌价。
    /// 收银台与商品档案列表都用它，保证「档案看到的价」与「收银收的价」是同一个数。
    /// </summary>
    public static decimal EffectivePrice(decimal salePrice, string? promoType, decimal promoPrice, decimal promoRate,
        bool enabled, DateTime? startAt, DateTime? endAt, DateTime now)
    {
        if (!IsActive(promoType, enabled, startAt, endAt, now)) return salePrice;
        // 上限保护：若档案后来把挂牌价改低、却忘了同步促销，促销价可能反而高于挂牌价，
        // 这时按挂牌价卖，不让顾客多付钱。有此约束才能保证 成交价 ≤ 挂牌价，让利不会算出负数
        var promo = promoType == TypePrice ? promoPrice : ApplyRate(salePrice, promoRate);
        return promo > salePrice ? salePrice : promo;
    }

    /// <summary>促销文案（档案列表用），如「特价 ¥3.90」「8.8 折」；无促销返回空串</summary>
    public static string Describe(string? promoType, decimal promoPrice, decimal promoRate)
    {
        if (promoType == TypePrice) return $"特价 ¥{promoPrice:0.00}";
        if (promoType == TypeRate) return $"{promoRate:0.##} 折";
        return "";
    }

    /// <summary>把「开始日期」归一化为当天零点</summary>
    public static DateTime? NormalizeStart(DateTime? start) => start?.Date;

    /// <summary>把「结束日期」归一化为当天 23:59:59，使结束日期当天全天有效（否则前端传 2026-09-30 会变成当天零点即失效）</summary>
    public static DateTime? NormalizeEnd(DateTime? end) => end?.Date.AddDays(1).AddSeconds(-1);
}
