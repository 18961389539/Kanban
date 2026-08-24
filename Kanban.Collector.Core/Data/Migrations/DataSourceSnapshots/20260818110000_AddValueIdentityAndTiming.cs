using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kanban.Collector.Core.Data.Migrations.DataSourceSnapshots;

public partial class AddValueIdentityAndTiming : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ValueId",
            table: "DataSourceSnapshots",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<DateTime>(
            name: "PersistedAt",
            table: "DataSourceSnapshots",
            type: "TEXT",
            nullable: false,
            defaultValue: new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));

        migrationBuilder.Sql(
            "UPDATE \"DataSourceSnapshots\" SET \"PersistedAt\" = \"Timestamp\" WHERE \"PersistedAt\" = '0001-01-01 00:00:00'");

        migrationBuilder.CreateIndex(
            name: "IX_DataSourceSnapshots_DeviceId_SourceId_ValueId_Timestamp",
            table: "DataSourceSnapshots",
            columns: new[] { "DeviceId", "SourceId", "ValueId", "Timestamp" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_DataSourceSnapshots_DeviceId_SourceId_ValueId_Timestamp",
            table: "DataSourceSnapshots");

        migrationBuilder.DropColumn(name: "ValueId", table: "DataSourceSnapshots");
        migrationBuilder.DropColumn(name: "PersistedAt", table: "DataSourceSnapshots");
    }
}