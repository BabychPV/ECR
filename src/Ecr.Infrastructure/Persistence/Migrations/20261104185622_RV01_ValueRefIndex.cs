using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Індекс <c>IX_RegistryValue_ValueRef</c> (фільтрований, за <c>ValueRefEntryId</c>) на <c>dic.RegistryValue</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ D-5 (аудит 2026-11-10): сирим SQL, а не <c>CreateIndex</c> — <c>ONLINE = ON</c> там, де він є (Enterprise/
    /// Developer/Azure), і перевірка наявності (індекс, зібраний DBA вручну до оновлення, не ламає міграцію помилкою
    /// 1913). Механізм той самий, що в <c>D450DocumentOwnerIndex</c>. Модель не змінилась: <c>Down</c>, Designer і знімок —
    /// як були; бази, де міграцію вже застосовано, її не виконують (EF звіряє лише <c>MigrationId</c>).
    /// </remarks>
    public partial class RV01_ValueRefIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreateIndexSql(D450DocumentOwnerIndex.EngineEditionExpression));
        }

        /// <summary>T-SQL індексу для заданої редакції (літерал — у тесті гілки без ONLINE).</summary>
        /// <param name="engineEdition">Вираз T-SQL типу <c>int</c>; див. <see cref="D450DocumentOwnerIndex.EngineEditionExpression"/>.</param>
        /// <returns>Текст T-SQL.</returns>
        public static string CreateIndexSql(string engineEdition)
            => $"""
                DECLARE @rv01Edition int = {engineEdition};
                DECLARE @rv01Online nvarchar(40) =
                    CASE WHEN @rv01Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;
                DECLARE @rv01 nvarchar(max) =
                      N'CREATE NONCLUSTERED INDEX IX_RegistryValue_ValueRef ON dic.RegistryValue (ValueRefEntryId) '
                    + N'WHERE ValueRefEntryId IS NOT NULL'
                    + @rv01Online
                    + N';';

                IF INDEXPROPERTY(OBJECT_ID(N'dic.RegistryValue'), N'IX_RegistryValue_ValueRef', 'IndexID') IS NULL
                    EXEC sys.sp_executesql @rv01;
                """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RegistryValue_ValueRef",
                schema: "dic",
                table: "RegistryValue");
        }
    }
}
