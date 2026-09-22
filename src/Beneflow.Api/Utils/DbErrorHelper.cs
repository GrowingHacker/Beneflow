using Microsoft.Data.SqlClient;

namespace Beneflow.Api.Utils;

/// <summary>
/// 数据库异常识别。
///
/// <para>
/// <b>只用错误码，不要匹配异常消息文本</b>：消息按系统语言本地化，在中文 Windows 上拿到的是中文，
/// 按英文关键词匹配会一条都匹配不上，而且不报错（静默失效，比抛异常更难查）。
/// </para>
/// </summary>
public static class DbErrorHelper
{
    /// <summary>SQL Server：违反了唯一索引（Cannot insert duplicate key row in object ... with unique index）。</summary>
    private const int DuplicateKeyInUniqueIndex = 2601;

    /// <summary>SQL Server：违反了唯一约束（Violation of UNIQUE KEY constraint / PRIMARY KEY）。</summary>
    private const int UniqueConstraintViolation = 2627;

    /// <summary>
    /// 异常链里是否藏着一个「唯一索引 / 唯一约束冲突」。
    ///
    /// <para>
    /// 用途：把「并发撞唯一索引」这类技术异常翻译成业务失败，别让它冒泡成 500。
    /// 典型场景是两个请求同时新增同一个条码 —— 前置查重会双双通过，只有唯一索引能兜住。
    /// </para>
    /// </summary>
    public static bool IsUniqueViolation(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SqlException sql &&
                (sql.Number == DuplicateKeyInUniqueIndex || sql.Number == UniqueConstraintViolation))
                return true;
        }
        return false;
    }
}
