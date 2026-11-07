using System.ComponentModel.DataAnnotations;

public class Account {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string AccountNumber { get; set; } = GenerateAccountNumber();
    public string CurrencyCode { get; set; } = "ZAR"; // e.g., ZAR, USD, EUR
    public decimal Balance { get; set; } = 0.0000m;
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Concurrency Token for optimistic locking in later transaction phases
    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    // Navigation Property
    public User User { get; set; } = null!;

    private static string GenerateAccountNumber() {
        return Random.Shared.Next(100000000, 999999999).ToString();
    }
}

public enum AccountStatus {
    Active,
    Frozen,
    Closed
}