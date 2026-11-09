public record TransferRequestDto(
    Guid SourceAccountId,
    Guid DestinationAccountId,
    decimal Amount,
    string CurrencyCode,
    string IdempotencyKey,
    string Description
);