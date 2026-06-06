using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentPlatform.Domain.Idempotency;

namespace PaymentPlatform.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKeyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyKeyRecord> builder)
    {
        builder.ToTable("idempotency_keys");

        builder.HasKey(i => new { i.MerchantId, i.Operation, i.Key })
            .HasName("pk_idempotency_keys");

        builder.Property(i => i.MerchantId)
            .HasColumnName("merchant_id")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(i => i.Operation)
            .HasColumnName("operation")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(i => i.Key)
            .HasColumnName("key")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(i => i.RequestHash)
            .HasColumnName("request_hash")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(i => i.ResponseStatus)
            .HasColumnName("response_status")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(i => i.ResponseBody)
            .HasColumnName("response_body")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(i => i.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()")
            .IsRequired();

        builder.HasIndex(i => i.CreatedAt)
            .HasDatabaseName("ix_idempotency_keys_created_at");
    }
}
