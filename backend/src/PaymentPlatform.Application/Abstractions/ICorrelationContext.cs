namespace PaymentPlatform.Application.Abstractions;

public interface ICorrelationContext
{
    /// The current request's correlation/trace id. Falls back to a fresh
    /// ULID when no ambient request context is present (e.g. background
    /// workers without an envelope).
    string CorrelationId { get; }
}
