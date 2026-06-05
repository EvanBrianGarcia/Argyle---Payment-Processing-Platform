using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentPlatform.Domain.Merchants;

namespace PaymentPlatform.Infrastructure.Persistence.Configurations;

public sealed class MerchantConfiguration : IEntityTypeConfiguration<Merchant>
{
    private static readonly DateTimeOffset SeedCreatedAt =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Configure(EntityTypeBuilder<Merchant> builder)
    {
        builder.ToTable("merchants");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .HasColumnName("id")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(m => m.Name)
            .HasColumnName("name")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(m => m.ApiKeyHash)
            .HasColumnName("api_key_hash")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(m => m.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()")
            .IsRequired();

        builder.HasIndex(m => m.ApiKeyHash)
            .HasDatabaseName("ux_merchants_api_key_hash")
            .IsUnique();

        builder.HasData(
            new Merchant(
                id: "mrc_acme",
                name: "Acme Corp",
                apiKeyHash: HashApiKey("dev-key-mrc-acme"),
                createdAt: SeedCreatedAt),
            new Merchant(
                id: "mrc_pied",
                name: "Pied Piper",
                apiKeyHash: HashApiKey("dev-key-mrc-pied"),
                createdAt: SeedCreatedAt));
    }

    private static string HashApiKey(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
