using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FancilPhones.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPhoneStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "FancilPhones");

            migrationBuilder.CreateTable(
                name: "Contacts",
                schema: "FancilPhones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OfficeNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MobileNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OtherNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Group = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Ring = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Phones",
                schema: "FancilPhones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Password = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Scheme = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    UploadPath = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UploadFieldName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SipDisplayName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SipLineIndex = table.Column<int>(type: "int", nullable: false),
                    SipExtension = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SipRegistrationEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    LastSyncMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Phones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuns",
                schema: "FancilPhones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhoneId = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContactCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuns_Phones_PhoneId",
                        column: x => x.PhoneId,
                        principalSchema: "FancilPhones",
                        principalTable: "Phones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_DisplayName",
                schema: "FancilPhones",
                table: "Contacts",
                column: "DisplayName");

            migrationBuilder.CreateIndex(
                name: "IX_Phones_IpAddress",
                schema: "FancilPhones",
                table: "Phones",
                column: "IpAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_PhoneId",
                schema: "FancilPhones",
                table: "SyncRuns",
                column: "PhoneId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Contacts",
                schema: "FancilPhones");

            migrationBuilder.DropTable(
                name: "SyncRuns",
                schema: "FancilPhones");

            migrationBuilder.DropTable(
                name: "Phones",
                schema: "FancilPhones");
        }
    }
}
