using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CellStyleUserColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CellStyle",
                schema: "doc",
                columns: table => new
                {
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    TableRowId = table.Column<long>(type: "bigint", nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false),
                    BackgroundHex = table.Column<string>(type: "char(7)", nullable: true),
                    ForegroundHex = table.Column<string>(type: "char(7)", nullable: true),
                    IsBold = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_CellStyle_Bold"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CellStyle", x => new { x.PeriodKey, x.TableRowId, x.ColumnDefId });
                    table.CheckConstraint("CK_CellStyle_Background", "BackgroundHex IS NULL OR BackgroundHex LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'");
                    table.CheckConstraint("CK_CellStyle_Foreground", "ForegroundHex IS NULL OR ForegroundHex LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CellStyle",
                schema: "doc");
        }
    }
}
