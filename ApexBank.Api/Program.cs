using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Configuration (PostgreSQL / Aiven)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// CRITICAL: Skip Npgsql registration during integration tests to prevent provider conflicts with SQLite
if (!builder.Environment.IsEnvironment("Testing")) {
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString));
}

// 2. Validation, API Explorer & Swagger
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 2.5. Built-in Rate Limiting Policies
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) => {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            "{\"error\": \"Too many requests. Please try again later.\"}", 
            cancellationToken);
    };

    // Policy 1: Strict rate limit for authentication/login endpoints (IP-based)
    options.AddPolicy("auth-limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions {
                PermitLimit = 5,
                Window = TimeSpan.FromSeconds(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Policy 2: Rate limit for financial operations (User ID or IP-based)
    options.AddPolicy("transaction-limit", httpContext => {
        var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                     ?? httpContext.Connection.RemoteIpAddress?.ToString() 
                     ?? "anonymous";

        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: userId,
            factory: _ => new SlidingWindowRateLimiterOptions {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 2,
                QueueLimit = 2
            });
    });
});

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

// 4. Register Domain Services & Background Processing Pipeline
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<CashService>();
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<QueuedHostedService>();

var app = builder.Build();

// 5. Middleware Pipeline
if (app.Environment.IsDevelopment()) {
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // CRITICAL: Must be placed after Auth and before endpoints

// --- BANKING ENGINE ENDPOINTS ---

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

// 2. Authenticate and issue JWT access token (Protected by auth rate limiter)
app.MapPost("/api/auth/login", async (LoginDto dto, AppDbContext db, TokenService tokenService) => {
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);

    if (user == null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash)) {
        return Results.Unauthorized();
    }

    var token = tokenService.GenerateToken(user);
    return Results.Ok(new { AccessToken = token, TokenType = "Bearer" });
}).RequireRateLimiting("auth-limit");

// 3. Open a new currency account (Secured via JWT)
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

// 5. Get paginated transaction history for a specific account (Secured & Ownership Verified)
app.MapGet("/api/accounts/{accountId}/transactions", async (
    Guid accountId, 
    int? page, 
    int? pageSize, 
    TransactionType? type, 
    AppDbContext db, 
    HttpContext httpContext) => {

    var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? httpContext.User.FindFirst("sub")?.Value;

    if (!Guid.TryParse(userIdClaim, out var authenticatedUserId)) {
        return Results.Unauthorized();
    }

    var account = await db.Accounts
        .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == authenticatedUserId);

    if (account == null) {
        return Results.NotFound(new { Message = "Account not found or you do not have permission to view it." });
    }

    var query = db.Transactions
        .Where(t => t.SourceAccountId == accountId || t.DestinationAccountId == accountId);

    if (type.HasValue) {
        query = query.Where(t => t.Type == type.Value);
    }

    int pageNumber = page ?? 1;
    int size = pageSize ?? 20;
    if (pageNumber < 1) pageNumber = 1;
    if (size < 1 || size > 100) size = 20;

    var totalCount = await query.CountAsync();

    var transactions = await query
        .OrderByDescending(t => t.CreatedAt)
        .Skip((pageNumber - 1) * size)
        .Take(size)
        .Select(t => new {
            t.Id,
            t.SourceAccountId,
            t.DestinationAccountId,
            t.Amount,
            t.CurrencyCode,
            Type = t.Type.ToString(),
            Status = t.Status.ToString(),
            t.Description,
            t.CreatedAt,
            IsDebit = t.SourceAccountId == accountId
        })
        .ToListAsync();

    return Results.Ok(new {
        AccountId = accountId,
        AccountNumber = account.AccountNumber,
        CurrentBalance = account.Balance,
        Page = pageNumber,
        PageSize = size,
        TotalTransactions = totalCount,
        TotalPages = (int)Math.Ceiling(totalCount / (double)size),
        Transactions = transactions
    });
}).RequireAuthorization();

// 6. Execute secure fund transfer between accounts (ACID, Concurrency Protected, Asynchronously Audited & Rate-limited)
app.MapPost("/api/transfers", async (
    TransferRequestDto dto, 
    IValidator<TransferRequestDto> validator, 
    TransferService transferService, 
    HttpContext httpContext) => {

    var validationResult = await validator.ValidateAsync(dto);
    if (!validationResult.IsValid) {
        return Results.ValidationProblem(validationResult.ToDictionary());
    }

    var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? httpContext.User.FindFirst("sub")?.Value;

    if (!Guid.TryParse(userIdClaim, out var authenticatedUserId)) {
        return Results.Unauthorized();
    }

    var result = await transferService.ExecuteTransferAsync(dto, authenticatedUserId);

    if (result.Status == "Completed") return Results.Ok(result);
    if (result.Status == "Unauthorized") return Results.Json(result, statusCode: StatusCodes.Status403Forbidden);
    if (result.Status == "Conflict") return Results.Conflict(result);

    return Results.BadRequest(result);
}).RequireAuthorization()
  .RequireRateLimiting("transaction-limit");

// 7. Execute secure cash deposit (Rate-limited)
app.MapPost("/api/deposits", async (
    DepositRequestDto dto,
    IValidator<DepositRequestDto> validator,
    CashService cashService,
    HttpContext httpContext) => {

    var validationResult = await validator.ValidateAsync(dto);
    if (!validationResult.IsValid) {
        return Results.ValidationProblem(validationResult.ToDictionary());
    }

    var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? httpContext.User.FindFirst("sub")?.Value;

    if (!Guid.TryParse(userIdClaim, out var authenticatedUserId)) {
        return Results.Unauthorized();
    }

    var result = await cashService.ExecuteDepositAsync(dto, authenticatedUserId);

    if (result.Status == "Completed") return Results.Ok(result);
    if (result.Status == "Unauthorized") return Results.Json(result, statusCode: StatusCodes.Status403Forbidden);
    if (result.Status == "Conflict") return Results.Conflict(result);

    return Results.BadRequest(result);
}).RequireAuthorization()
  .RequireRateLimiting("transaction-limit");

// 8. Execute secure cash withdrawal (Rate-limited)
app.MapPost("/api/withdrawals", async (
    WithdrawalRequestDto dto,
    IValidator<WithdrawalRequestDto> validator,
    CashService cashService,
    HttpContext httpContext) => {

    var validationResult = await validator.ValidateAsync(dto);
    if (!validationResult.IsValid) {
        return Results.ValidationProblem(validationResult.ToDictionary());
    }

    var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? httpContext.User.FindFirst("sub")?.Value;

    if (!Guid.TryParse(userIdClaim, out var authenticatedUserId)) {
        return Results.Unauthorized();
    }

    var result = await cashService.ExecuteWithdrawalAsync(dto, authenticatedUserId);

    if (result.Status == "Completed") return Results.Ok(result);
    if (result.Status == "Unauthorized") return Results.Json(result, statusCode: StatusCodes.Status403Forbidden);
    if (result.Status == "Conflict") return Results.Conflict(result);

    return Results.BadRequest(result);
}).RequireAuthorization()
  .RequireRateLimiting("transaction-limit");

app.Run();

// --- DTO RECORDS & PARTIAL PROGRAM ---
public record RegisterUserDto(string Username, string Email, string Password);
public record LoginDto(string Email, string Password);
public record OpenAccountDto(string CurrencyCode);

public partial class Program { }