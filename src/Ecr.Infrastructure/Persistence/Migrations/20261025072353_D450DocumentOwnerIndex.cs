using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// D-450 (борг p44): <c>doc.Document (CreatedByUserId) INCLUDE (ProjectId)</c> — фільтр переліку «Власник»
    /// (<c>ownerUserId</c>) і вибір власників (<c>DocumentStore.ListOwnersAsync</c>) шукають і групують документи за
    /// <c>CreatedByUserId</c>; жоден наявний індекс (<c>PK</c> за <c>Id</c>, <c>UQ_Document (ProjectId, BusinessKey)</c>) цього
    /// не обслуговував — скан усієї таблиці. <c>ProjectId</c> в INCLUDE: обидва запити звіряють проєкт із межею видимості.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ Дані міграція не змінює, індекс не унікальний — передперевірки немає. Відкат (<c>Down</c>) — <c>DROP INDEX</c>.
    /// </para>
    /// <para>
    /// ⛔ Сирим SQL, а не <c>CreateIndex</c>: <c>ONLINE = ON</c> є лише на Enterprise/Developer (<c>EngineEdition = 3</c>) і
    /// Azure (5, 8); на Standard і Express (2, 4) він падає помилкою 1712. Механізм той самий, що в
    /// <c>R11L4SweeperAndApprovalIndexes</c>: умова обчислюється на сервері (<see cref="CreateIndexSql"/>), тож
    /// <c>MigrateAsync</c> і ідемпотентний <c>migration.sql</c> дають однаковий результат, а наявний індекс (зібраний DBA
    /// вручну чи повторний запуск після збою) нічого не ламає. doc.Document не партиційована — індекс на типовій групі,
    /// як <c>UQ_Document</c>. На Standard/Express побудова офлайн: запис у doc.Document чекає на час скану таблиці.
    /// </para>
    /// <para>Модель (<c>DocumentConfiguration</c>) оголошує той самий індекс — знімок збігається з базою.</para>
    /// </remarks>
    public partial class D450DocumentOwnerIndex : Migration
    {
        /// <summary>Вираз редакції сервера, з яким міграція будує команду індексу.</summary>
        public const string EngineEditionExpression = "CAST(SERVERPROPERTY('EngineEdition') AS int)";

        /// <summary>Ім'я індексу власника.</summary>
        public const string IndexName = "IX_Document_CreatedByUserId";

        /// <summary>T-SQL, що створює індекс, якщо його ще немає, для заданої редакції сервера.</summary>
        /// <param name="engineEdition">
        /// Вираз T-SQL типу <c>int</c>: <see cref="EngineEditionExpression"/> у міграції; літерал (<c>4</c>) у тесті, який
        /// перевіряє гілку без <c>ONLINE</c> на Developer-інстансі, де справжньої Express немає.
        /// </param>
        /// <returns>Текст T-SQL.</returns>
        public static string CreateIndexSql(string engineEdition)
            => $"""
                DECLARE @d450Edition int = {engineEdition};

                -- ONLINE лише там, де він є: 3 - Enterprise/Developer/Evaluation, 5 - Azure SQL Database,
                -- 8 - Managed Instance. На Standard (2) і Express (4) ONLINE = ON - помилка 1712; там офлайн.
                DECLARE @d450Online nvarchar(40) =
                    CASE WHEN @d450Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;

                DECLARE @d450Owner nvarchar(max) =
                      N'CREATE NONCLUSTERED INDEX {IndexName} ON doc.Document (CreatedByUserId) '
                    + N'INCLUDE (ProjectId)'
                    + @d450Online
                    + N';';

                IF INDEXPROPERTY(OBJECT_ID(N'doc.Document'), N'{IndexName}', 'IndexID') IS NULL
                    EXEC sys.sp_executesql @d450Owner;
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
                schema: "doc",
                table: "Document");
        }
    }
}
