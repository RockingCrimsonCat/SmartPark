using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.IdentityModel.Tokens;
using SmartPark.Data;
using SmartPark.Data.Models;

namespace SmartPark.Server.Infrastructure.Security;

/// <summary>Ролі системи (див. розділ 3 «Характеристика користувачів»).</summary>
public static class Roles
{
    public const string Driver = "Driver";
    public const string Corporate = "Corporate";
    public const string Operator = "Operator";
    public const string ParkingAdmin = "ParkingAdmin";
    public const string Technician = "Technician";
    public const string TariffManager = "TariffManager";
    public const string Finance = "Finance";
    public const string SysAdmin = "SysAdmin";

    public const string Staff = "Operator,ParkingAdmin,Technician,TariffManager,Finance,SysAdmin";
    public const string Network = "ParkingAdmin,SysAdmin";

    public static readonly string[] All =
        { Driver, Corporate, Operator, ParkingAdmin, Technician, TariffManager, Finance, SysAdmin };
}

public static class ClaimsExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public class JwtOptions
{
    public string Issuer { get; set; } = "SmartPark";
    public string Audience { get; set; } = "SmartPark";
    public string Key { get; set; } = "";
    public int LifetimeMinutes { get; set; } = 480;
}

/// <summary>Видача JWT-токена з ролями користувача.</summary>
public class JwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(JwtOptions options) => _options = options;

    public string Create(AppUser user, IEnumerable<string> roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Email ?? user.UserName ?? ""),
            new("fullName", user.FullName)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            expires: DateTime.UtcNow.AddMinutes(_options.LifetimeMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>Поточний користувач для журналу аудиту.</summary>
public class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;

    public HttpCurrentUser(IHttpContextAccessor http) => _http = http;

    public Guid? Id
    {
        get
        {
            var value = _http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public string Name
    {
        get
        {
            var ctx = _http.HttpContext;
            if (ctx is null) return "system";
            if (ctx.Request.Headers.ContainsKey(EdgeKeyAttribute.Header)) return "edge-gateway";
            return ctx.User.Identity?.Name ?? "anonymous";
        }
    }
}

/// <summary>Перевірка ключа edge-шлюзу (симулятора) у заголовку X-Edge-Key.</summary>
public class EdgeKeyAttribute : Attribute, IAuthorizationFilter
{
    public const string Header = "X-Edge-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Edge:ApiKey"];
        if (!context.HttpContext.Request.Headers.TryGetValue(Header, out var key) || key != expected)
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid edge key" });
    }
}
