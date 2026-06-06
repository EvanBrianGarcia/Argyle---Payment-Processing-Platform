using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentEventsAndOperationKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_idempotency_keys",
                table: "idempotency_keys");

            // Add the operation column with a backfill default so any pre-existing
            // Phase 1 rows pick up the create_payment namespace cleanly (per ADR-0007).
            migrationBuilder.AddColumn<string>(
                name: "operation",
                table: "idempotency_keys",
                type: "text",
                nullable: false,
                defaultValue: "create_payment");

            migrationBuilder.AddPrimaryKey(
                name: "pk_idempotency_keys",
                table: "idempotency_keys",
                columns: new[] { "merchant_id", "operation", "key" });

            // The default was only for the one-time backfill. New rows must
            // specify their operation explicitly via the application layer.
            migrationBuilder.Sql(
                "ALTER TABLE idempotency_keys ALTER COLUMN operation DROP DEFAULT;");

            migrationBuilder.CreateTable(
                name: "payment_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    payment_id = table.Column<string>(type: "text", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_events_payments_payment_id",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_events_payment_id_at",
                table: "payment_events",
                columns: new[] { "payment_id", "at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_events");

            migrationBuilder.DropPrimaryKey(
                name: "pk_idempotency_keys",
                table: "idempotency_keys");

            // The Up migration drops the default after backfill; restore an empty
            // string default temporarily so DropColumn doesn't inherit Up's
            // explicit-default-required posture. This is symmetric with Up.
            migrationBuilder.Sql(
                "ALTER TABLE idempotency_keys ALTER COLUMN operation SET DEFAULT '';");

            migrationBuilder.DropColumn(
                name: "operation",
                table: "idempotency_keys");

            migrationBuilder.AddPrimaryKey(
                name: "pk_idempotency_keys",
                table: "idempotency_keys",
                columns: new[] { "merchant_id", "key" });
        }
    }
}
