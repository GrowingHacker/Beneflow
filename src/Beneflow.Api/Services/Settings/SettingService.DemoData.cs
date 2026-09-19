using System.Text.Json;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>
/// 系统设置 · 演示数据：判断当前是否仍是演示数据、一键初始化（清空业务数据）。
///
/// 三个设计要点：
/// 1. 「是否演示数据」用 SystemConfig 里的一行 demo 标记承载，**缺行/坏值一律视为仍是演示数据**；
///    这样本次改动之前就已经播种好的老库（没有这行）无需迁移也会正确显示提示。
/// 2. 清空**保留**账号、角色、菜单、系统设置；也正因为保留了用户，
///    <c>DbSeeder.SeedAsync</c> 开头的 <c>if (await db.Users.AnyAsync()) return;</c> 会在重启后直接跳过，
///    演示数据不会「复活」——这是这套清空方案能成立的前提（若将来连账号一起清，必须另补持久化标记）。
/// 3. 先备份再清空；备份失败**不阻断**（清的是演示数据，且物理备份依赖 SQL Server 权限），原因随结果返回前端。
/// </summary>
public partial class SettingService
{
    /// <summary>演示数据标记的配置键；值 <c>{"isDemoData":false}</c> 表示已初始化过</summary>
    private const string DemoConfigKey = "demo";

    /// <summary>标记「已初始化」的配置值</summary>
    private const string DemoFlagValue = "{\"isDemoData\":false}";

    /// <summary>初始化前必须原样提交的确认文字（前端也会拦一次，这里是最后一道闸）</summary>
    public const string ClearConfirmText = "清空";

    /// <summary>当前是否仍是演示数据：标记行缺失、非对象、坏 JSON 都按「是」处理。</summary>
    public async Task<bool> IsDemoDataAsync()
    {
        var row = await _db.SystemConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.ConfigKey == DemoConfigKey);
        if (row == null) return true;
        try
        {
            using var doc = JsonDocument.Parse(row.ConfigValue);
            return !(doc.RootElement.ValueKind == JsonValueKind.Object
                     && doc.RootElement.TryGetProperty("isDemoData", out var flag)
                     && flag.ValueKind == JsonValueKind.False);
        }
        catch { return true; }   // 坏值按「仍是演示数据」处理，宁多提示一次
    }

    /// <summary>
    /// 清空全部演示业务数据并打上「已初始化」标记。整段一个事务：要么全清、要么原样保留，
    /// 不会出现「商品清了但销售单还在」这种半截状态。
    /// </summary>
    public async Task<ApiResult<InitializeDataResultDto>> ClearDemoDataAsync(string? confirmText)
    {
        if ((confirmText ?? "").Trim() != ClearConfirmText)
            return ApiResult<InitializeDataResultDto>.Fail($"请输入「{ClearConfirmText}」两个字以确认");

        var backup = await TryBackupBeforeClearAsync();

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 顺序 = 子表 → 主表。本项目关系都是 NO ACTION（没配级联删除），显式按序更稳。
            await ClearAsync(_db.CreditPayments);
            await ClearAsync(_db.CreditSales);
            await ClearAsync(_db.SaleReturnDetails);
            await ClearAsync(_db.SaleReturns);
            await ClearAsync(_db.SaleOrderDetails);
            await ClearAsync(_db.SaleOrders);
            await ClearAsync(_db.PurchaseReturnDetails);
            await ClearAsync(_db.PurchaseReturns);
            await ClearAsync(_db.PurchaseOrderDetails);
            await ClearAsync(_db.PurchaseOrders);
            await ClearAsync(_db.StockCheckDetails);
            await ClearAsync(_db.StockChecks);
            await ClearAsync(_db.StockLogs);
            await ClearAsync(_db.Batches);
            await ClearAsync(_db.Products);
            // 分类自引用（ParentId）：先删子分类再删父分类
            await ClearAsync(_db.Categories.Where(c => c.ParentId != null));
            await ClearAsync(_db.Categories);
            await ClearAsync(_db.Suppliers);
            await ClearAsync(_db.OperationLogs);
            await ClearAsync(_db.LoginLogs);

            // 操作日志本身也被清空，所以这条要写在清空之后，否则会被自己删掉
            await _logs.WriteAsync("系统管理", "初始化数据", "已清空全部演示业务数据");

            // 打上「已初始化」标记：前端常驻提示随之消失
            var flagRow = await _db.SystemConfigs.FirstOrDefaultAsync(c => c.ConfigKey == DemoConfigKey);
            if (flagRow == null)
                _db.SystemConfigs.Add(new SystemConfig { ConfigKey = DemoConfigKey, ConfigValue = DemoFlagValue });
            else
            {
                flagRow.ConfigValue = DemoFlagValue;
                flagRow.UpdatedAt = DateTime.Now;
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult<InitializeDataResultDto>.Ok(backup);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("初始化数据失败：" + ex.Message, ex);
        }
    }

    /// <summary>
    /// 整表清空。刻意用「取出来再 RemoveRange」而不是 ExecuteDeleteAsync：
    /// InMemory（单元/集成测试用的库）不支持 ExecuteDelete，用它这条路径就永远测不到，
    /// 而这里清的是演示数据（量级小），多一次查询换「这条路径真的有测试守着」是划算的。
    /// </summary>
    private async Task ClearAsync<T>(IQueryable<T> query) where T : class
    {
        var rows = await query.ToListAsync();
        _db.RemoveRange(rows);
    }

    /// <summary>
    /// 清空前尽力做一次物理备份。非关系库（测试用 InMemory）没有 BACKUP 能力，直接跳过；
    /// 备份抛异常也只记原因，不阻断清空。
    /// </summary>
    private async Task<InitializeDataResultDto> TryBackupBeforeClearAsync()
    {
        if (!_db.Database.IsRelational() || string.IsNullOrWhiteSpace(_connString))
            return new InitializeDataResultDto();

        try { return new InitializeDataResultDto { BackupPath = await BackupToFileAsync() }; }
        catch (Exception ex) { return new InitializeDataResultDto { BackupError = ex.Message }; }
    }
}
