using FluentValidation;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Register PostgreSQL Database (using connection string or user secrets)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// 2. Register Validation and Swagger
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// 3. Configure Middleware Pipeline
if (app.Environment.IsDevelopment()) {
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// --- PHASE 1 ENDPOINTS ---

// Register a new bank customer
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

// Open a new currency account
app.MapPost("/api/accounts", async (OpenAccountDto dto, IValidator<OpenAccountDto> validator, AppDbContext db) => {
    var validationResult = await validator.ValidateAsync(dto);
    if (!validationResult.IsValid) {
        return Results.ValidationProblem(validationResult.ToDictionary());
    }

    var userExists = await db.Users.AnyAsync(u => u.Id == dto.UserId);
    if (!userExists) {
        return Results.NotFound(new { Message = "User not found." });
    }

    var account = new Account {
        UserId = dto.UserId,
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
});

// Check account balances for a specific user
app.MapGet("/api/users/{userId}/accounts", async (Guid userId, AppDbContext db) => {
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
});

app.Run();

// --- DTO Records ---
public record RegisterUserDto(string Username, string Email, string Password);
public record OpenAccountDto(Guid UserId, string CurrencyCode);

public partial class Program { }