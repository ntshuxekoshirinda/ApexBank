using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Register PostgreSQL Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment()) {
    app.UseSwagger();
    app.UseSwaggerUI();
}

// --- PHASE 1 ENDPOINTS ---

// 1. Register a new bank customer
app.MapPost("/api/users", async (RegisterUserDto dto, AppDbContext db) => {
    if (await db.Users.AnyAsync(u => u.Email == dto.Email)) {
        return Results.BadRequest(new { Message = "User with this email already exists." });
    }

    var user = new User {
        Username = dto.Username,
        Email = dto.Email,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password) // Secure hashing placeholder
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Username, user.Email });
});

// 2. Open a new currency account for an existing user
app.MapPost("/api/accounts", async (OpenAccountDto dto, AppDbContext db) => {
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

// 3. Check account balances for a specific user
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