using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class TransferConcurrencyIntegrationTests : IClassFixture<CustomWebApplicationFactory> {
    private readonly CustomWebApplicationFactory _factory;

    public TransferConcurrencyIntegrationTests(CustomWebApplicationFactory factory) {
        _factory = factory;
    }

    [Fact]
    public async Task ConcurrentTransfers_FromSameAccount_ShouldPreventDoubleSpendingAndHandleConcurrency() {
        // --- ARRANGE ---
        Guid sourceAccountId;
        Guid destId1;
        Guid destId2;
        string jwtToken;

        // 1. Seed test data into the Testcontainers PostgreSQL database
        using (var scope = _factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

            var user = new User { 
                Username = "ConcurrencyUser", 
                Email = "concurrency@bank.com", 
                PasswordHash = "hashed_pw" 
            };
            db.Users.Add(user);

            // Source account starts with 1000 ZAR
            var source = new Account { UserId = user.Id, CurrencyCode = "ZAR", Balance = 1000.0000m };
            var dest1 = new Account { UserId = user.Id, CurrencyCode = "ZAR", Balance = 0.0000m };
            var dest2 = new Account { UserId = user.Id, CurrencyCode = "ZAR", Balance = 0.0000m };

            db.Accounts.AddRange(source, dest1, dest2);
            await db.SaveChangesAsync();

            sourceAccountId = source.Id;
            destId1 = dest1.Id;
            destId2 = dest2.Id;
            jwtToken = tokenService.GenerateToken(user);
        }

        // 2. Create two independent test clients to simulate two separate users/threads hitting the API
        var client1 = _factory.CreateClient();
        client1.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);

        var client2 = _factory.CreateClient();
        client2.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);

        // Both requests try to transfer 600 ZAR (Total = 1200 ZAR, but balance is only 1000 ZAR).
        // Both read balance = 1000, passing initial validation simultaneously.
        var transferDto1 = new TransferRequestDto(sourceAccountId, destId1, 600.0000m, "ZAR", "idempotency-key-A", "Concurrent Transfer 1");
        var transferDto2 = new TransferRequestDto(sourceAccountId, destId2, 600.0000m, "ZAR", "idempotency-key-B", "Concurrent Transfer 2");

        // --- ACT ---
        // Fire both HTTP requests simultaneously using Task.WhenAll
        var task1 = client1.PostAsJsonAsync("/api/transfers", transferDto1);
        var task2 = client2.PostAsJsonAsync("/api/transfers", transferDto2);

        await Task.WhenAll(task1, task2);

        var response1 = await task1;
        var response2 = await task2;

        var statusCode1 = response1.StatusCode;
        var statusCode2 = response2.StatusCode;

        // --- ASSERT ---
        // Fintech Safety Rule: Exactly one transaction must succeed (200 OK), 
        // and the other must be rejected due to either a concurrency conflict (409) or insufficient funds.
        var oneSucceeded = (statusCode1 == HttpStatusCode.OK && (statusCode2 == HttpStatusCode.Conflict || statusCode2 == HttpStatusCode.BadRequest));
        var theOtherSucceeded = (statusCode2 == HttpStatusCode.OK && (statusCode1 == HttpStatusCode.Conflict || statusCode1 == HttpStatusCode.BadRequest));

        Assert.True(
            oneSucceeded || theOtherSucceeded,
            $"Expected race condition protection: One transfer should succeed and one should fail. Got StatusCode1: {statusCode1}, StatusCode2: {statusCode2}"
        );

        // 3. Verify final database state: Source account must not have gone into a negative balance
        using (var verifyScope = _factory.Services.CreateScope()) {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var finalSourceAccount = await verifyDb.Accounts.FindAsync(sourceAccountId);

            Assert.NotNull(finalSourceAccount);
            // Balance should be either 400 (if transfer 1 won) or 400 (if transfer 2 won), but never negative or corrupted
            Assert.True(finalSourceAccount.Balance >= 0.0000m, "Account balance dropped below zero, indicating a race condition failure!");
        }
    }
}