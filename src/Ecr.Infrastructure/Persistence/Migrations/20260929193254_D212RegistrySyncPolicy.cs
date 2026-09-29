using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Політика синку довідника з AF (<c>D-212</c>, PR-2):
    /// <c>ext.SourceEntity.OnMissingInSource</c> / <c>ValidFromAttribute</c> /
    /// <c>ValidToAttribute</c> / <c>ValidToInclusive</c>,
    /// <c>dic.RegistryExternalKey.MissingInSourceSince</c>, <c>CK_SE_OnMissingInSource</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ NOT NULL-колонки — з DEFAULT (0 / false): у таблицях уже є рядки.
    /// Обидві таблиці не темпоральні, <c>PERIOD</c> не додається (пастка
    /// <c>Msg 13542</c> тут не виникає).
    /// </remarks>
    public partial class D212RegistrySyncPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<byte>(
                name: "OnMissingInSource",
                schema: "ext",
                table: "SourceEntity",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<string>(
                name: "ValidFromAttribute",
                schema: "ext",
                table: "SourceEntity",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValidToAttribute",
                schema: "ext",
                table: "SourceEntity",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ValidToInclusive",
                schema: "ext",
                table: "SourceEntity",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "MissingInSourceSince",
                schema: "dic",
                table: "RegistryExternalKey",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SE_OnMissingInSource",
                schema: "ext",
                table: "SourceEntity",
                sql: "[OnMissingInSource] IN (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "CK_SE_OnMissingInSource",
                schema: "ext",
                table: "SourceEntity");

            migrationBuilder.DropColumn(
                name: "OnMissingInSource",
                schema: "ext",
                table: "SourceEntity");

            migrationBuilder.DropColumn(
                name: "ValidFromAttribute",
                schema: "ext",
                table: "SourceEntity");

            migrationBuilder.DropColumn(
                name: "ValidToAttribute",
                schema: "ext",
                table: "SourceEntity");

            migrationBuilder.DropColumn(
                name: "ValidToInclusive",
                schema: "ext",
                table: "SourceEntity");

            migrationBuilder.DropColumn(
                name: "MissingInSourceSince",
                schema: "dic",
                table: "RegistryExternalKey");
        }
    }
}
