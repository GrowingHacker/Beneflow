using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/settings")]
public class SettingsController : BaseApiController
{
    private readonly ISettingService _svc;
    public SettingsController(ISettingService svc) => _svc = svc;

    /// <summary>读取全部系统设置（按 shop/sale/receipt/stock 等分组返回）</summary>
    [HttpGet]
    public async Task<ApiResult<object>> Get() => ApiResult<object>.Ok(await _svc.GetAsync());

    /// <summary>整体保存：{ shop:{...}, sale:{...}, receipt:{...}, stock:{...}, units:[], payMethods:[] }</summary>
    [HttpPut]
    public Task<ApiResult> Save([FromBody] System.Text.Json.JsonElement body) => _svc.SaveAsync(body);

    /// <summary>
    /// 立即全量备份：生成 .bak 并作为文件下载。
    /// 极小概率下备份会退到 SQL Server 自己的备份目录（见 <c>SettingService.BackupToFileAsync</c>），
    /// 那里 app 账号可能读不到 —— 那种情况也**只报「读不到」并给出路径**，别让它变成一句泛化的 500。
    /// </summary>
    [HttpPost("backup")]
    public async Task<IActionResult> Backup()
    {
        var path = await _svc.BackupToFileAsync();
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return File(stream, "application/octet-stream", Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResult.Fail($"备份已生成，但当前运行账号没有读取权限：{path}"));
        }
    }

    /// <summary>上传 .bak 恢复数据库（覆盖现有全部数据，恢复期间服务短暂不可用）</summary>
    [HttpPost("restore")]
    public async Task<ApiResult> Restore(IFormFile file) => await _svc.RestoreAsync(file.OpenReadStream(), file.FileName);

    /// <summary>当前是否仍是演示数据（true=种子数据，前端据此常驻提示「当前数据为演示数据」）</summary>
    [HttpGet("demo-data")]
    public async Task<ApiResult<DemoDataStatusDto>> DemoData()
        => ApiResult<DemoDataStatusDto>.Ok(new DemoDataStatusDto { IsDemoData = await _svc.IsDemoDataAsync() });

    /// <summary>
    /// 初始化数据：清空全部演示业务数据（商品/库存/进货/销售/赊账/盘点/日志），
    /// **保留**账号、角色、菜单与系统设置。仅店主可执行，且需在 body 里原样提交「清空」作为确认；
    /// 清空前会尽力做一次物理备份（备份失败不阻断，原因随结果返回）。
    /// </summary>
    [HttpPost("demo-data/clear")]
    public async Task<ApiResult<InitializeDataResultDto>> ClearDemoData([FromBody] ClearDemoDataDto dto)
    {
        if (!IsOwner) return ApiResult<InitializeDataResultDto>.Fail("只有店主可以初始化数据");
        return await _svc.ClearDemoDataAsync(dto.Confirm);
    }

    /// <summary>是否店主：角色名「店主」或拥有通配权限（与 <c>UsersController</c> 同一判据）</summary>
    private bool IsOwner =>
        User.IsInRole("店主") || User.HasClaim(c => c.Type == "permission" && c.Value == "*");
}
