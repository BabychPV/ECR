using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// D-3 (аудит 2026-11-10): одноразове прибирання історії дозволів копій аркушів, видалених до виправлення
    /// <c>DocumentSheetCopyStore.DeleteCopyAsync</c> (J01 додали після RS1, каскад копії не оновили).
    /// </summary>
    /// <remarks>
    /// ⚠ Модель не змінюється: лише <c>DELETE</c>. Ідемпотентно (другий прогін нічого не знаходить); <c>Down</c> порожній —
    /// видалені рядки були сиротами без копії. <c>arc.*</c> не чіпаємо. Зовнішнього ключа <c>SheetCopyId</c> →
    /// <c>doc.DocumentSheet</c> немає, тому видалення не падає.
    /// </remarks>
    public partial class RS2_PermitHistoryCopyOrphans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE p FROM doc.CellPermitPart AS p
                 WHERE p.SheetCopyId IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM doc.DocumentSheet AS s WHERE s.Id = p.SheetCopyId);
                DELETE g FROM doc.PermitSegment AS g
                 WHERE g.SheetCopyId IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM doc.DocumentSheet AS s WHERE s.Id = g.SheetCopyId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Видалені рядки були сиротами: відновлювати нічого.
        }
    }
}
