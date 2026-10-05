using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// AN-37 L7-08 (аудит 1G): фільтрований унікальний <c>UQ_MV_Effective</c> на
    /// <c>calc.MethodologyVersion (MethodologyId, EffectiveFrom) WHERE Status = 1</c> - не більше
    /// однієї ОПУБЛІКОВАНОЇ версії методології від однієї дати (ФВ-13.3).
    /// </summary>
    /// <remarks>
    /// ⛔ Передперевірка йде ПЕРШОЮ командою: база, де гонка двох публікацій уже лишила дві
    /// опубліковані версії однієї методології на одну дату, зупиняється <c>THROW 50708</c> з
    /// переліком (методологія, дата, версії), а не SQL-помилкою 1505 без імен. Нічого не змінюється
    /// автоматично: яку з версій лишити чинною, вирішує людина (runbook §8.6). Статус
    /// <c>Deprecated</c> і чернетки перевіркою й індексом не зачеплені.
    /// ⚠ Механізм той самий, що в L4-01 (<c>AN34SourceEntityRegistryUnique</c>), U1 і Q222:
    /// <c>THROW</c> власного номера з переліком до зміни схеми; <c>FOR XML PATH</c>, а не
    /// <c>STRING_AGG</c> (підлога сервера - 2016 SP1, <c>D-101</c>). Перевірка лежить у тілі
    /// міграції, тож обидва шляхи застосування - <c>MigrateAsync</c> і ідемпотентний
    /// <c>migration.sql</c> - виконують її.
    /// </remarks>
    public partial class AN37MethodologyEffectiveUnique : Migration
    {
        /// <summary>Номер помилки передперевірки: «дві опубліковані версії на одну дату» (L7-08).</summary>
        public const int PrecheckErrorNumber = 50708;

        /// <summary>Скільки груп-порушників показувати.</summary>
        private const int SampleSize = 10;

        /// <summary>T-SQL передперевірки: <c>THROW 50708</c> з переліком, якщо є дублі.</summary>
        public static string PrecheckSql { get; } = BuildPrecheckSql();

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrecheckSql);

            migrationBuilder.CreateIndex(
                name: "UQ_MV_Effective",
                schema: "calc",
                table: "MethodologyVersion",
                columns: new[] { "MethodologyId", "EffectiveFrom" },
                unique: true,
                filter: "[Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_MV_Effective",
                schema: "calc",
                table: "MethodologyVersion");
        }

        private static string BuildPrecheckSql()
            => $"""
                DECLARE @an37Count int, @an37Rows nvarchar(max);

                SELECT @an37Count = COUNT(*)
                FROM (
                    SELECT 1 AS One
                    FROM calc.MethodologyVersion
                    WHERE Status = 1
                    GROUP BY MethodologyId, EffectiveFrom
                    HAVING COUNT(*) > 1
                ) AS g;

                IF @an37Count > 0
                BEGIN
                    SET @an37Rows = STUFF((
                        SELECT TOP ({SampleSize})
                            N'; (методологія ' + CONVERT(nvarchar(20), s.MethodologyId)
                            + N', чинна від ' + CONVERT(nvarchar(10), s.EffectiveFrom, 23)
                            + N', версії: '
                            + STUFF((
                                SELECT N', ' + CONVERT(nvarchar(20), t.Id) + N' ' + t.Version
                                FROM calc.MethodologyVersion AS t
                                WHERE t.Status = 1 AND t.MethodologyId = s.MethodologyId AND t.EffectiveFrom = s.EffectiveFrom
                                ORDER BY t.Id
                                FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'')
                            + N')'
                        FROM calc.MethodologyVersion AS s
                        WHERE s.Status = 1
                        GROUP BY s.MethodologyId, s.EffectiveFrom
                        HAVING COUNT(*) > 1
                        ORDER BY s.MethodologyId, s.EffectiveFrom
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');

                    DECLARE @an37Message nvarchar(2048) = LEFT(
                          N'Передперевірка AN-37 L7-08: оновлення зупинено ДО зміни схеми. '
                        + N'Міграція AN37MethodologyEffectiveUnique додає унікальний індекс UQ_MV_Effective '
                        + N'(одна опублікована версія методології на дату набуття чинності), а в calc.MethodologyVersion '
                        + N'є опубліковані версії однієї методології з однією датою; SQL Server дав би 1505 без переліку. '
                        + N'Груп (методологія, дата): ' + CONVERT(nvarchar(20), @an37Count) + N'; перші: ' + ISNULL(@an37Rows, N'')
                        + NCHAR(10) + N'Схему й дані не змінено, автоматичного виправлення немає. '
                        + N'Що робити: залиште чинною одну версію з кожної групи, а зайву виведіть з обігу '
                        + N'(Status = 2, Deprecated) або змініть їй EffectiveFrom, і повторіть оновлення, '
                        + N'див. docs/admin/operations-runbook.md, п. 8.6.',
                        2048);
                    THROW {PrecheckErrorNumber}, @an37Message, 1;
                END
                """;
    }
}
