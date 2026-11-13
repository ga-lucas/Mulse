using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mulse.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistentMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MaintenanceRequested",
                table: "Workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaintenanceRequested",
                table: "Workspaces");
        }
    }
}
