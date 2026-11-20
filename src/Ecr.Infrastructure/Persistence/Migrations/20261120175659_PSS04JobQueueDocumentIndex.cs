using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ PSS-04/06 (аудит 20.11, <c>D-561</c>): <c>itg.JobProgress (DocumentId, State) INCLUDE (Lane)</c> лише для живих
    /// рядків черги з документом — claim не бере задачу документа, який уже рахує <c>Running</c> лейна з локом документа
    /// (<c>DbJobQueue.DocumentExclusion</c>), а задача перерахунку читає задачі свого документа, що чекають
    /// (<c>DbJobQueue.ListQueuedForDocumentAsync</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ Дані міграція не змінює, індекс не унікальний — передперевірки немає. Відкат (<c>Down</c>) — <c>DROP INDEX</c>.
    /// </para>
    /// <para>
    /// ⛔ Сирим SQL, а не <c>CreateIndex</c>: механізм <c>D450DocumentOwnerIndex</c> — <c>ONLINE = ON</c> лише там, де він є
    /// (<c>EngineEdition</c> 3 — Enterprise/Developer, 5, 8 — Azure; на Standard і Express — помилка 1712, там офлайн), умова
    /// обчислюється на сервері, тож <c>MigrateAsync</c> і ідемпотентний <c>migration.sql</c> дають однаковий результат, а
    /// наявний індекс (зібраний DBA вручну чи повторний запуск після збою) нічого не ламає. Таблиця — черга з історією
    /// (30 днів), фільтр лишає в індексі лише живі рядки, тож побудова — секунди навіть офлайн.
    /// </para>
    /// <para>⚠ Фільтрований індекс: сеанс, що пише в таблицю, має <c>QUOTED_IDENTIFIER ON</c> (SqlClient — типово; <c>sqlcmd -I</c>).</para>
    /// <para>Модель (<c>JobProgressConfiguration.ConfigureQueue</c>) оголошує той самий індекс — знімок збігається з базою.</para>
    /// </remarks>
    public partial class PSS04JobQueueDocumentIndex : Migration
    {
        /// <summary>Вираз редакції сервера, з яким міграція будує команду індексу.</summary>
        public const string EngineEditionExpression = "CAST(SERVERPROPERTY('EngineEdition') AS int)";

        /// <summary>Ім'я індексу.</summary>
        public const string IndexName = "IX_JobProgress_Document_Active";

        /// <summary>T-SQL, що створює індекс, якщо його ще немає, для заданої редакції сервера.</summary>
        /// <param name="engineEdition">
        /// Вираз T-SQL типу <c>int</c>: <see cref="EngineEditionExpression"/> у міграції; літерал (<c>4</c>) у тесті гілки без
        /// <c>ONLINE</c> на Developer-інстансі.
        /// </param>
        /// <returns>Текст T-SQL.</returns>
        public static string CreateIndexSql(string engineEdition)
            => $"""
                DECLARE @pss04Edition int = {engineEdition};

                -- ONLINE лише там, де він є: 3 - Enterprise/Developer/Evaluation, 5 - Azure SQL Database,
                -- 8 - Managed Instance. На Standard (2) і Express (4) ONLINE = ON - помилка 1712; там офлайн.
                DECLARE @pss04Online nvarchar(40) =
                    CASE WHEN @pss04Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;

                DECLARE @pss04Create nvarchar(max) =
                      N'CREATE NONCLUSTERED INDEX {IndexName} ON itg.JobProgress (DocumentId, [State]) '
                    + N'INCLUDE (Lane) WHERE [State] IN (''Queued'', ''Running'') AND [DocumentId] IS NOT NULL'
                    + @pss04Online
                    + N';';

                IF INDEXPROPERTY(OBJECT_ID(N'itg.JobProgress'), N'{IndexName}', 'IndexID') IS NULL
                    EXEC sys.sp_executesql @pss04Create;
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
                table: "JobProgress");
        }
    }
}
