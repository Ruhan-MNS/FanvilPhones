using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FancilPhones.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SwitchboardKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SwitchboardDeployments",
                schema: "FancilPhones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneId = table.Column<int>(type: "int", nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProfileRevision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DirectoryHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwitchboardDeployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SwitchboardDeployments_Phones_PhoneId",
                        column: x => x.PhoneId,
                        principalSchema: "FancilPhones",
                        principalTable: "Phones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SwitchboardMembers",
                schema: "FancilPhones",
                columns: table => new
                {
                    ContactId = table.Column<int>(type: "int", nullable: false),
                    SortName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwitchboardMembers", x => x.ContactId);
                    table.ForeignKey(
                        name: "FK_SwitchboardMembers_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalSchema: "FancilPhones",
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SwitchboardOverrides",
                schema: "FancilPhones",
                columns: table => new
                {
                    PhoneId = table.Column<int>(type: "int", nullable: false),
                    ContactId = table.Column<int>(type: "int", nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(31)", maxLength: 31, nullable: true),
                    Value2 = table.Column<string>(type: "nvarchar(31)", maxLength: 31, nullable: true),
                    CapturedKeyJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwitchboardOverrides", x => new { x.PhoneId, x.ContactId });
                    table.ForeignKey(
                        name: "FK_SwitchboardOverrides_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalSchema: "FancilPhones",
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SwitchboardOverrides_Phones_PhoneId",
                        column: x => x.PhoneId,
                        principalSchema: "FancilPhones",
                        principalTable: "Phones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SwitchboardProfiles",
                schema: "FancilPhones",
                columns: table => new
                {
                    PhoneId = table.Column<int>(type: "int", nullable: false),
                    BaselineJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwitchboardProfiles", x => x.PhoneId);
                    table.ForeignKey(
                        name: "FK_SwitchboardProfiles_Phones_PhoneId",
                        column: x => x.PhoneId,
                        principalSchema: "FancilPhones",
                        principalTable: "Phones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SwitchboardDeployments_PhoneId_CreatedAt",
                schema: "FancilPhones",
                table: "SwitchboardDeployments",
                columns: new[] { "PhoneId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SwitchboardOverrides_ContactId",
                schema: "FancilPhones",
                table: "SwitchboardOverrides",
                column: "ContactId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SwitchboardDeployments",
                schema: "FancilPhones");

            migrationBuilder.DropTable(
                name: "SwitchboardMembers",
                schema: "FancilPhones");

            migrationBuilder.DropTable(
                name: "SwitchboardOverrides",
                schema: "FancilPhones");

            migrationBuilder.DropTable(
                name: "SwitchboardProfiles",
                schema: "FancilPhones");
        }
    }
}
