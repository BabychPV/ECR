// src/Ecr.Calculations/FlareEventArguments.cs
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;

namespace Ecr.Calculations;

/// <summary>
/// Похідні аргументи методології факела з події: <c>@Total</c>, <c>@Duration</c>, <c>@FlareUnitMode</c>,
/// <c>@IsPilot</c>, <c>@Category</c> (AN-5, імпорт методологій з AF).
/// </summary>
/// <remarks>
/// ⛔ Це не колонки таблиці прив'язки. В AF формули пишуть голе <c>Total</c>/<c>Duration</c>/<c>FlareUnitMode</c>,
/// а значення подає CLR-модуль профілю — дослівно <c>R_Air_HSE30X_Cont_General.cs:116–139</c>:
/// <c>Total = flare.Value</c>; <c>FlareUnitMode</c> — <c>Pr_Type</c> «HP» → 1, «LP» → 2, інакше 0;
/// <c>IsPilot</c> — <c>PurgePilot_Type = "Pilot"</c>; <c>Category = flare.FlareName</c>.
/// <para>
/// ⚠ <c>Duration</c> — НЕ «кінець мінус початок» (так не рахує жоден CLR-модуль): для безперервних
/// подій (<c>R_Air_HSE30X_Cont_General.cs:140,229</c>) це константа <c>86400</c> (секунд у добі: подія вже
/// агрегована за добу); для переривчастих (<c>R_Air_HSE30X_Int_FG_Calculate.cs:49</c>,
/// <c>Int_SG</c>) — збережений атрибут події <c>Duration</c> (секунди, <c>Q_Air_HSE30X_Int.cs:153</c>), тобто
/// звичайна колонка рядка, яка підставляється як є. Тому похідне <c>Duration = 86400</c> підставляється
/// лише коли в рядка події колонки <c>Duration</c> немає.
/// </para>
/// <para>
/// Для переривчастих <c>FlareUnitMode</c> береться з перших двох знаків <c>FlareUnit</c>
/// (<c>R_Air_HSE30X_Int_FG_Calculate.cs:25–40</c>), коли колонки <c>Pr_Type</c> немає.
/// </para>
/// Чисті функції: ні сховища, ні годинника, ні запитів. Значення беруться з уже прочитаних комірок рядка
/// (<c>CalculationInputBuilder</c>): атрибути події лягли туди за картою полів (<c>SourceEventMap</c>), тож
/// гарячий шлях нових запитів не отримує.
/// </remarks>
public static class FlareEventArguments
{
    /// <summary>Секунд у добі — значення <c>Duration</c> безперервної події.</summary>
    public const decimal DurationSeconds = 86400m;

    // Атрибути події факела, за наявності яких рядок вважається рядком події.
    private const string PressureType = "Pr_Type";
    private const string PurgePilotType = "PurgePilot_Type";
    private const string FlareName = "FlareName";
    private const string FlareUnit = "FlareUnit";
    private const string EventValue = "Value";

    /// <summary>Будує аргументи з атрибутів події факела (безперервна подія).</summary>
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
        var mode = ModeFromPressure(pressureType);

        return
        [
            new CalculationArgument(DerivedArgumentNames.Total, total, null, totalUnitId),
            new CalculationArgument(DerivedArgumentNames.Duration, DurationSeconds, null, durationUnitId),
            ModeArgument(mode),
            PilotArgument(purgePilotType),
            new CalculationArgument(DerivedArgumentNames.Category, null, flareName, null),
        ];
    }

    /// <summary>
    /// Доповнює аргументи рядка похідними, яких у ньому немає (колонка з тим самим кодом має пріоритет).
    /// </summary>
    /// <param name="rowArguments">Аргументи, зібрані з комірок рядка.</param>
    /// <returns>
    /// Той самий список, якщо рядок не є рядком події (немає жодної з колонок <c>Pr_Type</c>,
    /// <c>PurgePilot_Type</c>, <c>FlareName</c>, <c>FlareUnit</c>); інакше — з доданими похідними.
    /// </returns>
    public static IReadOnlyList<CalculationArgument> Derive(IReadOnlyList<CalculationArgument> rowArguments)
    {
        ArgumentNullException.ThrowIfNull(rowArguments);

        if (rowArguments.Count == 0)
        {
            return rowArguments;
        }

        var byCode = new Dictionary<string, CalculationArgument>(rowArguments.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var argument in rowArguments)
        {
            byCode.TryAdd(argument.ArgumentCode, argument);
        }

        if (!byCode.ContainsKey(PressureType) && !byCode.ContainsKey(PurgePilotType)
            && !byCode.ContainsKey(FlareName) && !byCode.ContainsKey(FlareUnit))
        {
            return rowArguments;
        }

        var result = new List<CalculationArgument>(rowArguments);

        // Total — об'єм події (flare.Value); немає ні Total, ні Value — аргумент не з'являється.
        if (!byCode.ContainsKey(DerivedArgumentNames.Total) && byCode.TryGetValue(EventValue, out var value))
        {
            result.Add(value with { ArgumentCode = DerivedArgumentNames.Total });
        }

        // Duration — колонка рядка (переривчаста подія) має пріоритет; інакше доба.
        if (!byCode.ContainsKey(DerivedArgumentNames.Duration))
        {
            result.Add(new CalculationArgument(DerivedArgumentNames.Duration, DurationSeconds, null, null));
        }

        if (!byCode.ContainsKey(DerivedArgumentNames.FlareUnitMode))
        {
            var mode = byCode.TryGetValue(PressureType, out var pressure)
                ? ModeFromPressure(pressure.ValueString)
                : ModeFromFlareUnit(byCode.TryGetValue(FlareUnit, out var unit) ? unit.ValueString : null);
            result.Add(ModeArgument(mode));
        }

        if (!byCode.ContainsKey(DerivedArgumentNames.IsPilot))
        {
            result.Add(PilotArgument(byCode.TryGetValue(PurgePilotType, out var pilot) ? pilot.ValueString : null));
        }

        if (!byCode.ContainsKey(DerivedArgumentNames.Category) && byCode.TryGetValue(FlareName, out var name))
        {
            result.Add(new CalculationArgument(DerivedArgumentNames.Category, null, name.ValueString, null));
        }

        return result;
    }

    private static decimal ModeFromPressure(string? pressureType) => pressureType switch
    {
        "HP" => 1m,
        "LP" => 2m,
        _ => 0m,
    };

    // Переривчаста подія: перші два знаки FlareUnit (R_Air_HSE30X_Int_FG_Calculate.cs:25–40).
    private static decimal ModeFromFlareUnit(string? flareUnit)
        => ModeFromPressure(flareUnit is { Length: >= 2 } ? flareUnit[..2] : flareUnit);

    private static CalculationArgument ModeArgument(decimal mode)
        => new(DerivedArgumentNames.FlareUnitMode, mode, mode.ToString(CultureInfo.InvariantCulture), null);

    private static CalculationArgument PilotArgument(string? purgePilotType)
        => new(
            DerivedArgumentNames.IsPilot,
            string.Equals(purgePilotType, "Pilot", StringComparison.Ordinal) ? 1m : 0m,
            null,
            null);
}
