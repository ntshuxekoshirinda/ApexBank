using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
builder.Services.AddScoped<CashService>();

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

// --- PHASE 1 & 2 ENDPOINTS ---

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

// 5. Execute secure fund transfer between accounts (ACID, Concurrency Protected & Asynchronously Audited)
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

    if (result.Status == "Completed") {
        return Results.Ok(result);
    } else if (result.Status == "Unauthorized") {
        return Results.Json(result, statusCode: StatusCodes.Status403Forbidden);
    } else if (result.Status == "Conflict") {
        return Results.Conflict(result);
    }

    return Results.BadRequest(result);
}).RequireAuthorization();

// 6. Get paginated transaction history for a specific account (Secured & Ownership Verified)
app.MapGet("/api/accounts/{accountId}/transactions", async (
    Guid accountId, 
    int? page, 
    int? pageSize, 
    TransactionType? type, 
    AppDbContext db, 
    HttpContext httpContext) => {

    // 1. Extract authenticated user ID from JWT claims
    var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? httpContext.User.FindFirst("sub")?.Value;

    if (!Guid.TryParse(userIdClaim, out var authenticatedUserId)) {
        return Results.Unauthorized();
    }

    // 2. Verify account ownership
    var account = await db.Accounts
        .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == authenticatedUserId);

    if (account == null) {
        return Results.NotFound(new { Message = "Account not found or you do not have permission to view it." });
    }

    // 3. Build base query: Transactions where this account is either source or destination
    var query = db.Transactions
        .Where(t => t.SourceAccountId == accountId || t.DestinationAccountId == accountId);

    // Optional Filter by Type (Deposit, Withdrawal, Transfer)
    if (type.HasValue) {
        query = query.Where(t => t.Type == type.Value);
    }

    // 4. Pagination Setup (Default: Page 1, 20 items per page)
    int pageNumber = page ?? 1;
    int size = pageSize ?? 20;
    if (pageNumber < 1) pageNumber = 1;
    if (size < 1 || size > 100) size = 20; // Enforce max page size limit

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

// 7. Execute secure cash deposit
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
}).RequireAuthorization();

// 8. Execute secure cash withdrawal
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
}).RequireAuthorization();

app.Run();

// --- DTO RECORDS & PARTIAL PROGRAM ---
public record RegisterUserDto(string Username, string Email, string Password);
public record LoginDto(string Email, string Password);
public record OpenAccountDto(string CurrencyCode);

public partial class Program { }