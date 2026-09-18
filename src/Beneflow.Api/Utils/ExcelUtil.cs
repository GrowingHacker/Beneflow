using ClosedXML.Excel;

namespace Beneflow.Api.Utils;

/// <summary>
/// Excel 读取工具。导入功能统一从这里打开工作簿，把第三方库的异常挡在业务入口，
/// 避免用户拿到一个「服务器内部错误」而不知道该怎么改。
/// </summary>
public static class ExcelUtil
{
    /// <summary>
    /// 打开工作簿，打不开返回 null（由调用方转成中文业务失败）。
    ///
    /// ClosedXML 只支持 OpenXML（.xlsx / .xlsm）：旧版 .xls（BIFF）、
    /// 被改名成 .xlsx 的 CSV、损坏或加密的文件都会在打开时抛异常。
    /// 若不在这里兜住，异常会冒到 Program.cs 的全局兜底变成 500「服务器内部错误」——
    /// 而「从旧软件导出 .xls 再上传」是很常见的操作，必须给出「另存为 .xlsx」这种可操作提示。
    /// </summary>
    public static XLWorkbook? TryOpen(string path)
    {
        try { return new XLWorkbook(path); }
        catch { return null; }
    }
}
