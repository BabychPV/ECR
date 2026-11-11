using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TL01_TemplateLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Layout",
                schema: "cfg",
                table: "TemplateVersion",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0)
                .Annotation("Relational:DefaultConstraintName", "DF_TV_Layout");

            // D-474 (4): річний проєкт не може стояти на розкладці «копія на місяць». Версії, якими користуються
            // ЛИШЕ річні проєкти (HSE401), стають YearWithMonthColumns; решта лишається PerMonthCopies (0).
            // ⚠ EXEC: в ідемпотентному скрипті колонки ще немає на час компіляції пакета.
            migrationBuilder.Sql("""
                EXEC(N'UPDATE v SET Layout = 1
                FROM   cfg.TemplateVersion v
                WHERE  EXISTS (SELECT 1 FROM doc.Project p
                               WHERE (p.TemplateVersionId = v.Id
                                      OR EXISTS (SELECT 1 FROM doc.ProjectTemplateVersion t
                                                 WHERE t.ProjectId = p.Id AND t.TemplateVersionId = v.Id))
                                 AND p.PeriodKind = 2)
                  AND  NOT EXISTS (SELECT 1 FROM doc.Project p
                               WHERE (p.TemplateVersionId = v.Id
                                      OR EXISTS (SELECT 1 FROM doc.ProjectTemplateVersion t
                                                 WHERE t.ProjectId = p.Id AND t.TemplateVersionId = v.Id))
                                 AND p.PeriodKind <> 2);');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Layout",
                schema: "cfg",
                table: "TemplateVersion")
                .Annotation("Relational:DefaultConstraintName", "DF_TV_Layout");
        }
    }
}
