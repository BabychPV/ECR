using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportSnapshotHashFormat : Migration
    {
        /// <summary>
        /// Формат контрольної суми зрізу (рішення 2026-09-21). ⚠ D-53: зміна
        /// АДИТИВНА — nullable-колонка без значення за замовчуванням, `rpt.v_*`
        /// перелічують колонки явно й не змінюються. Наявні зрізи лишаються
        /// NULL («ще не визначено»): формат не вгадується міграцією, його
        /// визначає звірка або нічна <c>ReportSnapshotFormatJob</c>.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HashFormat",
                schema: "rpt",
                table: "ReportSnapshot",
                type: "varchar(8)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReportSnapshot_HashFormat",
                schema: "rpt",
                table: "ReportSnapshot",
                sql: "[HashFormat] IN ('current', 'legacy')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReportSnapshot_HashFormat",
                schema: "rpt",
                table: "ReportSnapshot");

            migrationBuilder.DropColumn(
                name: "HashFormat",
                schema: "rpt",
                table: "ReportSnapshot");
        }
    }
}
