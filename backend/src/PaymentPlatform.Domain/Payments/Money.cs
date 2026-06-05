using PaymentPlatform.Domain.Common;

namespace PaymentPlatform.Domain.Payments;

public readonly struct Money : IEquatable<Money>
{
    public long AmountMinor { get; }
    public string Currency { get; }

    public Money(long amountMinor, string currency)
    {
        if (amountMinor <= 0)
        {
            throw new DomainException(
                "invalid_amount",
                $"Amount must be positive (got {amountMinor}).");
        }

        if (!IsValidCurrency(currency))
        {
            throw new DomainException(
                "invalid_currency",
                "Currency must be exactly 3 uppercase ASCII letters.");
        }

        AmountMinor = amountMinor;
        Currency = currency;
    }

    private static bool IsValidCurrency(string? currency)
    {
        if (currency is null || currency.Length != 3)
        {
            return false;
        }

        for (var i = 0; i < currency.Length; i++)
        {
            var c = currency[i];
            if (c is < 'A' or > 'Z')
            {
                return false;
            }
        }

        return true;
    }

    public bool Equals(Money other) =>
        AmountMinor == other.AmountMinor &&
        string.Equals(Currency, other.Currency, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(AmountMinor, Currency);

    public static bool operator ==(Money left, Money right) => left.Equals(right);

    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    public override string ToString() => $"{AmountMinor} {Currency}";
}
