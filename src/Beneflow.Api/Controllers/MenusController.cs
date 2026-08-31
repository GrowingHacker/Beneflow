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
    public async Task<ApiResult<List<object>>> Tree() =>
        ApiResult<List<object>>.Ok(await _svc.TreeAsync());

    [HttpPost]
    public async Task<ApiResult<object>> Create([FromBody] MenuBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || string.IsNullOrWhiteSpace(b.Type))
            return ApiResult<object>.Fail("菜单名称和类型不能为空");
        if (b.Type != "目录" && string.IsNullOrWhiteSpace(b.PermCode))
            return ApiResult<object>.Fail("菜单/按钮必须填写权限码");
        return await _svc.CreateAsync(b.Name.Trim(), b.Type.Trim(), b.PermCode, b.ParentId, b.Sort);
    }

    [HttpPut("{id:int}")]
    public async Task<ApiResult> Update(int id, [FromBody] MenuBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name))
            return ApiResult.Fail("菜单名称不能为空");
        return await _svc.UpdateAsync(id, b.Name.Trim(), b.PermCode, b.Sort);
    }

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
