using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PaymentPlatform.Domain.Merchants;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Infrastructure.Persistence.Configurations;

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> MetadataConverter =
        new(
            v => JsonSerializer.Serialize(v, MetadataJsonOptions),
            v => DeserializeMetadata(v));

    private static readonly ValueComparer<IReadOnlyDictionary<string, string>> MetadataComparer =
        new(
            (a, b) => DictionariesEqual(a, b),
            v => v == null ? 0 : v.Count,
            v => SnapshotMetadata(v));

    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", t =>
        {
            t.HasCheckConstraint("ck_payments_amount_minor_positive", "amount_minor > 0");
            t.HasCheckConstraint(
                "ck_payments_status_allowed",
                "status IN ('Pending','Authorized','Captured','Settled','Failed','Refunded')");
        });

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("id")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(p => p.MerchantId)
            .HasColumnName("merchant_id")
            .HasColumnType("text")
            .IsRequired();

        builder.ComplexProperty(p => p.Amount, money =>
        {
            money.Property(m => m.AmountMinor)
                .HasColumnName("amount_minor")
                .HasColumnType("bigint")
                .IsRequired();

            money.Property(m => m.Currency)
                .HasColumnName("currency")
                .HasColumnType("char(3)")
                .IsRequired();
        });

        builder.Property(p => p.Status)
            .HasColumnName("status")
            .HasColumnType("text")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(p => p.CardToken)
            .HasColumnName("card_token")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(p => p.CustomerReference)
            .HasColumnName("customer_reference")
            .HasColumnType("text");

        builder.Property(p => p.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .HasConversion(MetadataConverter, MetadataComparer)
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        builder.Property(p => p.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()")
            .IsRequired();

        builder.Property(p => p.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()")
            .IsRequired();

        builder.Property<int>("Version")
            .HasColumnName("version")
            .HasColumnType("integer")
            .HasDefaultValue(0)
            .IsConcurrencyToken()
            .IsRequired();

        builder.HasIndex(p => new { p.MerchantId, p.CreatedAt, p.Id })
            .HasDatabaseName("ix_payments_merchant_id_created_at_id")
            .IsDescending(false, true, true);

        builder.HasOne<Merchant>()
            .WithMany()
            .HasForeignKey(p => p.MerchantId)
            .HasConstraintName("fk_payments_merchants_merchant_id")
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static IReadOnlyDictionary<string, string> DeserializeMetadata(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, string>(0);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, MetadataJsonOptions)
            ?? new Dictionary<string, string>(0);
    }

    private static IReadOnlyDictionary<string, string> SnapshotMetadata(IReadOnlyDictionary<string, string> source)
    {
        var copy = new Dictionary<string, string>(source.Count);
        foreach (var kvp in source)
        {
            copy[kvp.Key] = kvp.Value;
        }
        return copy;
    }

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string>? a,
        IReadOnlyDictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var other) || !string.Equals(kvp.Value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
