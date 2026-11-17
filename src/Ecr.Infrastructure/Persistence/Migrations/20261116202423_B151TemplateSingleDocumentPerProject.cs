using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class B151TemplateSingleDocumentPerProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SingleDocumentPerProject",
                schema: "cfg",
                table: "Template",
                type: "bit",
                nullable: false,
                defaultValue: false)
                .Annotation("Relational:DefaultConstraintName", "DF_Template_SingleDocumentPerProject");

            // ✎ B151 (рішення людини 16.11): HSE400 і HSE401 — один документ на проєкт. Разово ТУТ, а не в сіді:
            // сід виконується на кожному старті й повертав би прапорець, який адміністратор свідомо вимкнув.
            // Лише за кодом шаблону і лише без наявних дублікатів (інакше прапорець лишається вимкненим, а
            // дублікати називає перевірка «увімкнути» в UI). EXEC — колонка щойно створена в цьому ж пакеті
            // ідемпотентного скрипта, і пряме посилання на неї не скомпілювалося б.
            migrationBuilder.Sql("""
                EXEC(N'
                UPDATE t SET SingleDocumentPerProject = 1
                FROM   cfg.Template t
                WHERE  t.Code IN (N''HSE400'', N''HSE401'')
                  AND  t.SingleDocumentPerProject = 0
                  AND  NOT EXISTS (
                        SELECT 1
                        FROM   doc.Document d
                        JOIN   doc.Project p ON p.Id = d.ProjectId
                        JOIN   cfg.TemplateVersion pv ON pv.Id = p.TemplateVersionId
                        WHERE  COALESCE(d.TemplateId, pv.TemplateId) = t.Id
                        GROUP BY d.ProjectId
                        HAVING COUNT(*) > 1);');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SingleDocumentPerProject",
                schema: "cfg",
                table: "Template")
                .Annotation("Relational:DefaultConstraintName", "DF_Template_SingleDocumentPerProject");
        }
    }
}
