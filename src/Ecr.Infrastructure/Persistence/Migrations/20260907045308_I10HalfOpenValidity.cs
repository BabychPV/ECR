using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Крок <c>I.10</c>: вікна чинності стають напівінтервалом
    /// <c>[ValidFrom, ValidTo)</c> (директива ПК-1 №05 §7, пастка 5).
    /// </summary>
    /// <remarks>
    /// ⛔ Міграція ПЕРЕНОСИТЬ ДАНІ, а не лише правила. До неї <c>ValidTo</c>
    /// означав «останній чинний день», після неї — «перший НЕчинний», і без
    /// <c>DATEADD(day, 1, …)</c> кожен наявний рядок мовчки вкоротився б на
    /// добу: дозвіл, чинний до 31 грудня, перестав би діяти 31 грудня, а
    /// побачив би це лише той, хто саме того дня заповнює звіт.
    ///
    /// ⚠ <c>sec.RoleAssignment</c> НЕ чіпається навмисно: там межа лишається
    /// включною (<c>D2-123</c>) — вона походить не з міграції, а з наказу про
    /// підміну, і зсув на день відібрав би права на день раніше, ніж написано
    /// в наказі.
    ///
    /// ⚠ Сентинел <c>9999-12-31</c> у даних зламає <c>DATEADD</c> — і саме
    /// цього ми й хочемо: у нашій моделі «без обмеження» — це <c>NULL</c>, а
    /// не дата, і мовчазний пропуск такого рядка залишив би в базі дату, яка
    /// рано чи пізно потрапить у різницю дат і дасть 7975 років.
    /// </remarks>
    public partial class I10HalfOpenValidity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            // 1. Обмеження знімається ПЕРШИМ: старе (`<=`) не заважає, але
            //    нове (`<`) не можна ставити на неперенесені дані.
            migrationBuilder.DropCheckConstraint(
                name: "CK_RegEntry_Period",
                schema: "dic",
                table: "RegistryEntry");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MC_Period",
                schema: "calc",
                table: "MethodologyConstant");

            // 2. Включна межа → виключна.
            migrationBuilder.Sql(
                "UPDATE dic.RegistryEntry SET ValidTo = CAST(DATEADD(day, 1, ValidTo) AS date) "
                + "WHERE ValidTo IS NOT NULL;");

            migrationBuilder.Sql(
                "UPDATE calc.MethodologyConstant SET ValidTo = CAST(DATEADD(day, 1, ValidTo) AS date) "
                + "WHERE ValidTo IS NOT NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RegEntry_Period",
                schema: "dic",
                table: "RegistryEntry",
                sql: "ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom < ValidTo");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MC_Period",
                schema: "calc",
                table: "MethodologyConstant",
                sql: "ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom < ValidTo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "CK_RegEntry_Period",
                schema: "dic",
                table: "RegistryEntry");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MC_Period",
                schema: "calc",
                table: "MethodologyConstant");

            // ⚠ Відкат НЕ бездоганний, і це треба знати заздалегідь: вікно
            // `[from, from+1)` (один чинний день) повернеться як
            // `from … from`, і це правильно, а от вікно, заведене вже після
            // міграції з `ValidTo = ValidFrom`, тут стало б порожнім — але
            // такого не існує: його не пускає `CK_*_Period` із `<`.
            migrationBuilder.Sql(
                "UPDATE dic.RegistryEntry SET ValidTo = CAST(DATEADD(day, -1, ValidTo) AS date) "
                + "WHERE ValidTo IS NOT NULL;");

            migrationBuilder.Sql(
                "UPDATE calc.MethodologyConstant SET ValidTo = CAST(DATEADD(day, -1, ValidTo) AS date) "
                + "WHERE ValidTo IS NOT NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RegEntry_Period",
                schema: "dic",
                table: "RegistryEntry",
                sql: "ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom <= ValidTo");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MC_Period",
                schema: "calc",
                table: "MethodologyConstant",
                sql: "ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom <= ValidTo");
        }
    }
}
