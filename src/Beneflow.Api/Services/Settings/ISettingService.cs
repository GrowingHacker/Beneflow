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
}
