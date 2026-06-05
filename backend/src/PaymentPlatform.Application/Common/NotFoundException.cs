namespace PaymentPlatform.Application.Common;

public sealed class NotFoundException : Exception
{
    public string Code { get; }

    public NotFoundException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}
