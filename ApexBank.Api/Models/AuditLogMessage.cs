public record AuditLogMessage(
    Guid TransactionId,
    Guid? SourceAccountId,
    Guid? DestinationAccountId,
    decimal Amount,
    string CurrencyCode,
    string Description,
    DateTime Timestamp
);