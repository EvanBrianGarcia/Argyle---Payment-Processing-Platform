using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.Application.Abstractions;

public interface IPaymentProcessor
{
    Task<ProcessorResult> SettleAsync(SettlePayment message, CancellationToken cancellationToken);
}

public abstract record ProcessorResult
{
    private ProcessorResult() { }

    public sealed record Success(string ExternalReference) : ProcessorResult;

    public sealed record TransientFailure(string Reason) : ProcessorResult;

    public sealed record PermanentFailure(string Reason) : ProcessorResult;
}
