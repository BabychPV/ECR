using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ PERF-01/10 (аудит PERF1000 21.11, огляд лейна H2/M2; <c>D-564a</c>, <c>D-564c</c>): (1) <c>calc.CalculationRun.CellChangeWatermark</c>
    /// — межа журналу комірок у мить завершення прогону (порівняння «записано після прогону» без годинників різних хостів);
    /// (2) <c>itg.JobProgress (Lane, State, CreatedByUserId) INCLUDE (AvailableAt, FanOutParentJobId)</c> лише для живих рядків
    /// черги — «черга людей» (<c>DbJobQueue.FairRankSql</c>) рахує задачі людини пошуком, без читання Payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ Дані не змінюються: стовпець nullable без DEFAULT — зміна лише метаданих; індекс не унікальний — передперевірки немає.
    /// Прогони до міграції лишаються з <c>NULL</c> — планувальник кнопки року рахує такі місяці (одне повне натискання).
    /// </para>
    /// <para>
    /// ⛔ Індекс — сирим SQL, механізм <c>PSS04JobQueueDocumentIndex</c>: <c>ONLINE = ON</c> лише там, де він є (EngineEdition 3,
    /// 5, 8), умова на сервері; наявний індекс не ламає повторний запуск. Фільтр лишає в індексі лише живі рядки черги.
    /// </para>
    /// </remarks>
    public partial class PERF564FairQueueAndRunWatermark : Migration
    {
        /// <summary>Ім'я індексу.</summary>
        public const string IndexName = "IX_JobProgress_User_Active";

        /// <summary>T-SQL, що створює індекс, якщо його ще немає, для заданої редакції сервера.</summary>
        /// <param name="engineEdition">Вираз T-SQL типу <c>int</c> (<see cref="PSS04JobQueueDocumentIndex.EngineEditionExpression"/>).</param>
        /// <returns>Текст T-SQL.</returns>
        public static string CreateIndexSql(string engineEdition)
            => $"""
                DECLARE @perf564Edition int = {engineEdition};

                -- ONLINE лише там, де він є: 3 - Enterprise/Developer/Evaluation, 5 - Azure SQL Database,
                -- 8 - Managed Instance. На Standard (2) і Express (4) ONLINE = ON - помилка 1712; там офлайн.
                DECLARE @perf564Online nvarchar(40) =
                    CASE WHEN @perf564Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;

                DECLARE @perf564Create nvarchar(max) =
                      N'CREATE NONCLUSTERED INDEX {IndexName} ON itg.JobProgress (Lane, [State], CreatedByUserId) '
                    + N'INCLUDE (AvailableAt, FanOutParentJobId) WHERE [Lane] IS NOT NULL AND [State] IN (''Queued'', ''Running'')'
                    + @perf564Online
                    + N';';

                IF INDEXPROPERTY(OBJECT_ID(N'itg.JobProgress'), N'{IndexName}', 'IndexID') IS NULL
                    EXEC sys.sp_executesql @perf564Create;
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CellChangeWatermark",
                schema: "calc",
                table: "CalculationRun",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(CreateIndexSql(PSS04JobQueueDocumentIndex.EngineEditionExpression));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: IndexName,
                schema: "itg",
                table: "JobProgress");

            migrationBuilder.DropColumn(
                name: "CellChangeWatermark",
                schema: "calc",
                table: "CalculationRun");
        }
    }
}
