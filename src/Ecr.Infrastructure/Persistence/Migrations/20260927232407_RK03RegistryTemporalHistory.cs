using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// RK03 (RT-04, D-158): системна історія <c>dic.RegistryEntry</c> і
    /// <c>dic.RegistryValue</c> і автор зміни рядка (<c>ChangedByUserId</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Тіло написане вручну, а не згенероване <c>AlterTable(...IsTemporal)</c>.
    /// Генератор EF дає три відхилення від контракту (FEATURE-REGISTRY-TABLES
    /// §3.2, 02a §5), і кожне змінює поведінку:
    /// <list type="bullet">
    /// <item>наявні рядки отримували <c>PeriodStart = 0001-01-01</c>, тобто
    /// «станом на» БУДЬ-ЯКИЙ момент до міграції повертав би нинішні значення так,
    /// ніби вони були чинні завжди. Контракт (§3.5): до міграції системного часу
    /// немає — <c>AS OF</c> раніше за неї порожній, тому
    /// <c>DEFAULT</c> від <c>SYSUTCDATETIME()</c> (із запасом у минуле — див.
    /// <see cref="PeriodStartDefault"/>);</item>
    /// <item>обмеження за замовчуванням — безіменні (<c>DF__RegistryE__…</c>), а
    /// відкат мусить знімати їх за іменем;</item>
    /// <item>генерований <c>Down</c> ВИДАЛЯЄ історичні таблиці, а <c>D-25</c>
    /// забороняє видаляти історію: відкат їх перейменовує.</item>
    /// </list>
    /// ⚠ Кожен оператор — окремий <c>Sql(...)</c> і сам по собі не посилається на
    /// колонку, додану іншим оператором того ж пакета: скрипт без
    /// <c>--idempotent</c> EF видає ОДНИМ пакетом, і посилання на щойно додану
    /// колонку падає на компіляції з <c>Msg 207</c> (саме так падає RK02).
    /// Колонки й період додаються одним <c>ALTER TABLE</c> — так і вимагає SQL
    /// Server для <c>GENERATED ALWAYS</c> на таблиці з даними.
    /// </remarks>
    public partial class RK03RegistryTemporalHistory : Migration
    {
        /// <summary>
        /// Початок періоду, який SQL Server ставить рядкам, що вже є в таблиці.
        /// </summary>
        /// <remarks>
        /// ⛔ Не голий <c>SYSUTCDATETIME()</c>. <c>ADD PERIOD FOR SYSTEM_TIME</c>
        /// відхиляє відкриті рядки з початком періоду в майбутньому (Msg 13542)
        /// і порівнює з годинником у момент ПЕРЕВІРКИ, а не обчислення
        /// <c>DEFAULT</c>. <c>SYSUTCDATETIME()</c> — <c>datetime2(7)</c>, і
        /// перетворення в <c>datetime2(3)</c> округлює ВГОРУ до 0,5 мс: якщо
        /// перевірка встигає раніше, ніж минуло це півмілісекунди, міграція
        /// падає на базі з рядками. Так і було в CI (Linux-контейнер, b12c5860,
        /// <c>D148ScalePrecheckTests</c>); на Windows той самий ALTER триває
        /// довше за похибку, тож локально не відтворювалось.
        /// Запас в одну секунду перекриває і округлення, і розбіжність джерел
        /// часу. Ціна: «станом на» в останню секунду перед міграцією вже бачить
        /// нинішні значення; наявні рядки однаково НЕ отримують <c>0001-01-01</c>,
        /// тож <c>AS OF</c> раніше за міграцію лишається порожнім (§3.5).
        /// ⚠ На вставки після міграції не впливає: <c>GENERATED ALWAYS</c>
        /// ставить час сам, ігноруючи <c>DEFAULT</c>.
        /// Доказ — <c>RK03PeriodStartMarginTests</c>.
        /// </remarks>
        internal const string PeriodStartDefault = "DATEADD(second, -1, SYSUTCDATETIME())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            EnableHistory(migrationBuilder, "RegistryEntry", "RegistryEntryHistory", "RegEntry");
            EnableHistory(migrationBuilder, "RegistryValue", "RegistryValueHistory", "RegValue");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DisableHistory(migrationBuilder, "RegistryValue", "RegistryValueHistory", "RegValue");
            DisableHistory(migrationBuilder, "RegistryEntry", "RegistryEntryHistory", "RegEntry");
        }

        private static void EnableHistory(MigrationBuilder migrationBuilder, string table, string history, string prefix)
        {
            migrationBuilder.Sql($"""
                ALTER TABLE [dic].[{table}] ADD
                    [ChangedByUserId] int NULL,
                    [PeriodStart] datetime2(3) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL
                        CONSTRAINT [DF_{prefix}_PS] DEFAULT {PeriodStartDefault},
                    [PeriodEnd]   datetime2(3) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL
                        CONSTRAINT [DF_{prefix}_PE] DEFAULT CONVERT(datetime2(3), '9999-12-31 23:59:59.999'),
                    PERIOD FOR SYSTEM_TIME ([PeriodStart], [PeriodEnd]);
                """);

            migrationBuilder.Sql(
                $"ALTER TABLE [dic].[{table}] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dic].[{history}]));");
        }

        /// <remarks>
        /// ⛔ Історична таблиця не видаляється (<c>D-25</c>), а перейменовується з
        /// міткою часу відкату: повторний <c>Up</c> створить нову порожню, а
        /// попередня історія лишиться поруч для DBA.
        /// </remarks>
        private static void DisableHistory(MigrationBuilder migrationBuilder, string table, string history, string prefix)
        {
            migrationBuilder.Sql($"ALTER TABLE [dic].[{table}] SET (SYSTEM_VERSIONING = OFF);");
            migrationBuilder.Sql($"ALTER TABLE [dic].[{table}] DROP PERIOD FOR SYSTEM_TIME;");
            migrationBuilder.Sql($"ALTER TABLE [dic].[{table}] DROP CONSTRAINT [DF_{prefix}_PS], [DF_{prefix}_PE];");
            migrationBuilder.Sql($"ALTER TABLE [dic].[{table}] DROP COLUMN [PeriodStart], [PeriodEnd], [ChangedByUserId];");
            // ⚠ Ім'я змінної — своє для кожної таблиці: обидва відкати лягають в
            // ОДИН пакет (і з `--idempotent`, і без), а друге `DECLARE` тієї самої
            // змінної в пакеті — помилка компіляції (Msg 134).
            migrationBuilder.Sql($"""
                DECLARE @{history}Renamed sysname = N'{history}_RK03Down_' + FORMAT(SYSUTCDATETIME(), 'yyyyMMddHHmmssfff');
                EXEC sp_rename N'[dic].[{history}]', @{history}Renamed;
                """);
        }
    }
}
