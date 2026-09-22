using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/roles")]
public class RolesController : BaseApiController
{
    private readonly IRoleService _svc;
    public RolesController(IRoleService svc) => _svc = svc;

    /// <summary>角色列表</summary>
    [HttpGet]
    public async Task<ApiResult<List<RoleListItemDto>>> List() =>
        ApiResult<List<RoleListItemDto>>.Ok(await _svc.ListAsync());

    /// <summary>查询角色已分配的菜单 ID 集合（权限勾选回显用）</summary>
    [HttpGet("{id:int}/menus")]
    public async Task<ApiResult<List<int>>> MenuIds(int id) =>
        ApiResult<List<int>>.Ok(await _svc.MenuIdsAsync(id));

    /// <summary>新增角色；名称和编码必填</summary>
    [HttpPost]
    public async Task<ApiResult<IdResultDto>> Create([FromBody] RoleBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || string.IsNullOrWhiteSpace(b.Code))
            return ApiResult<IdResultDto>.Fail("角色名称和编码不能为空");
        return await _svc.CreateAsync(b.Name.Trim(), b.Code.Trim(), b.Desc);
    }

    /// <summary>修改角色名称与描述</summary>
    [HttpPut("{id:int}")]
    public async Task<ApiResult> Update(int id, [FromBody] RoleBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name))
            return ApiResult.Fail("角色名称不能为空");
        return await _svc.UpdateAsync(id, b.Name.Trim(), b.Desc);
    }

    /// <summary>删除角色</summary>
    [HttpDelete("{id:int}")]
    public Task<ApiResult> Delete(int id) => _svc.DeleteAsync(id);

    /// <summary>权限分配：{ menuIds: [..] }</summary>
    [HttpPut("{id:int}/permissions")]
    public Task<ApiResult> SetPermissions(int id, [FromBody] RoleSetPermsDto dto)
        => _svc.SetPermissionsAsync(id, dto.MenuIds);

    public class RoleBody { public string? Name { get; set; } public string? Code { get; set; } public string? Desc { get; set; } }
}
