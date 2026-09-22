using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/menus")]
public class MenusController : BaseApiController
{
    private readonly IMenuService _svc;
    public MenusController(IMenuService svc) => _svc = svc;

    /// <summary>菜单树</summary>
    [HttpGet]
    public async Task<ApiResult<List<MenuNodeDto>>> Tree() =>
        ApiResult<List<MenuNodeDto>>.Ok(await _svc.TreeAsync());

    /// <summary>新增菜单；「菜单/按钮」类型必须填写权限码</summary>
    [HttpPost]
    public async Task<ApiResult<IdResultDto>> Create([FromBody] MenuBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || string.IsNullOrWhiteSpace(b.Type))
            return ApiResult<IdResultDto>.Fail("菜单名称和类型不能为空");
        if (b.Type != "目录" && string.IsNullOrWhiteSpace(b.PermCode))
            return ApiResult<IdResultDto>.Fail("菜单/按钮必须填写权限码");
        return await _svc.CreateAsync(b.Name.Trim(), b.Type.Trim(), b.PermCode, b.ParentId, b.Sort);
    }

    /// <summary>修改菜单名称、权限码与排序</summary>
    [HttpPut("{id:int}")]
    public async Task<ApiResult> Update(int id, [FromBody] MenuBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name))
            return ApiResult.Fail("菜单名称不能为空");
        return await _svc.UpdateAsync(id, b.Name.Trim(), b.PermCode, b.Sort);
    }

    /// <summary>删除菜单</summary>
    [HttpDelete("{id:int}")]
    public Task<ApiResult> Delete(int id) => _svc.DeleteAsync(id);

    public class MenuBody
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? PermCode { get; set; }
        public int? ParentId { get; set; }
        public int Sort { get; set; }
    }
}
