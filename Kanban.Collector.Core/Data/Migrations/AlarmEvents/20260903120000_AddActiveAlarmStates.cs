using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kanban.Collector.Core.Data.Migrations.AlarmEvents
{
    /// <inheritdoc />
    /// <summary>新增报警活跃状态快照表：显式 IsActive 状态，替代从事件流推断活跃报警的设计。</summary>
    public partial class AddActiveAlarmStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActiveAlarmStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AlarmId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AlarmName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PlcAddress = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ShiftName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveAlarmStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActiveAlarmStates_DeviceId_AlarmId",
                table: "ActiveAlarmStates",
                columns: new[] { "DeviceId", "AlarmId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActiveAlarmStates_IsActive",
                table: "ActiveAlarmStates",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActiveAlarmStates");
        }
    }
}