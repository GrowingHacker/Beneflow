using System.Text.Json;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>
/// 收银台让利风控：限额 → 授权 → 留痕。
///
/// 为什么要有它：收银台的让利一共三个口子 —— 档案促销、整单优惠、抹零。
/// 档案促销定义在商品档案里、收银台只负责执行，抹零是现金找零的产物，
/// 而整单优惠自「让利只在档案里定义」那次调整后，前端已经不再提供任何输入（Sales.html 里 discountAmount 恒为 0）。
/// **但「前端不给入口」不等于「接口不可用」**：手搓一个请求把 DiscountRate 填 0.1、
/// 或把 RoundOffAmount 填成商品总额，就能把整单打成 0 元，而单据、库存、流水全都正常 ——
/// 过后只看数据分辨不出这是「促销」还是「有人给自己免了单」。所以围栏必须落在服务端。
///
/// 三件事各管什么：
///   · 限额：额度来自系统设置 sale 组（店主可在设置页改），默认 整单优惠 ≤ ¥50、不低于 9 折、抹零 ≤ ¥1；
///   · 授权：超限**不直接拒绝**，而是要求店主本人到场 —— 当前登录账号须持 owner 角色，并再输一次该账号密码；
///   · 留痕：授权通过的写进「收银结算」日志（带单号），未授权的写「超额让利被拒」日志。
///          后者必须独立落库（写在业务事务之外），否则事务一回滚，证据也跟着没了。
/// </summary>
public partial class SaleService
{
    /// <summary>店主授权的角色编码（与 AuthService.GetPermissionCodesAsync 里发 "*" 通配的那一个判据同源）</summary>
    private const string OwnerRoleCode = "owner";

