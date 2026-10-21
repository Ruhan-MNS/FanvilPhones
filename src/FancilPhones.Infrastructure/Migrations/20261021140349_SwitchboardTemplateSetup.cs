using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FancilPhones.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SwitchboardTemplateSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                schema: "FancilPhones",
                table: "SwitchboardDeployments",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Layout");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Purpose",
                schema: "FancilPhones",
                table: "SwitchboardDeployments");
        }
    }
}
