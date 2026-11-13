using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mulse.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ConfirmedDeliveryReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RetainForConfirmation",
                table: "Messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetainForConfirmation",
                table: "Messages");
        }
    }
}
