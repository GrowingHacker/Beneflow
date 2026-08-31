namespace Beneflow.Api.Models.Entities;

/// <summary>用户表（逻辑删除 + 登录失败锁定）</summary>
public class UserInfo
{
    public int Id { get; set; }
    /// <summary>用户名，唯一，4-20 位字母或数字</summary>
    public string Username { get; set; } = "";
    /// <summary>密码哈希（PBKDF2-SHA256）</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>盐值</summary>
    public string Salt { get; set; } = "";
    /// <summary>真实姓名</summary>
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    /// <summary>状态：true=启用 / false=禁用</summary>
    public bool Status { get; set; } = true;
    public bool IsDeleted { get; set; }
    public int FailedCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? Remark { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>角色表</summary>
public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>角色编码，唯一：owner/cashier/buyer/keeper/finance</summary>
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public bool Status { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>用户-角色关联</summary>
public class UserRole
{
    public int UserId { get; set; }
    public int RoleId { get; set; }

    public UserInfo User { get; set; } = null!;
    public Role Role { get; set; } = null!;
}

/// <summary>菜单/权限表（目录-菜单-按钮三级，通过 Type 区分）</summary>
public class Menu
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>类型：目录 / 菜单 / 按钮</summary>
    public string Type { get; set; } = "菜单";
    public int? ParentId { get; set; }
    /// <summary>权限码，如 sales、stock-check；与前端 PAGE_PERMS 对齐</summary>
    public string PermCode { get; set; } = "";
    public string? Icon { get; set; }
    public int Sort { get; set; }
    public bool Visible { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>角色-菜单权限关联</summary>
public class RoleMenu
{
    public int RoleId { get; set; }
    public int MenuId { get; set; }

    public Role Role { get; set; } = null!;
    public Menu Menu { get; set; } = null!;
}

/// <summary>操作日志（不可删除，仅管理员可查）</summary>
public class OperationLog
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    /// <summary>模块，如 商品管理</summary>
    public string Module { get; set; } = "";
    /// <summary>动作，如 新增商品</summary>
    public string Action { get; set; } = "";
    /// <summary>操作对象标识，如 单号/名称</summary>
    public string? Target { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>登录日志</summary>
public class LoginLog
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>系统参数表（分组键-JSON 值存储）</summary>
public class SystemConfig
{
    public int Id { get; set; }
    /// <summary>参数键：shop / sale / receipt / stock / units / payMethods</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(50)]
    public string ConfigKey { get; set; } = "";
    public string ConfigValue { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
