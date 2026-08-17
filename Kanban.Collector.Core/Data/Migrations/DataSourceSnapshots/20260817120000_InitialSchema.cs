using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kanban.Collector.Core.Data.Migrations.DataSourceSnapshots
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataSourceSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: false),
                    IsValid = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShiftName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSourceSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceSnapshots_DeviceId",
                table: "DataSourceSnapshots",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceSnapshots_DeviceId_Timestamp",
                table: "DataSourceSnapshots",
                columns: new[] { "DeviceId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceSnapshots_SourceId_Timestamp",
                table: "DataSourceSnapshots",
                columns: new[] { "SourceId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceSnapshots_Timestamp",
                table: "DataSourceSnapshots",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataSourceSnapshots");
        }
    }
}