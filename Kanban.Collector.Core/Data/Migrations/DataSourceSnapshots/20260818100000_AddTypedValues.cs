using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kanban.Collector.Core.Data.Migrations.DataSourceSnapshots;

public partial class AddTypedValues : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("DataType", "DataSourceSnapshots", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<float>("FloatValue", "DataSourceSnapshots", nullable: true);
        migrationBuilder.AddColumn<bool>("BoolValue", "DataSourceSnapshots", nullable: true);
        migrationBuilder.AddColumn<string>("StringValue", "DataSourceSnapshots", maxLength: 1024, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("DataType", "DataSourceSnapshots");
        migrationBuilder.DropColumn("FloatValue", "DataSourceSnapshots");
        migrationBuilder.DropColumn("BoolValue", "DataSourceSnapshots");
        migrationBuilder.DropColumn("StringValue", "DataSourceSnapshots");
    }
}
