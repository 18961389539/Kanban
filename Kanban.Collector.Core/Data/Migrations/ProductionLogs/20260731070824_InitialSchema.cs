using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kanban.Collector.Core.Data.Migrations.ProductionLogs
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ShiftName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WorkOrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OkProduction = table.Column<int>(type: "INTEGER", nullable: false),
                    NgProduction = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusWord = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLogs_DeviceId",
                table: "ProductionLogs",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLogs_DeviceId_Timestamp",
                table: "ProductionLogs",
                columns: new[] { "DeviceId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLogs_Timestamp",
                table: "ProductionLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLogs_WorkOrderId",
                table: "ProductionLogs",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLogs_EventId",
                table: "ProductionLogs",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductionLogs");
        }
    }
}
