using System.Text.Json;
using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>系统设置</summary>
public interface ISettingService
{
    Task<object> GetAsync();
    Task<ApiResult> SaveAsync(JsonElement body);
    /// <summary>备份文件目录（自动备份守护用于判断当天是否已有备份）</summary>
    string BackupDirPath { get; }
    /// <summary>全量备份数据库到本地备份目录（自动/手动共用），返回 .bak 文件完整路径</summary>
    Task<string> BackupToFileAsync();
    /// <summary>从上传的 .bak 恢复数据库（覆盖现有全部数据）</summary>
    Task<ApiResult> RestoreAsync(Stream bakStream, string fileName);

    /// <summary>当前是否仍是演示数据（= 从未执行过初始化）</summary>
    Task<bool> IsDemoDataAsync();

    /// <summary>
    /// 初始化数据：清空全部演示业务数据（商品/库存/进货/销售/赊账/盘点/日志），
    /// 保留账号、角色、菜单与系统设置。<paramref name="confirmText"/> 必须为「清空」。
    /// </summary>
    Task<ApiResult<InitializeDataResultDto>> ClearDemoDataAsync(string? confirmText);
}
