using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// D-463: фільтровані індекси читання «Даних з PI» і «Звідки це значення».
    /// </summary>
    /// <remarks>
    /// ✎ Аудит 31.10 (F4): <c>IX_CollectionCoverage_PeriodEvents</c> — на журналі покриття, що росте без меж, тож сирим
    /// SQL, як у <c>AN34CollectionCoverageDedupIndex</c>: <c>ONLINE = ON</c> лише там, де він є (Enterprise/Developer —
    /// <c>EngineEdition = 3</c>, Azure — 5, 8; на Standard і Express — помилка 1712, там офлайн), і з перевіркою
    /// наявності (<c>INDEXPROPERTY</c>). Умова обчислюється на сервері, тож <c>MigrateAsync</c> і ідемпотентний
    /// <c>migration.sql</c> дають той самий результат. База, де міграцію вже застосовано, її не повторює (запис в
    /// історії); модель і знімок моделі не змінюються — індекс той самий.
    /// </remarks>
    public partial class D463RawDataReadIndexes : Migration
    {
        /// <summary>Вираз редакції сервера, з яким міграція будує команду індексу.</summary>
        public const string EngineEditionExpression = "CAST(SERVERPROPERTY('EngineEdition') AS int)";

        /// <summary>Ім'я індексу журналу покриття.</summary>
        public const string CoverageIndexName = "IX_CollectionCoverage_PeriodEvents";

        /// <summary>T-SQL, що створює індекс журналу покриття, якщо його ще немає, для заданої редакції сервера.</summary>
        /// <param name="engineEdition">Вираз T-SQL типу <c>int</c> (<see cref="EngineEditionExpression"/> або літерал у тесті).</param>
        /// <returns>Текст T-SQL.</returns>
        public static string CreateCoverageIndexSql(string engineEdition)
            => $"""
                DECLARE @d463Edition int = {engineEdition};
                DECLARE @d463Online nvarchar(40) =
                    CASE WHEN @d463Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;
                DECLARE @d463Create nvarchar(max) =
                      N'CREATE NONCLUSTERED INDEX {CoverageIndexName} ON itg.CollectionCoverage (SourceEntityId, PeriodKey, Id) '
                    + N'INCLUDE (Status, Details, CoveredFrom) WHERE [Status] IS NOT NULL AND [PeriodKey] IS NOT NULL'
                    + @d463Online
                    + N';';

                IF INDEXPROPERTY(OBJECT_ID(N'itg.CollectionCoverage'), N'{CoverageIndexName}', 'IndexID') IS NULL
                    EXEC sys.sp_executesql @d463Create;
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_EntityFieldMap_TargetCell",
                schema: "ext",
                table: "EntityFieldMap",
                columns: new[] { "TargetColumnDefId", "TargetRowKey" },
                filter: "[TargetColumnDefId] IS NOT NULL");

            migrationBuilder.Sql(CreateCoverageIndexSql(EngineEditionExpression));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EntityFieldMap_TargetCell",
                schema: "ext",
                table: "EntityFieldMap");

            migrationBuilder.DropIndex(
                name: "IX_CollectionCoverage_PeriodEvents",
                schema: "itg",
                table: "CollectionCoverage");
        }
    }
}
