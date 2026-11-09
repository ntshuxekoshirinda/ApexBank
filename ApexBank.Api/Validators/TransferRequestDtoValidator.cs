using FluentValidation;

public class TransferRequestDtoValidator : AbstractValidator<TransferRequestDto> {
    public TransferRequestDtoValidator() {
        RuleFor(x => x.SourceAccountId)
            .NotEmpty().WithMessage("Source account ID is required.");

        RuleFor(x => x.DestinationAccountId)
            .NotEmpty().WithMessage("Destination account ID is required.")
            .NotEqual(x => x.SourceAccountId).WithMessage("Source and destination accounts cannot be the same.");

        RuleFor(x => x.Amount)
            .GreaterThan(0.0000m).WithMessage("Transfer amount must be greater than zero.");

        RuleFor(x => x.CurrencyCode)
            .NotEmpty().Length(3).WithMessage("Valid 3-letter currency code is required.");

        RuleFor(x => x.IdempotencyKey)
            .NotEmpty().WithMessage("An Idempotency-Key header/field is required to prevent duplicate transfers.");
    }
}