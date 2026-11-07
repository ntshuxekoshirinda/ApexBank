using FluentValidation;

public class OpenAccountDtoValidator : AbstractValidator<OpenAccountDto> {
    public OpenAccountDtoValidator() {
        RuleFor(a => a.UserId)
            .NotEmpty().WithMessage("User ID cannot be empty.");

        RuleFor(a => a.CurrencyCode)
            .NotEmpty().WithMessage("Currency code is required.")
            .Length(3).WithMessage("Currency code must be exactly 3 characters (e.g., ZAR, USD).")
            .Must(code => code.Equals("ZAR", StringComparison.OrdinalIgnoreCase) || 
                          code.Equals("USD", StringComparison.OrdinalIgnoreCase) || 
                          code.Equals("EUR", StringComparison.OrdinalIgnoreCase) || 
                          code.Equals("GBP", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Unsupported currency code. Supported codes are ZAR, USD, EUR, GBP.");
    }
}