using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/roles")]
public class RolesController : BaseApiController
{
    private readonly IRoleService _svc;
    public RolesController(IRoleService svc) => _svc = svc;

    [HttpGet]
    public async Task<ApiResult<List<object>>> List() =>
        ApiResult<List<object>>.Ok(await _svc.ListAsync());

    [HttpGet("{id:int}/menus")]
    public async Task<ApiResult<List<int>>> MenuIds(int id) =>
        ApiResult<List<int>>.Ok(await _svc.MenuIdsAsync(id));

    [HttpPost]
    public async Task<ApiResult<object>> Create([FromBody] RoleBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || string.IsNullOrWhiteSpace(b.Code))
            return ApiResult<object>.Fail("角色名称和编码不能为空");
        return await _svc.CreateAsync(b.Name.Trim(), b.Code.Trim(), b.Desc);
    }

    [HttpPut("{id:int}")]
    public async Task<ApiResult> Update(int id, [FromBody] RoleBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name))
            return ApiResult.Fail("角色名称不能为空");
        return await _svc.UpdateAsync(id, b.Name.Trim(), b.Desc);
    }

    [HttpDelete("{id:int}")]
    public Task<ApiResult> Delete(int id) => _svc.DeleteAsync(id);

    /// <summary>权限分配：{ menuIds: [..] }</summary>
    [HttpPut("{id:int}/permissions")]
    public Task<ApiResult> SetPermissions(int id, [FromBody] RoleSetPermsDto dto)
        => _svc.SetPermissionsAsync(id, dto.MenuIds);

    public class RoleBody { public string? Name { get; set; } public string? Code { get; set; } public string? Desc { get; set; } }
}
