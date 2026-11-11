public record DepositRequestDto(
    Guid AccountId,
    decimal Amount,
    string CurrencyCode,
    string IdempotencyKey,
    string Description
);

