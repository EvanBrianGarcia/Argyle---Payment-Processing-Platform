namespace PaymentPlatform.Contracts.Common;

public sealed record ErrorEnvelope(ErrorBody Error);

public sealed record ErrorBody(
    string Code,
    string Message,
    object? Details,
    string? TraceId,
    string? RequestId);
