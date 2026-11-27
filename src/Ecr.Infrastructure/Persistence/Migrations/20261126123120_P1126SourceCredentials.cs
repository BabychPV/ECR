using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ P-1126-SRCCRED: облікові дані джерела в <c>ext.DataSource</c> — <c>AuthMode</c> (0 = Windows), <c>UserName</c>,
    /// <c>PasswordProtected</c> (блоб DataProtection, як SMTP) і <c>CredentialsRejectedAt</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Дані не змінюються: <c>Up</c> без <c>UPDATE</c> — наявні рядки стають Windows через <c>DEFAULT 0</c> колонки
    /// (AC-R). ⛔ Відкат (<c>Down</c>) стирає паролі: джерела з UserPassword після нього ходять як Windows — перед
    /// відкатом їх вимикають (R-16).
    /// </remarks>
    public partial class P1126SourceCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "AuthMode",
                schema: "ext",
                table: "DataSource",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CredentialsRejectedAt",
                schema: "ext",
                table: "DataSource",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PasswordProtected",
                schema: "ext",
                table: "DataSource",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserName",
                schema: "ext",
                table: "DataSource",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthMode",
                schema: "ext",
                table: "DataSource");

            migrationBuilder.DropColumn(
                name: "CredentialsRejectedAt",
                schema: "ext",
                table: "DataSource");

            migrationBuilder.DropColumn(
                name: "PasswordProtected",
                schema: "ext",
                table: "DataSource");

            migrationBuilder.DropColumn(
                name: "UserName",
                schema: "ext",
                table: "DataSource");
        }
    }
}
