using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FancilPhones.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SwitchboardDraftEditor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "SortAlphabetically", schema: "FancilPhones", table: "SwitchboardProfiles", type: "bit", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<string>(
                name: "DraftJson",
                schema: "FancilPhones",
                table: "SwitchboardProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DraftRevision",
                schema: "FancilPhones",
                table: "SwitchboardProfiles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "SourcePhoneId",
                schema: "FancilPhones",
                table: "SwitchboardDeployments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceRevision",
                schema: "FancilPhones",
                table: "SwitchboardDeployments",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SortAlphabetically", schema: "FancilPhones", table: "SwitchboardProfiles");
            migrationBuilder.DropColumn(
                name: "DraftJson",
                schema: "FancilPhones",
                table: "SwitchboardProfiles");

            migrationBuilder.DropColumn(
                name: "DraftRevision",
                schema: "FancilPhones",
                table: "SwitchboardProfiles");

            migrationBuilder.DropColumn(
                name: "SourcePhoneId",
                schema: "FancilPhones",
                table: "SwitchboardDeployments");

            migrationBuilder.DropColumn(
                name: "SourceRevision",
                schema: "FancilPhones",
                table: "SwitchboardDeployments");
        }
    }
}
