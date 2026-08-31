using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/users")]
public class UsersController : BaseApiController
{
    private readonly IUserService _svc;
    public UsersController(IUserService svc) => _svc = svc;

    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.ListAsync(keyword, page, pageSize));

    [HttpPost]
    public Task<ApiResult<object>> Create([FromBody] UserCreateDto dto) => _svc.CreateAsync(dto);

    /// <summary>编辑用户；body 可带 password 重置密码（仅店主）</summary>
    [HttpPut("{id:int}")]
    public async Task<ApiResult> Update(int id, [FromBody] UserUpdateDto dto)
        => await _svc.UpdateAsync(id, dto, IsAdmin);

    /// <summary>启用/禁用：{ status: "启用" | "禁用" }</summary>
    [HttpPut("{id:int}/status")]
    public async Task<ApiResult> Toggle(int id, [FromBody] StatusBody body)
        => await _svc.ToggleAsync(id, body.Status ?? "禁用");

    [HttpDelete("{id:int}")]
    public Task<ApiResult> Delete(int id) => _svc.DeleteAsync(id);

    private bool IsAdmin =>
        User.IsInRole("店主") || User.HasClaim(c => c.Type == "permission" && c.Value == "*");

    public class StatusBody { public string? Status { get; set; } }
}
