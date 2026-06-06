using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxTraceparent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "traceparent",
                table: "payment_outbox",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "traceparent",
                table: "payment_outbox");
        }
    }
}
