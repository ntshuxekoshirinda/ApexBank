using System.ComponentModel.DataAnnotations;

public class Transaction {
    public Guid Id { get; set; } = Guid.NewGuid();

    // Nullable because a pure deposit has no source account, and a pure withdrawal has no destination account
    public Guid? SourceAccountId { get; set; }
    public Guid? DestinationAccountId { get; set; }

    [Range(0.0001, double.MaxValue)]
    public decimal Amount { get; set; }

    public string CurrencyCode { get; set; } = "ZAR";

    public TransactionType Type { get; set; }
    public TransactionStatus Status { get; set; } = TransactionStatus.Pending;

    // CRITICAL FINTECH RULE: Unique key sent by client to prevent double-charging on network retries
    public string IdempotencyKey { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation Properties
    public Account? SourceAccount { get; set; }
    public Account? DestinationAccount { get; set; }
}

public enum TransactionType {
    Deposit,
    Withdrawal,
    Transfer
}

public enum TransactionStatus {
    Pending,
    Completed,
    Failed,
    Reversed
}