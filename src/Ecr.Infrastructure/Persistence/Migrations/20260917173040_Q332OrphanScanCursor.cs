using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Позиція відновлюваного сканування в <c>itg.ScanCursor</c> (<c>Q-332</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Без цієї таблиці нічна перевірка осиротілих рядків не мала ДЕ лишити
    /// позицію між прогонами — і тому не лишала її взагалі: один запит
    /// <c>Take(20_000)</c> без упорядкування щоночі, проти таблиці на ~108 млн
    /// рядків на рік. Оглядалося довільне вікно, решта не перевірялася ніколи,
    /// а задача звітувала про успіх в обох випадках.
    ///
    /// ⚠ Позиція — ПАРА <c>(PeriodKey, RowId)</c>, а не лічильник пройдених
    /// рядків. Первинний ключ <c>doc.TableRow</c> складений і в цьому ж порядку
    /// кластерний, тож пара перетворює кожну пачку на засічку по діапазону;
    /// <c>OFFSET</c> не дав би ні засічки, ні стійкості до вставок і видалень.
    ///
    /// ⚠ БЕЗ бекфілу й без рядків за замовчуванням. Курсор заводить сам сканер
    /// при першому прогоні (<c>OrphanScanner.LoadCursorAsync</c>) і заводить
    /// його на ПОЧАТКУ набору — тобто перша ніч після розгортання йде від
    /// початку, як і має. Рядок, засіяний міграцією, довелося б тримати
    /// синхронним із кодами сканувань у двох місцях.
    ///
    /// ⚠ Індексу немає навмисно: рядків тут стільки, скільки в системі
    /// відновлюваних сканувань (одиниці), і читаються вони лише за первинним
    /// ключем.
    ///
    /// ⚠ Таблиця НЕ партиційована і в <c>07-partition-tables.sql</c> не
    /// згадується: вона не росте з обсягом даних. Схема <c>itg</c> живе в
    /// моделі EF (на відміну від <c>sys_ecr</c> і <c>arc</c>), тож міграція —
    /// єдине місце, де така таблиця створюється, і скрипти розгортання правити
    /// не треба.
    /// </remarks>
    public partial class Q332OrphanScanCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScanCursor",
                schema: "itg",
                columns: table => new
                {
                    ScanCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CyclesCompleted = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    LastCycleCompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanCursor", x => x.ScanCode);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanCursor",
                schema: "itg");
        }
    }
}
