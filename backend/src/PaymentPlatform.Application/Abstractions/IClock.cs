namespace PaymentPlatform.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