    /// <summary>
    /// 让利限额：整单优惠金额上限（元）、最低折扣（折，9 = 不低于 9 折）、抹零上限（元）。
    /// 三项都是「不需要授权就能给的常规额度」，不是「最大值」—— 超过就转授权流程，而不是拒绝。
    /// </summary>
    private readonly record struct DiscountLimits(decimal MaxOrderDiscount, decimal MinDiscountRate, decimal MaxRoundOff)
    {
        /// <summary>
        /// 库里没配 sale 组、或配了但缺这几个键时的默认额度。
        /// 抹零为什么正好是 ¥1：抹零 = 抹掉金额的角分零头，抹到「元」为止最多也不会超过 ¥0.99 ——
        /// 超过 ¥1 的「抹零」已经不是零头，而是让利，本来就该走授权。
        /// </summary>
        public static readonly DiscountLimits Fallback = new(50m, 9m, 1m);

        /// <summary>
        /// 从 sale 组的 JSON 里读三个限额：缺项回退默认，读到的值一律夹到合法区间。
        /// 夹紧是因为限额本身也是「配置」：写个负数或 0 折进去，会让每一单都索要授权、收银台直接瘫掉。
        /// </summary>
        public static DiscountLimits From(JsonElement root) => new(
            Math.Max(0m, ReadDecimal(root, "maxOrderDiscount", Fallback.MaxOrderDiscount)),
            Math.Clamp(ReadDecimal(root, "minDiscountRate", Fallback.MinDiscountRate), 0.1m, 10m),
            Math.Max(0m, ReadDecimal(root, "maxRoundOff", Fallback.MaxRoundOff)));

        private static decimal ReadDecimal(JsonElement root, string name, decimal fallback) =>
            root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var v)
                ? v : fallback;
    }

    /// <summary>sale 配置组一次读出来：是否允许赊账 + 让利限额（原先只为 allowCredit 查一次库，顺带取限额，不加查询）</summary>
    private readonly record struct SaleConfig(bool AllowCredit, DiscountLimits Limits)
    {
        public static readonly SaleConfig Fallback = new(true, DiscountLimits.Fallback);
    }

    /// <summary>让利风控的判定结果：Pass = 是否放行；Message = 不放行时给前端的说法；AuthorizedNote = 超限但已获授权的说明</summary>
    private readonly record struct DiscountFence(bool Pass, string Message, string? AuthorizedNote)
    {
        public static readonly DiscountFence Ok = new(true, "", null);
    }

    /// <summary>
    /// 读 sale 配置组。组不存在 / JSON 解析失败一律回退默认值 —— 配置写坏不能把收银台卡死
    /// （原注释：JSON 解析失败时默认允许赊账，不影响正常使用）。
    /// </summary>
    private async Task<SaleConfig> ReadSaleConfigAsync()
    {
        var json = await _db.SystemConfigs.AsNoTracking()
            .Where(c => c.ConfigKey == "sale").Select(c => c.ConfigValue).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(json)) return SaleConfig.Fallback;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var allowCredit = !(root.TryGetProperty("allowCredit", out var ac)
                                && ac.ValueKind == JsonValueKind.False);
            return new SaleConfig(allowCredit, DiscountLimits.From(root));
        }
        catch { return SaleConfig.Fallback; }
    }

    /// <summary>
    /// 让利风控闸门：判断本次让利是否超限，超限则校验店主授权，并把结果落进操作日志。
    ///
    /// **只看 dto 就能判定**，所以能放在业务事务之前，这一点是有意为之：
    /// ① 拒绝时写的日志不会被事务回滚掉；② 不必先开事务、查一遍商品、再白回滚一次。
    /// 比的是「填进来的那个数」而不是「被夹紧后的数」—— 把 ¥9999 的抹零夹到 ¥20 再比，
    /// 等于「只要单子够小就随便抹」，围栏就白设了。
    ///
    /// 三项判定都与实际生效的口径对齐，避免索要一次根本用不上的授权：
    ///   · 折率与金额是同一件事的两种录入方式、业务上以折率为准（见 CreateCoreAsync），故有折率时只比折率；
    ///   · 抹零只有「单项支付 + 现金」才真的减钱，混合支付恒 0、非现金单直接忽略，那两种情形不比。
    /// </summary>
    private async Task<DiscountFence> CheckDiscountFenceAsync(CreateSaleDto dto, DiscountLimits limits)
    {
        var over = new List<string>();

        var rate = dto.DiscountRate.HasValue ? Math.Round(dto.DiscountRate.Value, 2) : (decimal?)null;
        if (rate.HasValue)
        {
            // 只对「合法折率（0.1 ~ 10 折）」判政策：区间外的折率下游那道合法性校验本来就不接受
            // （CreateCoreAsync「折扣需在 0.1 ~ 10 折之间」），若在这里先报「需店主授权」会误导 ——
            // 授权也救不了非法值，两件事得分开说。
            if (rate.Value >= 0.1m && rate.Value <= 10m && rate.Value < limits.MinDiscountRate)
                over.Add($"折率 {FmtRate(rate.Value)} 折低于下限 {FmtRate(limits.MinDiscountRate)} 折");
        }
        else
        {
            var amount = Math.Round(dto.DiscountAmount, 2);
            if (amount > limits.MaxOrderDiscount)
                over.Add($"整单优惠 ¥{amount:0.00} 超过上限 ¥{limits.MaxOrderDiscount:0.00}");
        }

        var isMixed = dto.Payments is { Count: > 0 };
        var roundOff = isMixed || dto.PayMethod != "现金" ? 0m : Math.Round(dto.RoundOffAmount, 2);
        if (roundOff > limits.MaxRoundOff)
            over.Add($"抹零 ¥{roundOff:0.00} 超过上限 ¥{limits.MaxRoundOff:0.00}");

        if (over.Count == 0) return DiscountFence.Ok;   // 常规让利，收银员自己就能做

        var detail = string.Join("、", over);
        var deny = await VerifyOwnerAsync(dto.DiscountAuthPassword);
        if (deny == null) return new DiscountFence(true, "", $"{detail}，店主 {_me.Username} 授权");

        // 越权尝试留痕：立即独立落库，写在业务事务之外 —— 事务回滚抹不掉这条证据
        await WriteOpLogAsync("收银台", "超额让利被拒", $"{detail}；{deny}");
        return new DiscountFence(false, $"让利超出限额（{detail}），需店主授权：{deny}", null);
    }

    /// <summary>
    /// 店主二次确认：当前登录账号必须持 owner 角色，且提交的密码要能通过该账号自己的哈希校验。
    /// 返回 null 表示通过，否则返回拒绝原因。
    ///
    /// 为什么不是「输任意一个店主的密码」：库里可能不止一个店主账号，那样得先回答「算谁的授权」；
    /// 而且那等于开了一条绕过登录态的通道。现在的口径是「店主人到场、用自己的账号确认」——
    /// 收银员账号本身就没有让利能力，权限的问题用权限解决，别用口令硬掰。
    /// </summary>
    private async Task<string?> VerifyOwnerAsync(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return "未提供店主授权密码";

        var isOwner = await (
            from ur in _db.UserRoles
            join r in _db.Roles on ur.RoleId equals r.Id
            where ur.UserId == _me.Id && r.Code == OwnerRoleCode
            select 1).AnyAsync();
        if (!isOwner) return $"当前账号「{_me.Username}」不是店主，无权授权";

        var auth = await _db.Users.AsNoTracking().Where(u => u.Id == _me.Id)
            .Select(u => new { u.Salt, u.PasswordHash }).FirstOrDefaultAsync();
        if (auth == null) return "当前账号不存在";
        return PasswordHasher.Verify(password, auth.Salt, auth.PasswordHash) ? null : "店主授权密码不正确";
    }

    private static string FmtRate(decimal rate) => rate.ToString("0.##");

    /// <summary>写一条操作日志并立即落库（风控留痕要独立于业务事务，不能等调用方统一保存）</summary>
    private async Task WriteOpLogAsync(string module, string action, string? target)
    {
        _db.OperationLogs.Add(new OperationLog
        {
            UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp,
            Module = module, Action = action, Target = target,
        });
        await _db.SaveChangesAsync();
    }
}
