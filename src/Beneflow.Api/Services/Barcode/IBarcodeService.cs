namespace Beneflow.Api.Services;

/// <summary>条形码联网查询</summary>
public interface IBarcodeService
{
    Task<BarcodeInfo?> LookupAsync(string code);
}
