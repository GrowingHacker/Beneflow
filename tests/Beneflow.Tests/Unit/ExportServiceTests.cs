using System.Text;
using Beneflow.Api.Services;
using ClosedXML.Excel;

namespace Beneflow.Tests.Unit;

/// <summary>Excel/CSV 导出测试：基于 ClosedXML，能反读 .xlsx 验证内容；CSV 走 BOM + 解析</summary>
public class ExportServiceTests
{
    private static ExcelReport SampleReport(bool withSummary = true) => new()
    {
        SheetName = "商品库存",
        Title = "商品库存报表",
        FileName = "stock.xlsx",
        Columns = new List<ExcelColumn>
        {
            new() { Field = "name",    Title = "名称", Type = ExcelColumnType.Text,  IsLabel = true },
            new() { Field = "qty",     Title = "数量", Type = ExcelColumnType.Number },
            new() { Field = "amount",  Title = "金额", Type = ExcelColumnType.Money },
            new() { Field = "expire",  Title = "到期", Type = ExcelColumnType.Date },
        },
        Rows = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "可乐",   ["qty"] = 10,    ["amount"] = 35.5m, ["expire"] = new DateTime(2026, 12, 1) },
            new() { ["name"] = "雪碧",   ["qty"] = 5,     ["amount"] = 18.0m, ["expire"] = new DateTime(2027, 1, 15) },
        },
        SummaryFields = withSummary ? new() { "qty", "amount" } : new(),
        SummaryLabel = withSummary ? "合计" : null,
    };

    [Fact]
    public void Build_EmptyReport_ReturnsValidWorkbook()
    {
        var bytes = new ExcelExportService().Build(new ExcelReport { SheetName = "空", Title = "空表" });
        Assert.NotEmpty(bytes);
        Assert.Equal(0x50, bytes[0]);                            // ZIP 头 "PK"
    }

    [Fact]
    public void Build_RendersTitleHeaderAndDataRows()
    {
        using var ms = new MemoryStream(new ExcelExportService().Build(SampleReport()));
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet("商品库存");

        Assert.Equal("商品库存报表", ws.Cell(1, 1).GetString());     // 标题行
        Assert.Equal("名称", ws.Cell(2, 1).GetString());
        Assert.Equal("数量", ws.Cell(2, 2).GetString());
        Assert.Equal("可乐", ws.Cell(3, 1).GetString());
        Assert.Equal(10, ws.Cell(3, 2).GetDouble(), 0);
        Assert.Equal(35.5, ws.Cell(3, 3).GetDouble(), 0.001);
        // 日期列按 ToString 写入：默认走当前 culture，得到完整日期时间
        Assert.Contains("2026/12/1", ws.Cell(3, 4).GetString());
    }

    [Fact]
    public void Build_AppendsSummaryRow_WhenSummaryFieldsPresent()
    {
        using var ms = new MemoryStream(new ExcelExportService().Build(SampleReport()));
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet("商品库存");

        // 标题 1 行 + 表头 1 行 + 2 数据行 = 合计在第 5 行
        Assert.Equal("合计", ws.Cell(5, 1).GetString());
        Assert.Equal(15, ws.Cell(5, 2).GetDouble(), 0);
        Assert.Equal(53.5, ws.Cell(5, 3).GetDouble(), 0.001);
    }

    [Fact]
    public void Build_NoSummary_OmitsSummaryRow()
    {
        using var ms = new MemoryStream(new ExcelExportService().Build(SampleReport(withSummary: false)));
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet("商品库存");

        Assert.Equal("雪碧", ws.Cell(4, 1).GetString());           // 第 4 行仍是数据行（无合计）
    }

    [Fact]
    public void Build_FreezesTopTwoRows()
    {
        using var ms = new MemoryStream(new ExcelExportService().Build(SampleReport()));
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet("商品库存");

        Assert.True(ws.SheetView.SplitRow >= 2);                // ClosedXML 实际 FreezeRows(2) → SplitRow=2
    }

    // ==================== CSV ====================

    [Fact]
    public void BuildCsv_StartsWithUtf8Bom()
    {
        var csv = new ExcelExportService().BuildCsv(SampleReport());

        Assert.Equal('\uFEFF', csv[0]);
    }

    [Fact]
    public void BuildCsv_HeaderAndDataLines_AreCommaSeparated()
    {
        var csv = new ExcelExportService().BuildCsv(SampleReport(withSummary: false));
        var lines = csv.TrimStart('\uFEFF').Split("\n", StringSplitOptions.None);

        Assert.Contains("名称", lines[0]);
        Assert.Contains("数量", lines[0]);
        Assert.Equal(4, lines[0].Split(',').Length);  // 4 列表头单元
        Assert.Contains("可乐", lines[1]);
        Assert.Contains("35.50", lines[1]);   // 金额 F2
        Assert.Contains("\"10\"", lines[1]);          // 整数 F0 也被引号包裹
    }

    [Fact]
    public void BuildCsv_EmptyReport_OnlyHeader()
    {
        var report = new ExcelReport
        {
            Columns = new() { new() { Field = "x", Title = "X", Type = ExcelColumnType.Text, IsLabel = true } },
        };
        var csv = new ExcelExportService().BuildCsv(report);

        Assert.Equal('\uFEFF', csv[0]);
        Assert.Equal("\"X\"", csv.TrimStart('\uFEFF').Trim());
    }

    [Fact]
    public void BuildCsv_SummaryRow_AggregatesNumericColumns()
    {
        var csv = new ExcelExportService().BuildCsv(SampleReport());
        var lines = csv.TrimStart('\uFEFF').Split("\n");

        Assert.Equal(5, lines.Length);                            // 含尾部换行产生的空串
        var sum = lines[3].TrimEnd('\r');
        Assert.Contains("合计", sum);
        Assert.Contains("\"15\"", sum);
        Assert.Contains("53.50", sum);
    }

    // ============ 合计剔除规则：作废单据照常输出为数据行，但不进合计 ============

    /// <summary>三行报表：两行有效 + 一行已作废（金额 999 不得进合计）</summary>
    private static ExcelReport ReportWithVoidedRow() => new()
    {
        SheetName = "销售单列表",
        Title = "销售单列表",
        Columns = new List<ExcelColumn>
        {
            new() { Field = "orderNo", Title = "单号",  Type = ExcelColumnType.Text,  IsLabel = true },
            new() { Field = "amount",  Title = "应收",  Type = ExcelColumnType.Money },
            new() { Field = "status",  Title = "状态",  Type = ExcelColumnType.Text },
        },
        Rows = new List<Dictionary<string, object?>>
        {
            new() { ["orderNo"] = "SO001", ["amount"] = 20m,  ["status"] = "已完成" },
            new() { ["orderNo"] = "SO002", ["amount"] = 999m, ["status"] = "已作废" },
            new() { ["orderNo"] = "SO003", ["amount"] = 30m,  ["status"] = "已完成" },
        },
        SummaryLabel = "合计（不含已作废）",
        SummaryFields = new() { "amount" },
        SummaryExcludeField = "status",
        SummaryExcludeValues = { "已作废" },
    };

    [Fact]
    public void BuildCsv_Summary_ExcludesRowsMatchingExcludeValues()
    {
        var csv = new ExcelExportService().BuildCsv(ReportWithVoidedRow());
        var lines = csv.TrimStart('\uFEFF').TrimEnd('\r', '\n').Split("\n");

        Assert.Equal(5, lines.Length);                            // 表头 + 3 数据行 + 合计行
        // 合计 = 20 + 30，作废行的 999 不计入
        Assert.Contains("50.00", lines[4]);
        Assert.DoesNotContain("1049", lines[4]);
        // 作废行本身仍照常导出，便于审计
        Assert.Contains("999.00", lines[2]);
    }

    [Fact]
    public void Build_Summary_ExcludesRowsMatchingExcludeValues()
    {
        using var ms = new MemoryStream(new ExcelExportService().Build(ReportWithVoidedRow()));
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet("销售单列表");

        Assert.Equal("SO002", ws.Cell(4, 1).GetString());          // 作废行仍在数据区
        Assert.Equal(999m, (decimal)ws.Cell(4, 2).GetDouble());
        Assert.Equal(50m, (decimal)ws.Cell(6, 2).GetDouble());     // 合计行只累计有效行
    }

    [Fact]
    public void BuildCsv_NoExcludeRule_SumsEveryRow()
    {
        var noRule = ReportWithVoidedRow();
        noRule.SummaryExcludeField = null;
        noRule.SummaryExcludeValues.Clear();

        var csv = new ExcelExportService().BuildCsv(noRule);

        Assert.Contains("1049.00", csv);                          // 未配置剔除规则时按原口径全量累加
    }
}
