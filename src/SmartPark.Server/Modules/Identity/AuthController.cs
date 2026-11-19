using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Security;

namespace SmartPark.Server.Modules.Identity;

public record RegisterRequest(string Email, string Password, string FullName);

public record LoginRequest(string Email, string Password);

public record PaymentMethodRequest(bool Linked);

public record CreateUserRequest(string Email, string FullName, string TemporaryPassword, string[] Roles, Guid[] ParkingIds);

public record UpdateUserRequest(string FullName, string[] Roles, Guid[] ParkingIds);

/// <summary>Модуль Identity: реєстрація і вхід (UC01) на основі ASP.NET Core Identity + JWT.</summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<AppUser> _users;
    private readonly JwtTokenService _tokens;

    public AuthController(UserManager<AppUser> users, JwtTokenService tokens)
    {
        _users = users;
        _tokens = tokens;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest r)
    {
        var user = new AppUser { UserName = r.Email, Email = r.Email, FullName = r.FullName };
        var result = await _users.CreateAsync(user, r.Password);
        if (!result.Succeeded) return BadRequest(new { error = string.Join("; ", result.Errors.Select(e => e.Description)) });
        await _users.AddToRoleAsync(user, Roles.Driver);
        return Ok(await TokenResponse(user));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest r)
    {
        var user = await _users.FindByEmailAsync(r.Email);
        if (user is null) return Unauthorized(new { error = "Невірний e-mail або пароль" });
        if (user.IsBlocked) return Unauthorized(new { error = "Обліковий запис заблоковано" });
        if (await _users.IsLockedOutAsync(user))
            return Unauthorized(new { error = "Забагато невдалих спроб. Спробуйте через 15 хвилин" });

        if (!await _users.CheckPasswordAsync(user, r.Password))
        {
            await _users.AccessFailedAsync(user); // після 5 невдалих спроб – блокування на 15 хв
            return Unauthorized(new { error = "Невірний e-mail або пароль" });
        }
        await _users.ResetAccessFailedCountAsync(user);
        return Ok(await TokenResponse(user));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var user = await _users.FindByIdAsync(User.GetUserId().ToString());
        if (user is null) return NotFound();
        return Ok(new { user.Id, user.Email, user.FullName, user.HasPaymentMethod, roles = await _users.GetRolesAsync(user) });
    }

    /// <summary>Прив’язка картки – емуляція (дані картки не вводяться і не зберігаються).</summary>
    [HttpPut("payment-method")]
    [Authorize]
    public async Task<IActionResult> PaymentMethod(PaymentMethodRequest r)
    {
        var user = await _users.FindByIdAsync(User.GetUserId().ToString());
        if (user is null) return NotFound();
        user.HasPaymentMethod = r.Linked;
        await _users.UpdateAsync(user);
        return NoContent();
    }

    private async Task<object> TokenResponse(AppUser user)
    {
        var roles = await _users.GetRolesAsync(user);
        return new { token = _tokens.Create(user, roles), user = new { user.Id, user.Email, user.FullName, roles } };
    }
}

/// <summary>Керування користувачами та ролями (UC17).</summary>
[ApiController]
[Route("api/users")]
[Authorize(Roles = Roles.SysAdmin)]
public class UsersController : ControllerBase
{
    private readonly UserManager<AppUser> _users;
    private readonly AppDbContext _db;

    public UsersController(UserManager<AppUser> users, AppDbContext db)
    {
        _users = users;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var users = await _users.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync();
        var scopes = await _db.UserParkingScopes.AsNoTracking().ToListAsync();
        var result = new List<object>();
        foreach (var u in users)
            result.Add(new
            {
                u.Id, u.Email, u.FullName, u.IsBlocked, u.CreatedAt,
                roles = await _users.GetRolesAsync(u),
                parkingIds = scopes.Where(s => s.UserId == u.Id).Select(s => s.ParkingId)
            });
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest r)
    {
        if (r.Roles.Any(x => !Roles.All.Contains(x))) return BadRequest(new { error = "Невідома роль" });
        var user = new AppUser { UserName = r.Email, Email = r.Email, FullName = r.FullName, EmailConfirmed = true };
        var result = await _users.CreateAsync(user, r.TemporaryPassword);
        if (!result.Succeeded) return BadRequest(new { error = string.Join("; ", result.Errors.Select(e => e.Description)) });
        await _users.AddToRolesAsync(user, r.Roles);
        _db.UserParkingScopes.AddRange(r.ParkingIds.Select(p => new UserParkingScope { UserId = user.Id, ParkingId = p }));
        await _db.SaveChangesAsync();
        return Ok(new { user.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest r)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        if (id == User.GetUserId() && !r.Roles.Contains(Roles.SysAdmin))
            return BadRequest(new { error = "Не можна зняти з себе роль адміністратора" });

        user.FullName = r.FullName;
        await _users.UpdateAsync(user);
        var current = await _users.GetRolesAsync(user);
        await _users.RemoveFromRolesAsync(user, current.Except(r.Roles));
        await _users.AddToRolesAsync(user, r.Roles.Except(current));

        _db.UserParkingScopes.RemoveRange(_db.UserParkingScopes.Where(s => s.UserId == id));
        _db.UserParkingScopes.AddRange(r.ParkingIds.Select(p => new UserParkingScope { UserId = id, ParkingId = p }));
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Блокування замість видалення (звільнення працівника).</summary>
    [HttpPost("{id:guid}/block")]
    public async Task<IActionResult> Block(Guid id, [FromQuery] bool blocked = true)
    {
        if (id == User.GetUserId()) return BadRequest(new { error = "Не можна заблокувати себе" });
        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        user.IsBlocked = blocked;
        await _users.UpdateAsync(user);
        await _users.UpdateSecurityStampAsync(user);
        return NoContent();
    }
}
