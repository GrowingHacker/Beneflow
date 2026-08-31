using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

/// <summary>
/// 所有业务 API 的基类：默认需要登录认证。
/// 公开接口（如登录）在子类上显式打 [AllowAnonymous] 即可覆盖。
/// </summary>
[ApiController]
[Authorize]
public abstract class BaseApiController : ControllerBase { }
