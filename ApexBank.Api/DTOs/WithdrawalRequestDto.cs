public record WithdrawalRequestDto(
    Guid AccountId,
    decimal Amount,
    string CurrencyCode,
    string IdempotencyKey,
    string Description
);