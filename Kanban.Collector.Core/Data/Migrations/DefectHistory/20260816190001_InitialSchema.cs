using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kanban.Collector.Core.Data.Migrations.DefectHistory
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DefectSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DefectId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DefectName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    ShiftName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DefectSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DefectSnapshots_DeviceId_DefectId_Timestamp",
                table: "DefectSnapshots",
                columns: new[] { "DeviceId", "DefectId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_DefectSnapshots_DeviceId_Timestamp",
                table: "DefectSnapshots",
                columns: new[] { "DeviceId", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DefectSnapshots");
        }
    }
}
