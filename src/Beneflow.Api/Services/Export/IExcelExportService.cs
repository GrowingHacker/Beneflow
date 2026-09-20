using ClosedXML.Excel;

namespace Beneflow.Api.Services;

/// <summary>Excel 列类型，决定数字格式与合计行为</summary>
public enum ExcelColumnType { Text, Number, Money, Date, DateTime }

/// <summary>导出列定义</summary>
public class ExcelColumn
{
    public string Field { get; set; } = "";
    public string Title { get; set; } = "";
    public ExcelColumnType Type { get; set; } = ExcelColumnType.Text;
    /// <summary>该列在合计行显示“合计”标签（默认第一列）；数值列填合计</summary>
    public bool IsLabel { get; set; }
}

/// <summary>通用导出报表描述</summary>
public class ExcelReport
{
    public string SheetName { get; set; } = "Sheet1";
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "export.xlsx";
    public List<ExcelColumn> Columns { get; set; } = new();
    /// <summary>行数据：每行为字段名→值的字典</summary>
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    /// <summary>非空则在末尾追加合计行：数值列汇总，标签列显示 SummaryLabel</summary>
    public string? SummaryLabel { get; set; }
    /// <summary>参与合计的数值字段名（Type=Number/Money）</summary>
    public List<string> SummaryFields { get; set; } = new();
    /// <summary>
    /// 合计剔除规则字段名：当某行的该字段值命中 <see cref="SummaryExcludeValues"/> 时，
    /// 该行仍照常输出为数据行，但不参与合计（例如「已作废」单据不进合计）。
    /// </summary>
    public string? SummaryExcludeField { get; set; }
    /// <summary>命中即不参与合计的字段值</summary>
    public List<string> SummaryExcludeValues { get; set; } = new();
}

/// <summary>基于 ClosedXML 的美观 Excel 生成器，统一样式</summary>
public interface IExcelExportService
{
    /// <summary>按 ExcelReport 描述生成 .xlsx 字节流</summary>
    byte[] Build(ExcelReport report);

    /// <summary>按相同数据描述生成 UTF-8 BOM 的 CSV 文本（含表头/数据/合计行）</summary>
    string BuildCsv(ExcelReport report);
}
