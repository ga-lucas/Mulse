using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mulse.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class DurableExecutionJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Executions",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ExecutionId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FlowId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FlowKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    StartedUtcTicks = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtcTicks = table.Column<long>(type: "bigint", nullable: false),
                    Definition = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Executions", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FlowId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExecutionKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    ExecutionId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    State = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    CreatedUtcTicks = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtcTicks = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Deliveries",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    UpdatedUtcTicks = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Key);
                    table.ForeignKey(
                        name: "FK_Deliveries_Executions_ExecutionKey",
                        column: x => x.ExecutionKey,
                        principalTable: "Executions",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    CreatedUtcTicks = table.Column<long>(type: "bigint", nullable: false),
                    Detail = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionEvents_Executions_ExecutionKey",
                        column: x => x.ExecutionKey,
                        principalTable: "Executions",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_ExecutionKey",
                table: "Deliveries",
                column: "ExecutionKey");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionEvents_ExecutionKey_CreatedUtcTicks",
                table: "ExecutionEvents",
                columns: new[] { "ExecutionKey", "CreatedUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_FlowKey_StartedUtcTicks",
                table: "Executions",
                columns: new[] { "FlowKey", "StartedUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_StartedUtcTicks",
                table: "Executions",
                column: "StartedUtcTicks");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ChannelKey_State_CreatedUtcTicks",
                table: "Messages",
                columns: new[] { "ChannelKey", "State", "CreatedUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ExecutionKey",
                table: "Messages",
                column: "ExecutionKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Deliveries");

            migrationBuilder.DropTable(
                name: "ExecutionEvents");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Executions");
        }
    }
}
