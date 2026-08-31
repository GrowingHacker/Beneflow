using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/settings")]
public class SettingsController : BaseApiController
{
    private readonly ISettingService _svc;
    public SettingsController(ISettingService svc) => _svc = svc;

    [HttpGet]
    public async Task<ApiResult<object>> Get() => ApiResult<object>.Ok(await _svc.GetAsync());

    /// <summary>整体保存：{ shop:{...}, sale:{...}, receipt:{...}, stock:{...}, units:[], payMethods:[] }</summary>
    [HttpPut]
    public Task<ApiResult> Save([FromBody] System.Text.Json.JsonElement body) => _svc.SaveAsync(body);

    /// <summary>立即全量备份：生成 .bak 并作为文件下载</summary>
    [HttpPost("backup")]
    public async Task<IActionResult> Backup()
    {
        var path = await _svc.BackupToFileAsync();
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return File(stream, "application/octet-stream", Path.GetFileName(path));
    }

    /// <summary>上传 .bak 恢复数据库（覆盖现有全部数据，恢复期间服务短暂不可用）</summary>
    [HttpPost("restore")]
    public async Task<ApiResult> Restore(IFormFile file) => await _svc.RestoreAsync(file.OpenReadStream(), file.FileName);
}
