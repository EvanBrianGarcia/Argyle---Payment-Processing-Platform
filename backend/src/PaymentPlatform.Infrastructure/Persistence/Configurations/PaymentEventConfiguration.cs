using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Infrastructure.Persistence.Configurations;

public sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> PayloadConverter =
        new(
            v => JsonSerializer.Serialize(v, PayloadJsonOptions),
            v => DeserializePayload(v));

    private static readonly ValueComparer<IReadOnlyDictionary<string, string>> PayloadComparer =
        new(
            (a, b) => DictionariesEqual(a, b),
            v => v == null ? 0 : v.Count,
            v => SnapshotPayload(v));

    public void Configure(EntityTypeBuilder<PaymentEvent> builder)
    {
        builder.ToTable("payment_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(e => e.PaymentId)
            .HasColumnName("payment_id")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(e => e.FromStatus)
            .HasColumnName("from_status")
            .HasColumnType("text")
            .HasConversion<string>();

        builder.Property(e => e.ToStatus)
            .HasColumnName("to_status")
            .HasColumnType("text")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(e => e.Actor)
            .HasColumnName("actor")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(e => e.Reason)
            .HasColumnName("reason")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(e => e.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .HasConversion(PayloadConverter, PayloadComparer)
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        builder.Property(e => e.At)
            .HasColumnName("at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()")
            .IsRequired();

        builder.HasIndex(e => new { e.PaymentId, e.At })
            .HasDatabaseName("ix_payment_events_payment_id_at")
            .IsDescending(false, true);

        builder.HasOne<Payment>()
            .WithMany()
            .HasForeignKey(e => e.PaymentId)
            .HasConstraintName("fk_payment_events_payments_payment_id")
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static IReadOnlyDictionary<string, string> DeserializePayload(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, string>(0);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, PayloadJsonOptions)
            ?? new Dictionary<string, string>(0);
    }

    private static IReadOnlyDictionary<string, string> SnapshotPayload(IReadOnlyDictionary<string, string> source)
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
            if (!b.TryGetValue(kvp.Key, out var other) ||
                !string.Equals(kvp.Value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
