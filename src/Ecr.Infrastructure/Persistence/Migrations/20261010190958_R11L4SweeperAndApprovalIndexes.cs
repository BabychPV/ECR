using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R11 L4 (аудит 5/6): два індекси під запити, що читали таблиці цілком.
    /// <list type="bullet">
    /// <item><description>
    /// <b>Q1-04.</b> <c>calc.CalculationRun (StartedAt) WHERE [Status] = 'Running'</c> - прибиральник покинутих прогонів
    /// (<c>AbandonedWorkSweeper.CloseCalculationRunsAsync</c>) щохвилини читає
    /// <c>Status = 'Running' AND StartedAt &lt; @межа ORDER BY StartedAt</c>; жоден наявний індекс його не обслуговував
    /// (<c>UX_CalculationRun_Current</c> - фільтр <c>Current</c>, <c>IX_CalculationRun_Project_FinishedAt</c> починається з
    /// <c>ProjectId</c>), тож щохвилини скан усієї таблиці (мільйони прогонів). У індексі лише прогони, що йдуть зараз.
    /// </description></item>
    /// <item><description>
    /// <b>X6-03.</b> <c>wf.ApprovalEvent (DocumentId, At DESC) INCLUDE (Action, ByUserId)</c> - підзапит
    /// <c>approverDisplayName</c> переліку документів (<c>DocumentStore</c>) читає події кожного документа сторінки від
    /// найновішої; наявний <c>IX_ApprovalEvent_Document</c> має <c>PeriodKey</c> між <c>DocumentId</c> і <c>At</c> і не
    /// містить <c>Action</c>/<c>ByUserId</c>, тож порядок не віддавав, а поля діставав key lookup на кожну подію.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ Дані міграція не змінює, обидва індекси не унікальні, тож передперевірки (як у AN34/AN37) немає: нічого,
    /// що могло б завадити створенню, у даних бути не може. Відкат (<c>Down</c>) - <c>DROP INDEX</c> обох.
    /// </para>
    /// <para>
    /// ⛔ Індекси - сирим SQL, а не <c>CreateIndex</c>: <c>ONLINE = ON</c> є лише на Enterprise/Developer
    /// (<c>EngineEdition = 3</c>) і Azure (5, 8); на Standard і Express (2, 4) він падає помилкою 1712. Механізм той самий,
    /// що в <c>AN34CollectionCoverageDedupIndex</c>: умова обчислюється на сервері (<see cref="CreateIndexesSql"/>), тож
    /// <c>MigrateAsync</c> і ідемпотентний <c>migration.sql</c> дають однаковий результат, а повторний запуск на базі, де
    /// індекс уже є, нічого не ламає. На Standard/Express побудова ОФЛАЙН: запис у <c>calc.CalculationRun</c> (і
    /// <c>wf.ApprovalEvent</c>) чекає на час скану таблиці - секунди на мільйони рядків (реліз-нотатки, runbook §8.8).
    /// </para>
    /// <para>
    /// ⚠ <c>QUOTED_IDENTIFIER ON</c> потрібен фільтрованому індексу: його дають SqlClient (<c>MigrateAsync</c>) і
    /// <c>sqlcmd -I</c> (ідемпотентний <c>migration.sql</c>), як у <c>B18HotPathIndexes</c> і <c>AN80</c>.
    /// </para>
    /// </remarks>
    public partial class R11L4SweeperAndApprovalIndexes : Migration
    {
        /// <summary>Вираз редакції сервера, з яким міграція будує команди індексів.</summary>
        public const string EngineEditionExpression = "CAST(SERVERPROPERTY('EngineEdition') AS int)";

        /// <summary>Ім'я індексу прибиральника (Q1-04).</summary>
        public const string RunningIndexName = "IX_CalculationRun_Running";

        /// <summary>Ім'я індексу «хто затвердив» (X6-03).</summary>
        public const string ApprovalIndexName = "IX_ApprovalEvent_DocumentAt";

        /// <summary>
        /// T-SQL, що створює обидва індекси, якщо їх ще немає, для заданої редакції сервера.
        /// </summary>
        /// <param name="engineEdition">
        /// Вираз T-SQL типу <c>int</c>: <see cref="EngineEditionExpression"/> у міграції; літерал (<c>4</c>) у тесті, який
        /// перевіряє гілку без <c>ONLINE</c> на Developer-інстансі, де справжньої Express немає.
        /// </param>
        /// <returns>Текст T-SQL.</returns>
        public static string CreateIndexesSql(string engineEdition)
            => $"""
                DECLARE @r11Edition int = {engineEdition};

                -- ONLINE лише там, де він є: 3 - Enterprise/Developer/Evaluation, 5 - Azure SQL Database,
                -- 8 - Managed Instance. На Standard (2) і Express (4) ONLINE = ON - помилка 1712; там офлайн.
                DECLARE @r11Online nvarchar(40) =
                    CASE WHEN @r11Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;

                DECLARE @r11Running nvarchar(max) =
                      N'CREATE NONCLUSTERED INDEX {RunningIndexName} ON calc.CalculationRun (StartedAt) '
                    + N'WHERE [Status] = ''Running'''
                    + @r11Online
                    + N';';

                DECLARE @r11Approval nvarchar(max) =
                      N'CREATE NONCLUSTERED INDEX {ApprovalIndexName} ON wf.ApprovalEvent (DocumentId, [At] DESC) '
                    + N'INCLUDE ([Action], ByUserId)'
                    + @r11Online
                    + N';';

                IF INDEXPROPERTY(OBJECT_ID(N'calc.CalculationRun'), N'{RunningIndexName}', 'IndexID') IS NULL
                    EXEC sys.sp_executesql @r11Running;

                IF INDEXPROPERTY(OBJECT_ID(N'wf.ApprovalEvent'), N'{ApprovalIndexName}', 'IndexID') IS NULL
                    EXEC sys.sp_executesql @r11Approval;
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreateIndexesSql(EngineEditionExpression));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: RunningIndexName,
                schema: "calc",
                table: "CalculationRun");

            migrationBuilder.DropIndex(
                name: ApprovalIndexName,
                schema: "wf",
                table: "ApprovalEvent");
        }
    }
}
