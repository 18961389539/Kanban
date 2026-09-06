using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kanban.Collector.Core.Data.Migrations.StatusTransitions
{
    /// <inheritdoc />
    /// <summary>状态转换增加离线原因：区分 PLC 报 0、通讯中断、采集停止、空窗补写。</summary>
    public partial class AddOfflineCause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OfflineCause",
                table: "StatusTransitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfflineCause",
                table: "StatusTransitions");
        }
    }
}
