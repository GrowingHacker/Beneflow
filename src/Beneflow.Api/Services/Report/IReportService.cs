namespace Beneflow.Api.Services;

/// <summary>财务报表与首页看板</summary>
public interface IReportService
{
    Task<object> DailySalesAsync(DateTime date);
    Task<object> MonthlySalesAsync(int year, int month);
    Task<object> ProfitAnalysisAsync(DateTime from, DateTime to);
    Task<object> SupplierStatementAsync(int supplierId, string? dateFrom, string? dateTo);
    Task<object> CreditSummaryAsync(string? dateFrom, string? dateTo);
    Task<int> GetExpiryDaysAsync();
    Task<object> DashboardSummaryAsync(int expiryDays = 30);
}
