using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MainAPP.Data.Migrations.AlarmEvents
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlarmEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AlarmId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AlarmName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PlcAddress = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    EventTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ShiftName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_AlarmId",
                table: "AlarmEvents",
                column: "AlarmId");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_DeviceId_AlarmId",
                table: "AlarmEvents",
                columns: new[] { "DeviceId", "AlarmId" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_EventTime",
                table: "AlarmEvents",
                column: "EventTime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmEvents");
        }
    }
}
