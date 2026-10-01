using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D234ColumnWidthPx : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WidthPx",
                schema: "cfg",
                table: "ColumnDef",
                type: "int",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ColumnDef_WidthPx",
                schema: "cfg",
                table: "ColumnDef",
                sql: "WidthPx IS NULL OR WidthPx BETWEEN 40 AND 800");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ColumnDef_WidthPx",
                schema: "cfg",
                table: "ColumnDef");

            migrationBuilder.DropColumn(
                name: "WidthPx",
                schema: "cfg",
                table: "ColumnDef");
        }
    }
}
