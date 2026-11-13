using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mulse.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class StorageMigrationSeals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StorageMigrationId",
                table: "Workspaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageMigrationState",
                table: "Workspaces",
                type: "character varying(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageMigrationTarget",
                table: "Workspaces",
                type: "character varying(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageMigrationId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "StorageMigrationState",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "StorageMigrationTarget",
                table: "Workspaces");
        }
    }
}
