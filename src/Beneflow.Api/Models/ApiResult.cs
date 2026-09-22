namespace Beneflow.Api.Models;

/// <summary>
/// 统一响应格式：{ code, message, data }
/// </summary>
public class ApiResult
{
    public int Code { get; set; }
    public string Message { get; set; } = "success";
    public object? Data { get; set; }

    public static ApiResult Ok() => new() { Code = 0 };
    public static ApiResult Ok(object? data) => new() { Code = 0, Data = data };
    public static ApiResult Fail(string message, int code = 1) => new() { Code = code, Message = message };
}

public class ApiResult<T>
{
    public int Code { get; set; }
    public string Message { get; set; } = "success";
    public T? Data { get; set; }

    public static ApiResult<T> Ok(T? data = default) => new() { Code = 0, Data = data };
    public static ApiResult<T> Fail(string message, int code = 1) => new() { Code = code, Message = message };
}

/// <summary>
/// 分页结果
/// </summary>
public class PagedResult<T>
{
    public IReadOnlyList<T> List { get; set; } = Array.Empty<T>();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }

    /// <summary>
    /// 可选：与本次查询同过滤条件的全量合计（非当前页合计）。
    /// 目前销售单列表、采购退货单列表使用；为 null 时整个字段不出现在响应里，不影响其它分页接口的契约。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public object? Summary { get; set; }
}
