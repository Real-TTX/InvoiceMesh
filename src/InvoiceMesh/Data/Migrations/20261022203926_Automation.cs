using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InvoiceMesh.Data.Migrations
{
    /// <inheritdoc />
    public partial class Automation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceDetail",
                table: "Invoice",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiKey",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Token = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false),
                    KeyPrefix = table.Column<string>(type: "text", nullable: false),
                    CanImport = table.Column<bool>(type: "boolean", nullable: false),
                    CanRead = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKey", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExportPlan",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ExportProfileId = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Selection = table.Column<string>(type: "text", nullable: false),
                    Days = table.Column<int>(type: "integer", nullable: false),
                    OnlyValid = table.Column<bool>(type: "boolean", nullable: false),
                    SkipEmpty = table.Column<bool>(type: "boolean", nullable: false),
                    Schedule = table.Column<string>(type: "text", nullable: false),
                    IntervalHours = table.Column<int>(type: "integer", nullable: false),
                    TimeOfDay = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    Delivery = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Host = table.Column<string>(type: "text", nullable: true),
                    Share = table.Column<string>(type: "text", nullable: true),
                    Domain = table.Column<string>(type: "text", nullable: true),
                    UserName = table.Column<string>(type: "text", nullable: true),
                    PasswordProtected = table.Column<string>(type: "text", nullable: true),
                    Url = table.Column<string>(type: "text", nullable: true),
                    HeaderName = table.Column<string>(type: "text", nullable: true),
                    HeaderValueProtected = table.Column<string>(type: "text", nullable: true),
                    CoveredUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRunDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunOk = table.Column<bool>(type: "boolean", nullable: false),
                    LastStatus = table.Column<string>(type: "text", nullable: true),
                    LastCount = table.Column<int>(type: "integer", nullable: false),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportPlan", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExportPlan_ExportProfile_ExportProfileId",
                        column: x => x.ExportProfileId,
                        principalTable: "ExportProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntakeSource",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    DoneFolder = table.Column<string>(type: "text", nullable: true),
                    Host = table.Column<string>(type: "text", nullable: true),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Share = table.Column<string>(type: "text", nullable: true),
                    Domain = table.Column<string>(type: "text", nullable: true),
                    UserName = table.Column<string>(type: "text", nullable: true),
                    PasswordProtected = table.Column<string>(type: "text", nullable: true),
                    Security = table.Column<string>(type: "text", nullable: false),
                    MailFolder = table.Column<string>(type: "text", nullable: false),
                    NextRunDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunOk = table.Column<bool>(type: "boolean", nullable: false),
                    LastStatus = table.Column<string>(type: "text", nullable: true),
                    LastCount = table.Column<int>(type: "integer", nullable: false),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeSource", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKey_KeyHash",
                table: "ApiKey",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKey_Token",
                table: "ApiKey",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExportPlan_ExportProfileId",
                table: "ExportPlan",
                column: "ExportProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKey");

            migrationBuilder.DropTable(
                name: "ExportPlan");

            migrationBuilder.DropTable(
                name: "IntakeSource");

            migrationBuilder.DropColumn(
                name: "SourceDetail",
                table: "Invoice");
        }
    }
}
