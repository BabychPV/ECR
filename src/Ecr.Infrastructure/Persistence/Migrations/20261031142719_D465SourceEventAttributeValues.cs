using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D465SourceEventAttributeValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceEventAttributeValue",
                schema: "ext",
                columns: table => new
                {
                    LinkId = table.Column<long>(type: "bigint", nullable: false),
                    Scope = table.Column<byte>(type: "tinyint", nullable: false),
                    Attribute = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ValueNumeric = table.Column<decimal>(type: "decimal(34,16)", precision: 34, scale: 16, nullable: true),
                    ValueText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    IsTruncated = table.Column<bool>(type: "bit", nullable: false),
                    SourceUnit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ValueHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    SourceModifiedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RunId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceEventAttributeValue", x => new { x.LinkId, x.Scope, x.Attribute });
                    table.CheckConstraint("CK_SEAV_Scope", "Scope BETWEEN 0 AND 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SEL_MapStart",
                schema: "ext",
                table: "SourceEventLink",
                columns: new[] { "SourceEventMapId", "StartUtc", "Id" })
                .Annotation("SqlServer:Include", new[] { "Status", "PrimaryElement", "EventName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceEventAttributeValue",
                schema: "ext");

            migrationBuilder.DropIndex(
                name: "IX_SEL_MapStart",
                schema: "ext",
                table: "SourceEventLink");
        }
    }
}
