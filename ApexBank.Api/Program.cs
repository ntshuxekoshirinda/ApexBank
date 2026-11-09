using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Configuration (PostgreSQL / Aiven or Local)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// 2. Validation, API Explorer & Standard Swagger
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddScoped<TransferService>();

// 3. JWT Authentication & Authorization
var jwtSettings = builder.Configuration.GetSection("Jwt");
var secretKey = jwtSettings["SecretKey"] ?? throw new InvalidOperationException("JWT SecretKey is missing.");
var key = Encoding.UTF8.GetBytes(secretKey);

builder.Services.AddAuthentication(options => {
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options => {
    options.TokenValidationParameters = new TokenValidationParameters {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidIssuer = jwtSettings["Issuer"],
        ValidateAudience = true,
        ValidAudience = jwtSettings["Audience"],
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();
builder.Services.AddSingleton<TokenService>();

var app = builder.Build();

// 4. Middleware Pipeline
if (app.Environment.IsDevelopment()) {
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

// --- PHASE 1 ENDPOINTS ---

// 1. Register a new customer
app.MapPost("/api/users", async (RegisterUserDto dto, IValidator<RegisterUserDto> validator, AppDbContext db) => {
    var validationResult = await validator.ValidateAsync(dto);
    if (!validationResult.IsValid) {
        return Results.ValidationProblem(validationResult.ToDictionary());
    }

    if (await db.Users.AnyAsync(u => u.Email == dto.Email)) {
        return Results.BadRequest(new { Message = "User with this email already exists." });
    }

    var user = new User {
        Username = dto.Username,
        Email = dto.Email,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password)
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Username, user.Email });
});

// 2. Authenticate and issue JWT access token
app.MapPost("/api/auth/login", async (LoginDto dto, AppDbContext db, TokenService tokenService) => {
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);

    if (user == null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash)) {
        return Results.Unauthorized();
    }

    var token = tokenService.GenerateToken(user);
    return Results.Ok(new { AccessToken = token, TokenType = "Bearer" });
});

// 3. Open a new currency account (Secured via JWT & Bound to Authenticated User)
app.MapPost("/api/accounts", async (OpenAccountDto dto, IValidator<OpenAccountDto> validator, AppDbContext db, HttpContext httpContext) => {
    var validationResult = await validator.ValidateAsync(dto);
    if (!validationResult.IsValid) {
        return Results.ValidationProblem(validationResult.ToDictionary());
    }

    var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? httpContext.User.FindFirst("sub")?.Value;

    if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var authenticatedUserId)) {
        return Results.Unauthorized();
    }

    var userExists = await db.Users.AnyAsync(u => u.Id == authenticatedUserId);
    if (!userExists) {
        return Results.NotFound(new { Message = "User not found." });
    }

    var account = new Account {
        UserId = authenticatedUserId,
        CurrencyCode = dto.CurrencyCode.ToUpper(),
        Balance = 0.0000m
    };

    db.Accounts.Add(account);
    await db.SaveChangesAsync();

    return Results.Created($"/api/accounts/{account.Id}", new {
        account.Id,
        account.AccountNumber,
        account.CurrencyCode,
        account.Balance,
        account.CreatedAt
    });
}).RequireAuthorization();

// 4. Check account balances for the logged-in user
app.MapGet("/api/accounts", async (AppDbContext db, HttpContext httpContext) => {
    var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? httpContext.User.FindFirst("sub")?.Value;

    if (!Guid.TryParse(userIdClaim, out var userId)) {
        return Results.Unauthorized();
    }

    var accounts = await db.Accounts
        .Where(a => a.UserId == userId)
        .Select(a => new {
            a.Id,
            a.AccountNumber,
            a.CurrencyCode,
            a.Balance,
            a.Status
        })
        .ToListAsync();

    return Results.Ok(accounts);
}).RequireAuthorization();

// 5. POST: Execute secure fund transfer between accounts
app.MapPost("/api/transfers", async (
    TransferRequestDto dto, 
    IValidator<TransferRequestDto> validator, 
    TransferService transferService, 
    HttpContext httpContext) => {

    var validationResult = await validator.ValidateAsync(dto);
    if (!validationResult.IsValid) {
        return Results.ValidationProblem(validationResult.ToDictionary());
    }

    // Extract authenticated user ID from JWT claims
    var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? httpContext.User.FindFirst("sub")?.Value;

    if (!Guid.TryParse(userIdClaim, out var authenticatedUserId)) {
        return Results.Unauthorized();
    }

    var result = await transferService.ExecuteTransferAsync(dto, authenticatedUserId);

    if (result.Status == "Completed") {
        return Results.Ok(result);
    } else if (result.Status == "Unauthorized") {
        return Results.Json(result, statusCode:StatusCodes.Status403Forbidden);
    } else if (result.Status == "Conflict") {
        return Results.Conflict(result);
    }

    return Results.BadRequest(result);
}).RequireAuthorization();

app.Run();

// --- DTO RECORDS & PARTIAL PROGRAM ---
public record RegisterUserDto(string Username, string Email, string Password);
public record LoginDto(string Email, string Password);
public record OpenAccountDto(string CurrencyCode);

public partial class Program { }