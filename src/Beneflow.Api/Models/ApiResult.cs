namespace Beneflow.Api.Models;

/// <summary>
/// 统一响应格式：{ code, message, data }
/// </summary>
public class ApiResult
{
    /// <summary>0 = 成功；非 0 = 业务失败（技术异常另走全局兜底，不会以本结构返回）</summary>
    public int Code { get; set; }

    /// <summary>提示信息：成功为 "success"，业务失败为可直接展示给用户的中文原因</summary>
    public string Message { get; set; } = "success";

    /// <summary>
    /// 返回数据。本项目的非泛型包络**约定不带数据** —— 用它的接口都是「改 / 删 / 启停 / 作废 / 结算」
    /// 这类只表达「操作已完成」的动作，所以接口文档里看到 data 是空对象 <c>{}</c> 属正常，
    /// 不必去找它的字段。要返回数据的接口请用 <see cref="ApiResult{T}"/>，
    /// Swagger 里才会显示 data 的实际结构。
    /// </summary>
    public object? Data { get; set; }

    public static ApiResult Ok() => new() { Code = 0 };
    public static ApiResult Ok(object? data) => new() { Code = 0, Data = data };
    public static ApiResult Fail(string message, int code = 1) => new() { Code = code, Message = message };
}

public class ApiResult<T>
{
    /// <summary>同 <see cref="ApiResult.Code"/></summary>
    public int Code { get; set; }

    /// <summary>同 <see cref="ApiResult.Message"/></summary>
    public string Message { get; set; } = "success";

    /// <summary>成功时的返回数据；业务失败时为 null</summary>
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
