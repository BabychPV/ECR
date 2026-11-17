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
            migrationBuilder.Sql(FlagTemplatesSql("HSE400", "HSE401"));
        }

        /// <summary>
        /// Разове виправлення даних B151: прапорець «один документ на проєкт» шаблонам із названими кодами — лише тим,
        /// де жоден проєкт не має кількох документів шаблону; решту пропускає з повідомленням (PRINT) і міграції не
        /// валить. Повторний прогін нічого не змінює.
        /// </summary>
        /// <remarks>
        /// Шаблон документа тут — власний <c>TemplateId</c> або, до ремонту 99-epilogue (він іде ПІСЛЯ міграцій),
        /// основний шаблон проєкту. Параметр — заради тестів (<c>B151MigrationSqlTests</c>) на власних кодах.
        /// </remarks>
        /// <param name="codes">Коди шаблонів (лише латинські літери й цифри).</param>
        internal static string FlagTemplatesSql(params string[] codes)
        {
            foreach (var code in codes)
            {
                if (code.Length == 0 || !code.All(char.IsAsciiLetterOrDigit))
                {
                    throw new ArgumentException($"Template code '{code}': ASCII letters and digits only.", nameof(codes));
                }
            }

            var list = string.Join(", ", codes.Select(c => $"N''{c}''"));
            return $$"""
                EXEC(N'
                DECLARE @dup TABLE (Code nvarchar(64) NOT NULL);
                INSERT INTO @dup (Code)
                SELECT t.Code
                FROM   cfg.Template t
                WHERE  t.Code IN ({{list}})
                  AND  t.SingleDocumentPerProject = 0
                  AND  EXISTS (
                        SELECT 1
                        FROM   doc.Document d
                        JOIN   doc.Project p ON p.Id = d.ProjectId
                        JOIN   cfg.TemplateVersion pv ON pv.Id = p.TemplateVersionId
                        WHERE  COALESCE(d.TemplateId, pv.TemplateId) = t.Id
                        GROUP BY d.ProjectId
                        HAVING COUNT(*) > 1);

                UPDATE t SET SingleDocumentPerProject = 1
                FROM   cfg.Template t
                WHERE  t.Code IN ({{list}})
                  AND  t.SingleDocumentPerProject = 0
                  AND  t.Code NOT IN (SELECT Code FROM @dup);

                DECLARE @skipped nvarchar(max) = STUFF((SELECT N'', '' + Code FROM @dup FOR XML PATH(''''), TYPE).value(''.'', ''nvarchar(max)''), 1, 2, N'''');
                IF @skipped IS NOT NULL
                    PRINT N''B151: skipped (projects already hold several documents), flag left off: '' + @skipped;');
                """;
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
