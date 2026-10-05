using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// AN-34 L4-11 (аудит 1D): індекс <c>IX_CollectionCoverage_RegistryEvents</c> на
    /// <c>itg.CollectionCoverage (SourceEntityId, Id) INCLUDE (Status, PeriodKey, Details)
    /// WHERE [Status] IS NOT NULL AND [PeriodKey] IS NULL</c> - під дедуп подій синку довідника.
    /// </summary>
    /// <remarks>
    /// Запит дедупу (<c>RegistrySyncJob.DedupJournal</c>) читає події однієї сутності зі
    /// <c>Status IS NOT NULL</c> у порядку <c>Id</c>. Єдиний індекс за сутністю
    /// (<c>IX_CollectionCoverage_SourceEntity_CoveredTo</c>) має фільтр <c>Status IS NULL</c>, тож
    /// запит ішов сканом усього журналу щопрогону синку.
    /// <para>
    /// ⚠ <c>PeriodKey</c> у INCLUDE, хоч у індексі він завжди NULL (це фільтр): без нього
    /// оптимізатор після Index Seek робив Key Lookup лише для перевірки <c>PeriodKey IS NULL</c>
    /// (заміряно планом <c>RegistrySyncDedupPlanTests</c>).
    /// </para>
    /// <para>
    /// ⚠ Дані міграція не змінює, дублів немає за визначенням (індекс не унікальний), тож
    /// передперевірки, як у L4-01, тут немає.
    /// </para>
    /// <para>
    /// ⚠ Індекс - сирим SQL, а не <c>CreateIndex</c>: <c>ONLINE = ON</c> є лише на
    /// Enterprise/Developer (<c>EngineEdition = 3</c>) і Azure (5, 8); на Standard і Express
    /// (2, 4) він падає помилкою 1712. Механізм той самий, що в U1 (<c>U1UnitForeignKeys</c>):
    /// умова обчислюється на сервері (<see cref="CreateIndexSql"/>), тож <c>MigrateAsync</c> і
    /// ідемпотентний <c>migration.sql</c> дають однаковий результат. Створення охоронене
    /// перевіркою наявності: повторний запуск на базі, де індекс уже є (збірка вручну до
    /// оновлення), нічого не ламає. Розмір і час побудови - runbook §8.5.
    /// </para>
    /// </remarks>
    public partial class AN34CollectionCoverageDedupIndex : Migration
    {
        /// <summary>Вираз редакції сервера, з яким міграція будує команду індексу.</summary>
        public const string EngineEditionExpression = "CAST(SERVERPROPERTY('EngineEdition') AS int)";

        /// <summary>Ім'я індексу.</summary>
        public const string IndexName = "IX_CollectionCoverage_RegistryEvents";

        /// <summary>
        /// T-SQL, що створює індекс, якщо його ще немає, для заданої редакції сервера.
        /// </summary>
        /// <param name="engineEdition">
        /// Вираз T-SQL типу <c>int</c>: <see cref="EngineEditionExpression"/> у міграції;
        /// літерал (<c>4</c>) у тесті, який перевіряє гілку без <c>ONLINE</c> на
        /// Developer-інстансі, де справжньої Express немає.
        /// </param>
        /// <returns>Текст T-SQL.</returns>
        public static string CreateIndexSql(string engineEdition)
            => $"""
                DECLARE @an34Edition int = {engineEdition};

                -- ⛔ ONLINE лише там, де він є: 3 - Enterprise/Developer/Evaluation, 5 - Azure SQL
                -- Database, 8 - Managed Instance. На Standard (2) і Express (4) ONLINE = ON - помилка
                -- 1712; там індекс будується офлайн, і таблиця журналу на час побудови заблокована
                -- (runbook §8.5).
                DECLARE @an34Online nvarchar(40) =
                    CASE WHEN @an34Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;

                DECLARE @an34Create nvarchar(max) =
                      N'CREATE NONCLUSTERED INDEX {IndexName} ON itg.CollectionCoverage (SourceEntityId, Id) '
                    + N'INCLUDE (Status, PeriodKey, Details) WHERE [Status] IS NOT NULL AND [PeriodKey] IS NULL'
                    + @an34Online
                    + N';';

                IF INDEXPROPERTY(OBJECT_ID(N'itg.CollectionCoverage'), N'{IndexName}', 'IndexID') IS NULL
                    EXEC sys.sp_executesql @an34Create;
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreateIndexSql(EngineEditionExpression));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: IndexName,
                schema: "itg",
                table: "CollectionCoverage");
        }
    }
}
