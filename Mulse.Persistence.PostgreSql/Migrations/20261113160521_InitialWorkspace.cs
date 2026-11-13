using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mulse.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class InitialWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Checkpoints",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    FlowKey = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checkpoints", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationValues",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationValues", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "FlowRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowKey = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtcTicks = table.Column<long>(type: "bigint", nullable: false),
                    Definition = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowRevisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModulePackages",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModulePackages", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Retries",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    FlowKey = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    ExecutionKey = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    NextAttemptUtcTicks = table.Column<long>(type: "bigint", nullable: false),
                    NotificationsExhausted = table.Column<bool>(type: "boolean", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Retries", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    Encrypted = table.Column<bool>(type: "boolean", nullable: false),
                    KeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PluginDirectories = table.Column<byte[]>(type: "bytea", nullable: false),
                    ImportSource = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Flows",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    OriginalId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Flows", x => x.Key);
                    table.ForeignKey(
                        name: "FK_Flows_FlowRevisions_CurrentRevisionId",
                        column: x => x.CurrentRevisionId,
                        principalTable: "FlowRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Checkpoints_FlowKey",
                table: "Checkpoints",
                column: "FlowKey");

            migrationBuilder.CreateIndex(
                name: "IX_FlowRevisions_FlowKey_Number",
                table: "FlowRevisions",
                columns: new[] { "FlowKey", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Flows_CurrentRevisionId",
                table: "Flows",
                column: "CurrentRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_Retries_FlowKey_ExecutionKey",
                table: "Retries",
                columns: new[] { "FlowKey", "ExecutionKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Retries_NotificationsExhausted_NextAttemptUtcTicks",
                table: "Retries",
                columns: new[] { "NotificationsExhausted", "NextAttemptUtcTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Checkpoints");

            migrationBuilder.DropTable(
                name: "ConfigurationValues");

            migrationBuilder.DropTable(
                name: "Flows");

            migrationBuilder.DropTable(
                name: "ModulePackages");

            migrationBuilder.DropTable(
                name: "Retries");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropTable(
                name: "FlowRevisions");
        }
    }
}
