using ClosedXML.Excel;
using System.Text;

namespace Beneflow.Api.Services;

/// <summary>
/// 基于 ClosedXML 的美观 Excel 生成器。统一样式：
/// 第1行标题（合并、加粗居中、浅蓝底）
/// 第2行表头（蓝底白字加粗居中、细边框）
/// 数据行细边框，金额列 ¥#,##0.00、日期列 yyyy-MM-dd、整数列 #,##0
/// 冻结前2行、自动列宽、可选合计行（黄底加粗）
/// </summary>
public class ExcelExportService : IExcelExportService
{
    private const string HeaderBg = "#409eff";   // Element Plus 主色
    private const string TitleBg  = "#E8F3FF";   // 浅蓝
    private const string SummaryBg = "#FFF3CD";  // 淡黄

    public byte[] Build(ExcelReport report)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(report.SheetName);
        var cols = report.Columns.Count;
        if (cols == 0) return ToBytes(wb);

        // ---- 第1行：标题（合并所有列）----
        ws.Cell(1, 1).Value = report.Title;
        ws.Range(1, 1, 1, cols).Merge();
        var titleRow = ws.Row(1);
        titleRow.Style.Font.FontSize = 14;
        titleRow.Style.Font.Bold = true;
        titleRow.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        titleRow.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        titleRow.Style.Fill.BackgroundColor = XLColor.FromHtml(TitleBg);
        titleRow.Height = 28;

        // ---- 第2行：表头 ----
        for (var i = 0; i < cols; i++)
        {
            var c = ws.Cell(2, i + 1);
            c.Value = report.Columns[i].Title;
            c.Style.Font.Bold = true;
            c.Style.Font.FontColor = XLColor.White;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml(HeaderBg);
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        // ---- 数据行 ----
        var r = 3;
        foreach (var row in report.Rows)
        {
            for (var i = 0; i < cols; i++)
            {
                var col = report.Columns[i];
                if (!row.TryGetValue(col.Field, out var v) || v == null) continue;
                var cell = ws.Cell(r, i + 1);
                switch (col.Type)
                {
                    case ExcelColumnType.Money:
                    case ExcelColumnType.Number:
                        cell.Value = Convert.ToDouble(v);
                        cell.Style.NumberFormat.Format = col.Type == ExcelColumnType.Money ? "¥#,##0.00" : "#,##0";
                        break;
                    case ExcelColumnType.Date:
                    case ExcelColumnType.DateTime:
                    case ExcelColumnType.Text:
                    default:
                        cell.Value = v.ToString();
                        break;
                }
            }
            r++;
        }

        // ---- 合计行 ----
        var hasSummary = !string.IsNullOrEmpty(report.SummaryLabel) && report.SummaryFields.Count > 0;
        if (hasSummary)
        {
            var sums = new Dictionary<string, double>();
            foreach (var f in report.SummaryFields)
                sums[f] = 0;
            foreach (var row in report.Rows)
                foreach (var f in report.SummaryFields)
                    if (row.TryGetValue(f, out var v) && v != null && double.TryParse(v.ToString(), out var d))
                        sums[f] += d;

            for (var i = 0; i < cols; i++)
            {
                var col = report.Columns[i];
                var cell = ws.Cell(r, i + 1);
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml(SummaryBg);
                if (col.IsLabel)
                {
                    cell.Value = report.SummaryLabel;
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }
                else if (sums.ContainsKey(col.Field))
                {
                    cell.Value = sums[col.Field];
                    cell.Style.NumberFormat.Format = col.Type == ExcelColumnType.Money ? "¥#,##0.00" : "#,##0";
                }
            }
            r++;
        }

        // ---- 边框（表头到末行）----
        var borderRange = ws.Range(2, 1, r - 1, cols);
        var bd = borderRange.Style.Border;
        bd.OutsideBorder = XLBorderStyleValues.Thin;
        bd.InsideBorder = XLBorderStyleValues.Thin;
        bd.OutsideBorderColor = XLColor.FromHtml("#C0C4CC");
        bd.InsideBorderColor = XLColor.FromHtml("#C0C4CC");

        // ---- 列宽自适应：手动按 CJK 显示宽度计算（ClosedXML AdjustToContents 对中文按1字符算偏窄）----
        for (var c = 0; c < cols; c++)
        {
            var field = report.Columns[c].Field;
            double maxW = DisplayWidth(report.Columns[c].Title);
            foreach (var row in report.Rows)
                if (row.TryGetValue(field, out var v) && v != null)
                    maxW = Math.Max(maxW, DisplayWidth(v.ToString()));
            if (hasSummary && report.Columns[c].IsLabel)
                maxW = Math.Max(maxW, DisplayWidth(report.SummaryLabel ?? ""));
            ws.Column(c + 1).Width = Math.Clamp(maxW + 2, 8, 60);
        }
        ws.SheetView.FreezeRows(2);

        return ToBytes(wb);
    }

    /// <summary>字符串显示宽度：CJK/全角字符按 2，其余按 1</summary>
    private static double DisplayWidth(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        double w = 0;
        foreach (var ch in s)
        {
            // CJK 统一表意/康熙/CJK扩展/韩文音节/全角符号
            if ((ch >= 0x2E80 && ch <= 0x9FFF) ||
                (ch >= 0xAC00 && ch <= 0xD7AF) ||
                (ch >= 0xF900 && ch <= 0xFAFF) ||
                (ch >= 0xFF00 && ch <= 0xFFEF))
                w += 2;
            else
                w += 1;
        }
        return w;
    }

    private static byte[] ToBytes(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>生成 UTF-8 BOM 的 CSV 文本：表头+数据行+合计行（金额 F2、整数 F0，纯数字便于程序解析）</summary>
    public string BuildCsv(ExcelReport report)
    {
        var sb = new StringBuilder();
        sb.Append('\ufeff'); // BOM：Excel 双击打开不乱码
        sb.AppendLine(string.Join(",", report.Columns.Select(c => Esc(c.Title))));

        foreach (var row in report.Rows)
            sb.AppendLine(string.Join(",", report.Columns.Select(c =>
                Esc(row.TryGetValue(c.Field, out var v) && v != null ? Format(v, c.Type) : ""))));

        if (!string.IsNullOrEmpty(report.SummaryLabel) && report.SummaryFields.Count > 0)
        {
            var sums = new Dictionary<string, double>();
            foreach (var f in report.SummaryFields) sums[f] = 0;
            foreach (var row in report.Rows)
                foreach (var f in report.SummaryFields)
                    if (row.TryGetValue(f, out var v) && v != null && double.TryParse(v.ToString(), out var d))
                        sums[f] += d;
            sb.AppendLine(string.Join(",", report.Columns.Select(c =>
            {
                if (c.IsLabel) return Esc(report.SummaryLabel!);
                if (sums.ContainsKey(c.Field))
                    return Esc(c.Type == ExcelColumnType.Money ? sums[c.Field].ToString("F2") : sums[c.Field].ToString("F0"));
                return Esc("");
            })));
        }
        return sb.ToString();
    }

    /// <summary>按列类型把值格式化为 CSV 单元文本（金额 2 位小数、整数 0 位、其余原值）</summary>
    private static string Format(object v, ExcelColumnType t) => t switch
    {
        ExcelColumnType.Money => Convert.ToDouble(v).ToString("F2"),
        ExcelColumnType.Number => Convert.ToDouble(v).ToString("F0"),
        _ => v.ToString() ?? "",
    };

    /// <summary>CSV 单元转义：含逗号/引号/换行则用双引号包裹，内部引号双写</summary>
    private static string Esc(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
}
