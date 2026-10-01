// src/Ecr.Calculations/FlareEventArguments.cs
using System.Globalization;
using Ecr.Application.Ports;

namespace Ecr.Calculations;

/// <summary>
/// Аргументи методології факела з події: <c>@Total</c>, <c>@Duration</c>, <c>@FlareUnitMode</c>,
/// <c>@IsPilot</c>, <c>@Category</c> (AN-5, імпорт методологій з AF).
/// </summary>
/// <remarks>
/// ⛔ Це не колонки документа. В AF формули пишуть голе <c>Total</c>/<c>Duration</c>/<c>FlareUnitMode</c>
/// (імпорт перейменував їх на <c>@…</c>), а значення подає CLR-модуль профілю — дослівно
/// <c>R_Air_HSE30X_Cont_General.cs:116–139</c>: <c>Total = flare.Value</c>; <c>FlareUnitMode</c> —
/// <c>Pr_Type</c> «HP» → 1, «LP» → 2, інакше 0; <c>IsPilot</c> — <c>PurgePilot_Type = "Pilot"</c>;
/// <c>Category = flare.FlareName</c>.
/// <para>
/// ⚠ <c>Duration</c> — НЕ «кінець мінус початок події», а константа <c>86400</c> (секунд у добі):
/// чинний модуль подає її так само для кожної події, бо подія факела вже агрегована за добу.
/// Рахувати її з меж події означало б розійтися з чинними числами.
/// </para>
/// Чиста функція: ні сховища, ні годинника. Підключення до збірки входів (колонки події →
/// <c>CalculationInputBuilder</c>) лишається на карті полів події (<c>SourceEventMap</c>).
/// </remarks>
public static class FlareEventArguments
{
    /// <summary>Секунд у добі — значення <c>Duration</c> чинного модуля.</summary>
    public const decimal DurationSeconds = 86400m;

    /// <summary>Будує аргументи з атрибутів події факела.</summary>
    /// <param name="total">Значення події (<c>flare.Value</c>); <c>null</c> — порожній <c>@Total</c>.</param>
    /// <param name="pressureType"><c>Pr_Type</c>: «HP», «LP» або інше.</param>
    /// <param name="purgePilotType"><c>PurgePilot_Type</c>: «Pilot» або інше.</param>
    /// <param name="flareName"><c>FlareName</c> — категорія.</param>
    /// <param name="totalUnitId">Одиниця <c>Total</c>; <c>null</c> — безрозмірне.</param>
    /// <param name="durationUnitId">Одиниця <c>Duration</c> (секунда); <c>null</c> — безрозмірне.</param>
    /// <returns>П'ять аргументів у стабільному порядку.</returns>
    public static IReadOnlyList<CalculationArgument> Build(
        decimal? total,
        string? pressureType,
        string? purgePilotType,
        string? flareName,
        int? totalUnitId = null,
        int? durationUnitId = null)
    {
        var mode = pressureType switch
        {
            "HP" => 1m,
            "LP" => 2m,
            _ => 0m,
        };

        return
        [
            new CalculationArgument("Total", total, null, totalUnitId),
            new CalculationArgument("Duration", DurationSeconds, null, durationUnitId),
            new CalculationArgument("FlareUnitMode", mode, mode.ToString(CultureInfo.InvariantCulture), null),
            new CalculationArgument("IsPilot", string.Equals(purgePilotType, "Pilot", StringComparison.Ordinal) ? 1m : 0m, null, null),
            new CalculationArgument("Category", null, flareName, null),
        ];
    }
}
