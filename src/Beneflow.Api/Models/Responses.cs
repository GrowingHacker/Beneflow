using System.Text.Json.Serialization;

namespace Beneflow.Api.Models;

// =====================================================================================
// 响应侧 DTO（接口返回给前端的 data）
// -------------------------------------------------------------------------------------
// 为什么单独一个文件：请求侧 DTO（前端提交上来的 body）在 Dtos.cs 里，它按「谁提交」
// 组织；这里的东西按「谁消费」组织 —— 前端页面读哪些字段、Excel 导出的表头取哪些列。
// 两件事会各自演化，混在一个文件里改一处要翻全文。
//
// 约定：
// 1. 属性名用 PascalCase，序列化由 ASP.NET Core 默认的 camelCase 策略转成 camelCase；
//    所以属性名必须与该接口原来匿名对象里的键**逐字对应**（改一个字就是改接口契约）。
// 2. 曾经 `ApiResult<object>.Ok(new { ... })` 的写法改成 `ApiResult<XxxDto>.Ok(new XxxDto { ... })`，
//    编译器从此能拦住「返回体改了、前端没改」这类漏改；前端引用的字段名靠单元测试的
//    反射断言（`P<T>(r, "field")`）兜底。
// 3. EF 查询里的 `select new { ... }` 是**查询投影**，不是响应体，保持匿名即可 ——
//    它只活在方法内部，没有契约可言。
// =====================================================================================

/// <summary>只回一个新建/命中的主键，用于「创建成功只告诉前端 id」这类响应</summary>
public class IdResultDto
{
    public int Id { get; set; }
}

// ==================== 认证 ====================

/// <summary>登录成功的响应：令牌 + 当前账号（含权限码）</summary>
public class LoginResultDto
{
    public string Token { get; set; } = "";
    public LoginUserDto User { get; set; } = new();
}

/// <summary>登录后下发给前端的当前账号信息</summary>
public class LoginUserDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";

    /// <summary>权限码列表；店主为 ["*"]，前端据此判全量放行</summary>
    public List<string> Permissions { get; set; } = new();
}

// ==================== 供应商 ====================

/// <summary>供应商列表行（含累计采购与最近供货，用于列表页与「能否删除」判断）</summary>
public class SupplierListItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Contact { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string Remark { get; set; } = "";

    /// <summary>中文状态（"启用"/"停用"）—— 前端直接显示，不自己翻译布尔</summary>
    public string Status { get; set; } = "";

    /// <summary>累计采购金额</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>最近一次供货日期（yyyy-MM-dd）；从未供货为 null</summary>
    public string? LastDate { get; set; }

    /// <summary>是否已有采购记录（有则不允许删除，只能停用）</summary>
    public bool HasPurchase { get; set; }

    /// <summary>列表页未展示，保留字段以维持与表格列的一一对应</summary>
    public string CreatedAt { get; set; } = "";
}

/// <summary>供应商详情/新增回显</summary>
public class SupplierDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Contact { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Remark { get; set; }
    public string Status { get; set; } = "";
}

// ==================== 用户 ====================

/// <summary>用户列表行</summary>
public class UserListItemDto
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Phone { get; set; }

    /// <summary>角色名；未分配角色为空串</summary>
    public string Role { get; set; } = "";

    /// <summary>角色编码（前端按 owner 等编码做特判）</summary>
    public string? RoleCode { get; set; }

    public string Status { get; set; } = "";
    public string? Remark { get; set; }
    public string CreatedAt { get; set; } = "";
}

// ==================== 角色 ====================

/// <summary>角色列表行（含关联用户数，用于「已关联用户不可删」提示）</summary>
public class RoleListItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Status { get; set; } = "";
    public int UserCount { get; set; }
}

// ==================== 菜单 ====================

/// <summary>菜单树节点（children 递归）</summary>
public class MenuNodeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? PermCode { get; set; }
    public int Sort { get; set; }
    public bool Visible { get; set; }
    public List<MenuNodeDto> Children { get; set; } = new();
}

// ==================== 操作日志 ====================

/// <summary>操作日志列表行</summary>
public class OperationLogItemDto
{
    /// <summary>OperationLog.Id 是 long（自增日志表，单表可能超过 int 上限）</summary>
    public long Id { get; set; }
    public string UserName { get; set; } = "";
    public string Module { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Ip { get; set; }
    public string CreatedAt { get; set; } = "";
}
