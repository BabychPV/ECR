// src/Ecr.Application/Calculations/Scripting/ScriptStarter.cs
namespace Ecr.Application.Calculations.Scripting;

/// <summary>Заготовка тексту для нової порожньої версії-скрипта (MS, план 5.4).</summary>
/// <remarks>
/// ⚠ Увесь приклад закоментований: заготовка нічого не вимикає й не рахує, а сама лишається коректним
/// текстом. ✎ P-1124-1: текст лише з коментарів діагностик не має (людина 24.11: «свіжа чернетка — 0 проблем»);
/// опублікувати порожню версію не дасть <c>noBoundOutput</c>.
/// </remarks>
public static class ScriptStarter
{
    /// <summary>Стартовий текст.</summary>
    public const string Text = """
        // Скрипт методології (ECR Script). Розкоментуйте й змініть приклад під свою таблицю.
        // Script of the methodology. Uncomment the example and adapt it to your table.

        // 1. Які рядки рахуємо (код таблиці з шаблону; умови необов'язкові):
        // rows from T3_00
        //     where row.Land_TypeFuel == entry("LAND_FUEL", "Gasoline")
        //     active when row.Land_Status == entry("LAND_GENERIC_EQUIPSTATUS", "Running");

        // 2. Коефіцієнти (константи; нове значення з нової дати - нова версія методології):
        // const k1 = 5;
        // const EF = { Summer: { NOx: 1.2 }, Winter: { NOx: 1.4 } };

        // 3. Проміжні величини:
        // var hours  = row.Land_RunningHoursMonthly;
        // var season = period.Month >= 4 && period.Month <= 9 ? "Summer" : "Winter";

        // 4. Результати (код виходу : одиниця = вираз):
        // output ECW_EC_tons_NOx : t = hours * k1 * EF[season].NOx / 1000000;
        """;
}
