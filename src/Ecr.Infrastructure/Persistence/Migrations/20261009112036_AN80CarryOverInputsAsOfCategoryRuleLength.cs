using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// AN-80 (аудит 1B): <c>calc.CalculationRun.InputsAsOfUtc</c> (N2-03) і межа довжини
    /// <c>calc.CategoryRule.Expression</c> (<c>nvarchar(4000)</c>, N2-02 / L7-01).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>N2-03 / stale-for-B (D-324).</b> <c>InputsAsOfUtc</c> - nullable <c>datetime2(3)</c>: момент, на який актуальні
    /// входи чисел прогону, коли той переніс (<c>CarryOver</c>) результати старішого прогону. <c>NULL</c> - входи знято на
    /// старті (так і лишається для всіх наявних прогонів); читач застарілості міряє правку довідника від
    /// <c>COALESCE(InputsAsOfUtc, StartedAt)</c>. Додавання nullable-колонки без DEFAULT - зміна лише метаданих, таблицю
    /// (мільйони прогонів) не переписує й не блокує надовго; <c>ONLINE</c> тут не потрібен (він стосується індексів, як у
    /// <c>AN34CollectionCoverageDedupIndex</c>).
    /// </para>
    /// <para>
    /// <b>N2-02 / L7-01.</b> Колонка <c>calc.CategoryRule.Expression</c> була <c>nvarchar(max)</c>; домен
    /// (<c>MethodologyCategoryRule.Normalize</c>) і <c>ExpressionLengthGuard</c> уже обмежують вираз
    /// <c>MethodologyFormula.MaxExpressionLength</c> (4000), тепер це тримає й БД.
    /// </para>
    /// <para>
    /// ⛔ Передперевірка (зміна колонки) йде ПЕРШОЮ командою: база, де є правило довше за 4000 символів, зупиняється
    /// <c>THROW 50801</c> з переліком (правило, версія методології, довжина), а не помилкою 8152 про обрізання без
    /// імен. Нічого не скорочується автоматично: вираз правила - це бізнес-логіка, і обрізаний вираз був би іншою
    /// формулою (runbook §8.7). <c>DATALENGTH(...) &gt; 8000</c> (байти UTF-16), а не <c>LEN</c>: <c>LEN</c> не
    /// рахує кінцевих пробілів, які <c>nvarchar(4000)</c> теж не вмістить. Механізм той самий, що в
    /// <c>AN34SourceEntityRegistryUnique</c> і <c>AN37MethodologyEffectiveUnique</c>: власний номер з переліком до
    /// зміни схеми; <c>FOR XML PATH</c>, а не <c>STRING_AGG</c> (підлога сервера - 2016 SP1, <c>D-101</c>). Перевірка
    /// лежить у тілі міграції, тож обидва шляхи застосування - <c>MigrateAsync</c> і ідемпотентний <c>migration.sql</c> -
    /// виконують її.
    /// </para>
    /// <para>
    /// ⚠ <c>QUOTED_IDENTIFIER ON</c>: <c>calc.CalculationRun</c> має фільтрований індекс (<c>UX_CalculationRun_Current</c>),
    /// а зміна таблиці з таким індексом при <c>OFF</c> падає (1934). Опцію дають SqlClient (<c>MigrateAsync</c>) і
    /// <c>sqlcmd -I</c> (ідемпотентний <c>migration.sql</c>) - так само, як у <c>B18HotPathIndexes</c> і <c>MI02JobQueue</c>.
    /// </para>
    /// </remarks>
    public partial class AN80CarryOverInputsAsOfCategoryRuleLength : Migration
    {
        /// <summary>Номер помилки передперевірки: «правило категорії довше за межу» (AN-80, N2-02).</summary>
        public const int PrecheckErrorNumber = 50801;

        /// <summary>Межа довжини виразу правила, символів (<c>MethodologyFormula.MaxExpressionLength</c>).</summary>
        public const int MaxExpressionLength = 4000;

        /// <summary>Скільки правил-порушників показувати.</summary>
        private const int SampleSize = 10;

        /// <summary>T-SQL передперевірки: <c>THROW 50801</c> з переліком, якщо є правила довші за межу.</summary>
        public static string PrecheckSql { get; } = BuildPrecheckSql();

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrecheckSql);

            migrationBuilder.AlterColumn<string>(
                name: "Expression",
                schema: "calc",
                table: "CategoryRule",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<DateTime>(
                name: "InputsAsOfUtc",
                schema: "calc",
                table: "CalculationRun",
                type: "datetime2(3)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InputsAsOfUtc",
                schema: "calc",
                table: "CalculationRun");

            migrationBuilder.AlterColumn<string>(
                name: "Expression",
                schema: "calc",
                table: "CategoryRule",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000);
        }

        private static string BuildPrecheckSql()
            => $"""
                DECLARE @an80Count int, @an80Rows nvarchar(max);

                SELECT @an80Count = COUNT(*)
                FROM calc.CategoryRule
                WHERE DATALENGTH(Expression) > {MaxExpressionLength * 2};

                IF @an80Count > 0
                BEGIN
                    SET @an80Rows = STUFF((
                        SELECT TOP ({SampleSize})
                            N'; (правило ' + CONVERT(nvarchar(20), r.Id)
                            + N', версія методології ' + CONVERT(nvarchar(20), r.MethodologyVersionId)
                            + N', довжина ' + CONVERT(nvarchar(20), DATALENGTH(r.Expression) / 2) + N')'
                        FROM calc.CategoryRule AS r
                        WHERE DATALENGTH(r.Expression) > {MaxExpressionLength * 2}
                        ORDER BY r.Id
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');

                    DECLARE @an80Message nvarchar(2048) = LEFT(
                          N'Передперевірка AN-80 N2-02: оновлення зупинено ДО зміни схеми. '
                        + N'Міграція AN80CarryOverInputsAsOfCategoryRuleLength обмежує calc.CategoryRule.Expression '
                        + N'{MaxExpressionLength} символами (межа формули, L7-01), а в таблиці є правила категорії довші; '
                        + N'SQL Server дав би 8152 без переліку. '
                        + N'Правил: ' + CONVERT(nvarchar(20), @an80Count) + N'; перші: ' + ISNULL(@an80Rows, N'')
                        + NCHAR(10) + N'Схему й дані не змінено, автоматичного скорочення немає: обрізаний вираз - інша формула. '
                        + N'Що робити: скоротіть вираз кожного з цих правил (для чернетки версії - PUT category-rule, '
                        + N'для опублікованої - за погодженням з розробником) і повторіть оновлення, '
                        + N'див. docs/admin/operations-runbook.md, п. 8.7.',
                        2048);
                    THROW {PrecheckErrorNumber}, @an80Message, 1;
                END
                """;
    }
}
