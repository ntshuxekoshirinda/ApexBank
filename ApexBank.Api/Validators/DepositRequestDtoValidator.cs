using FluentValidation;

public class DepositRequestDtoValidator : AbstractValidator<DepositRequestDto> {
    public DepositRequestDtoValidator() {
        RuleFor(x => x.AccountId).NotEmpty().WithMessage("Account ID is required.");
        RuleFor(x => x.Amount).GreaterThan(0.0000m).WithMessage("Deposit amount must be greater than zero.");
        RuleFor(x => x.CurrencyCode).NotEmpty().Length(3).WithMessage("Valid 3-letter currency code is required.");
        RuleFor(x => x.IdempotencyKey).NotEmpty().WithMessage("An Idempotency-Key is required.");
    }
}

public class WithdrawalRequestDtoValidator : AbstractValidator<WithdrawalRequestDto> {
    public WithdrawalRequestDtoValidator() {
        RuleFor(x => x.AccountId).NotEmpty().WithMessage("Account ID is required.");
        RuleFor(x => x.Amount).GreaterThan(0.0000m).WithMessage("Withdrawal amount must be greater than zero.");
        RuleFor(x => x.CurrencyCode).NotEmpty().Length(3).WithMessage("Valid 3-letter currency code is required.");
        RuleFor(x => x.IdempotencyKey).NotEmpty().WithMessage("An Idempotency-Key is required.");
    }
}