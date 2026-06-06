using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentPlatform.Domain.Outbox;

namespace PaymentPlatform.Infrastructure.Persistence.Configurations;

public sealed class PaymentOutboxMessageConfiguration : IEntityTypeConfiguration<PaymentOutboxMessage>
{
    public void Configure(EntityTypeBuilder<PaymentOutboxMessage> builder)
    {
        builder.ToTable("payment_outbox");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd()
            .UseIdentityByDefaultColumn();

        builder.Property(o => o.AggregateId)
            .HasColumnName("aggregate_id")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(o => o.MessageType)
            .HasColumnName("message_type")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(o => o.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(o => o.CorrelationId)
            .HasColumnName("correlation_id")
            .HasColumnType("text")
            .IsRequired();

        // Phase 4 Task 5 — persisted W3C traceparent so the OutboxDispatcher
        // can restore the originating capture's trace context before publish
        // and the consume span lands in the same trace.
        builder.Property(o => o.Traceparent)
            .HasColumnName("traceparent")
            .HasColumnType("text");

        builder.Property(o => o.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()")
            .IsRequired();

        builder.Property(o => o.DispatchedAt)
            .HasColumnName("dispatched_at")
            .HasColumnType("timestamptz");

        builder.HasIndex(o => o.AggregateId)
            .HasDatabaseName("ix_payment_outbox_aggregate_id");

        // Partial index on undispatched rows — keeps dispatcher polls cheap as
        // history grows. EF's native HasIndex does not emit the WHERE clause,
        // so the migration supplements this with raw SQL.
        builder.HasIndex(o => o.CreatedAt)
            .HasDatabaseName("ix_payment_outbox_undispatched")
            .HasFilter("\"dispatched_at\" IS NULL");
    }
}
