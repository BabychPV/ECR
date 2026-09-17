using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// «Актуальний прогін на область — щонайбільше один» стає інваріантом БАЗИ
    /// (<c>UX_CalculationRun_Current</c>, ФВ-9.11).
    /// </summary>
    /// <remarks>
    /// ⛔ Доти цей інваріант не тримало НІЩО: ні блокування, ні
    /// <c>rowversion</c>, ні індекс. <c>CalculationResultStore.SwitchCurrentRunAsync</c>
    /// знімає актуальність зі старих прогонів за ЗНІМКОМ, прочитаним на початку
    /// власної транзакції (TOCTOU, той самий клас, що <c>Q-241</c>/<c>Q-245</c>),
    /// тож два одночасні завершення прогонів одного проєкту й періоду одне
    /// одного не бачили й комітилися ОБИДВА — у <c>calc.CalculationRun</c>
    /// лишалося два рядки зі <c>Status = 'Current'</c>.
    ///
    /// ⚠ Наслідок не падав, а брехав: <c>ReadCurrentAsync</c> добирає
    /// результати підзапитом <c>EXISTS (… Status = 'Current')</c> і при двох
    /// актуальних прогонах повертає їх ОБ'ЄДНАННЯ — кожне число документа
    /// двічі, за двома різними версіями методології. Звіт при цьому будується
    /// і не кидає нічого.
    ///
    /// ⚠ Прийом той самий, що вже тримає «поточний зріз» у
    /// <c>UX_ReportSnapshot_Current</c> (<c>ReportSnapshotConfiguration</c>):
    /// унікальний фільтрований індекс. Скрипти розгортання
    /// (<c>Persistence/Sql/*.sql</c>) правити НЕ треба — <c>calc.CalculationRun</c>
    /// створюється міграціями, а не скриптом, і сам <c>UX_ReportSnapshot_Current</c>
    /// теж живе лише в міграції.
    /// </remarks>
    public partial class Q330CalculationRunCurrentUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // ⛔ Розв'язання ВЖЕ наявних дублікатів мусить іти ПЕРЕД створенням
            // індексу: на живій базі, яка встигла їх нажити, CREATE UNIQUE
            // INDEX інакше просто впаде, і міграція не пройде взагалі.
            //
            // Правило: в кожній області «проєкт × період» актуальним лишається
            // прогін із НАЙПІЗНІШИМ FinishedAt, а за однакового часу —
            // найбільший Id. Це той самий прогін, який лишив би чинним
            // послідовний (неконкурентний) виклик SwitchCurrentRunAsync: хто
            // завершився останнім, той і перемикав актуальність на себе. Id як
            // розв'язувач нічиєї — бо він монотонний, тобто правило дає ОДНУ Й
            // ТУ САМУ відповідь на будь-якій репліці й за будь-якого повтору.
            //
            // ⚠ FinishedAt DESC ставить NULL В КІНЕЦЬ (SQL Server сортує NULL
            // першими за ASC): прогін без часу завершення — той, що ніколи не
            // завершувався законно, і саме він мусить програти, а не виграти.
            //
            // ⚠ Переможені переводяться в 'Superseded', а НЕ видаляються: їхні
            // результати лишаються читабельними разом із прогоном, за яким їх
            // рахували (ЗБР-1, «нічого не затирається»). Міграція розрізняє,
            // ЯКІ числа показувати, а не стирає числа.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT Id,
                           ROW_NUMBER() OVER (
                               PARTITION BY ProjectId, PeriodKey
                               ORDER BY FinishedAt DESC, Id DESC) AS SlotNumber
                    FROM calc.CalculationRun
                    WHERE Status = N'Current')
                UPDATE r
                SET r.Status = N'Superseded'
                FROM calc.CalculationRun AS r
                INNER JOIN ranked AS d ON d.Id = r.Id
                WHERE d.SlotNumber > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_CalculationRun_Current",
                schema: "calc",
                table: "CalculationRun",
                columns: new[] { "ProjectId", "PeriodKey" },
                unique: true,
                filter: "[Status] = 'Current'");
        }

        /// <inheritdoc />
        /// <remarks>
        /// ⚠ Відкат знімає лише індекс. Повернути демотованим прогонам
        /// 'Current' неможливо й не потрібно: у таблиці немає сліду, котрі з
        /// 'Superseded' демотувала саме ця міграція, а головне — той стан і був
        /// дефектом. Відкат повертає базу до відсутності інваріанта, а не до
        /// зіпсованих даних.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "UX_CalculationRun_Current",
                schema: "calc",
                table: "CalculationRun");
        }
    }
}
