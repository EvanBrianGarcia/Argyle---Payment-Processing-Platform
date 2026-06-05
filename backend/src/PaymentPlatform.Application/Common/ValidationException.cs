namespace PaymentPlatform.Application.Common;

public sealed record ValidationFailure(string Field, string Message);

public sealed class ValidationException : Exception
{
    public IReadOnlyList<ValidationFailure> Failures { get; }

    public ValidationException(IReadOnlyList<ValidationFailure> failures)
        : base("One or more validation errors occurred.")
    {
        Failures = failures;
    }
}
