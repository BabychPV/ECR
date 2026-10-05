// src/Ecr.Application/Sources/BoundaryUnitConversion.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Sources;

/// <summary>
/// Конверсія одиниць на межі інтеграції: <c>SourceUnitId</c> → <c>TargetUnitId</c>
/// мапінгу після згортки (HSE301 §4.2, <c>D-173</c>, ФВ-16.10).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Одна арифметика на всю межу. <c>SourceUnitConverter</c> (адаптер PI),
/// <c>MaterializeCollectedDataJob</c> (інфраструктура) і попередній перегляд
/// мапінгу кличуть саме цей клас; множення — у доменному
/// <see cref="UnitConverter"/>. Переїзд сюди з адаптера PI — тому, що
/// інфраструктура на адаптер не посилається, а друга копія розійшлася б із
/// першою тихо: обидві дають число, жодна не падає.
/// </para>
/// <para>
/// ⚠ Статичний навмисно: стану немає, а реєстрація в контейнері змінила б
/// конструктори задачі й обробника, яких тримають десятки тестів.
/// </para>
/// </remarks>
public static class BoundaryUnitConversion
{
    /// <summary>Базова одиниця часу, у якій <see cref="PeriodFold"/> повертає інтеграл.</summary>
    public const string SecondCode = "s";

    private static readonly UnitConverter SharedConverter = new();

    /// <summary>Конвертує одне значення з одиниці в одиницю.</summary>
    /// <param name="value">Значення у вихідній одиниці.</param>
    /// <param name="fromUnitId">Вихідна одиниця.</param>
    /// <param name="toUnitId">Цільова одиниця.</param>
    /// <param name="units">Знімок довідника.</param>
    /// <param name="converter">Доменний конвертер; <c>null</c> — спільний екземпляр.</param>
    /// <returns>Значення в цільовій одиниці.</returns>
    /// <exception cref="BusinessRuleException">
    /// Одиниці немає в знімку — <c>ECR-INT-0422</c> <c>unitMissingFromSnapshot</c>.
    /// </exception>
    /// <exception cref="Ecr.Domain.Abstractions.DomainException">
    /// Різні розмірності — <c>ECR-UOM-0422</c> (<c>Sm3</c> ↔ <c>m3</c>).
    /// </exception>
    /// <remarks>
    /// ⚠ Явні конверсії <c>uom.Conversion</c> сюди не доходять: знімок їх не
    /// несе, маршрут іде через базову одиницю. <c>LegacyPinned</c> існує заради
    /// збігу з поданим звітом чинної системи, а не заради сирої точки.
    /// </remarks>
    public static decimal Convert(
        decimal value, int fromUnitId, int toUnitId, UnitCatalogSnapshot units, UnitConverter? converter = null)
    {
        ArgumentNullException.ThrowIfNull(units);

        if (fromUnitId == toUnitId)
        {
            return value;
        }

        return (converter ?? SharedConverter).Convert(
            value, Spec(units, fromUnitId), Spec(units, toUnitId), explicitConversion: null);
    }

    /// <summary>Чи збігається фактична одиниця джерела з оголошеною (ФВ-16.9).</summary>
    /// <param name="declaredSourceUnitId">Одиниця з мапінгу; <c>null</c> — не оголошена.</param>
    /// <param name="actualSourceUnitCode">Одиниця, яку фактично повернуло джерело.</param>
    /// <param name="units">Знімок довідника.</param>
    /// <returns>
    /// <c>true</c> — збігається, або порівнювати нема з чим (джерело не повідомило
    /// одиниці, мапінг її не оголошує).
    /// </returns>
    /// <remarks>
    /// ⛔ Одна перевірка на всю межу (L3-06): збір PI (<c>SourceUnitConverter</c>),
    /// вікна рядків (<see cref="Integration.RowWindowFetch"/>) і події джерел
    /// (<c>SourceEventRowBuilder</c>) кличуть саме цей метод. Доти її бачив лише
    /// збір, а вікна й події конвертували за ОГОЛОШЕНОЮ одиницею: зміна UOM
    /// атрибута в PI (<c>Sm3/h</c> → <c>Sm3/d</c>) давала тиху помилку ×24.
    /// Джерело без одиниці — не «збіглося», а «нема з чим порівняти»:
    /// значення лягає в одиниці мапінгу (ФВ-16.12).
    /// </remarks>
    public static bool IsDeclaredUnit(int? declaredSourceUnitId, string? actualSourceUnitCode, UnitCatalogSnapshot units)
    {
        ArgumentNullException.ThrowIfNull(units);

        if (string.IsNullOrWhiteSpace(actualSourceUnitCode) || declaredSourceUnitId is not { } declared)
        {
            return true;
        }

        return units.Units.TryGetValue(actualSourceUnitCode, out var actual) && actual.Id == declared;
    }

    /// <summary>Переводить результат згортки в цільову одиницю мапінгу.</summary>
    /// <param name="kind">Спосіб згортання, яким отримано <paramref name="folded"/>.</param>
    /// <param name="folded">
    /// Результат <see cref="PeriodFold"/>: в одиниці джерела, а для
    /// <see cref="AggregationKind.TimeIntegral"/> — в «одиниця джерела × секунда».
    /// </param>
    /// <param name="sourceUnitId">Одиниця джерела з мапінгу; <c>null</c> — не оголошена.</param>
    /// <param name="targetUnitId">Цільова одиниця з мапінгу; <c>null</c> — не оголошена.</param>
    /// <param name="units">Знімок довідника.</param>
    /// <param name="converter">Доменний конвертер; <c>null</c> — спільний екземпляр.</param>
    /// <returns>Значення в цільовій одиниці й опис конверсії для журналу.</returns>
    /// <exception cref="BusinessRuleException">
    /// Одиниці немає в знімку; інтеграл без оголошених одиниць; інтеграл величини,
    /// що не є швидкістю «щось / час» — усе <c>ECR-UOM-0422</c> або <c>ECR-INT-0422</c>.
    /// </exception>
    /// <exception cref="Ecr.Domain.Abstractions.DomainException">Різні розмірності — <c>ECR-UOM-0422</c>.</exception>
    /// <remarks>
    /// <para>
    /// Згортки, крім інтеграла, лишають розмірність джерела: без обох одиниць
    /// чи за однакових одиниць значення не змінюється (ФВ-16.12) — побітно те
    /// саме число, що й до конверсії на межі.
    /// </para>
    /// <para>
    /// ⛔ <b>Інтеграл — через знаменник, а не через базову одиницю.</b>
    /// <c>Sm3/h × s</c> ділиться на тривалість знаменника в секундах
    /// (<c>h</c> = 3600), і лише потім чисельник (<c>Sm3</c>) конвертується в
    /// ціль. Маршрут через <c>FactorToBase</c> швидкості множив би на
    /// <c>1/3600</c>, якого скінченний десятковий запис не має:
    /// <c>3348 × 0.000277777777777778 = 0.930000000000000744</c> замість
    /// <c>0.93</c> — і місячний об'єм лягав би в комірку з хвостом.
    /// </para>
    /// <para>
    /// ⛔ Інтеграл без оголошених одиниць — відмова, а не число в «одиниця ×
    /// секунда»: у колонці об'єму воно було б у 3600 разів більшим за правду.
    /// </para>
    /// </remarks>
    public static BoundaryValue ConvertFolded(
        AggregationKind kind,
        decimal folded,
        int? sourceUnitId,
        int? targetUnitId,
        UnitCatalogSnapshot units,
        UnitConverter? converter = null)
    {
        ArgumentNullException.ThrowIfNull(units);

        if (kind == AggregationKind.TimeIntegral)
        {
            return Integral(folded, sourceUnitId, targetUnitId, units, converter ?? SharedConverter);
        }

        if (sourceUnitId is not { } from || targetUnitId is not { } to || from == to)
        {
            return BoundaryValue.Unchanged(folded);
        }

        var source = Spec(units, from);
        var target = Spec(units, to);
        var value = (converter ?? SharedConverter).Convert(folded, source, target, explicitConversion: null);

        return new BoundaryValue(value, source.Code, target.Code, null, null, Factor(source, target));
    }

    /// <summary>Інтеграл «швидкість × с» → чисельник швидкості → ціль.</summary>
    private static BoundaryValue Integral(
        decimal folded, int? sourceUnitId, int? targetUnitId, UnitCatalogSnapshot units, UnitConverter converter)
    {
        if (sourceUnitId is not { } from || targetUnitId is not { } to)
        {
            throw new BusinessRuleException(
                ErrorCodes.UnitDimensionMismatch,
                "Інтеграл за часом лягає в комірку лише з оголошеними одиницями джерела й цілі: "
                + "без них значення лишилося б в «одиниця × секунда».",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-UOM-0422.integralUnitsUndeclared",
                    ["sourceUnitId"] = sourceUnitId?.ToString(CultureInfo.InvariantCulture),
                    ["targetUnitId"] = targetUnitId?.ToString(CultureInfo.InvariantCulture),
                });
        }

        var source = Spec(units, from);
        var (numeratorId, denominatorId) = RateParts(units, from)
            ?? throw NotRate(source.Code);

        var denominator = Spec(units, denominatorId);
        if (!units.Units.TryGetValue(SecondCode, out var second)
            || second.DimensionId != denominator.DimensionId
            || second.FactorToBase == 0m
            || denominator.FactorToBase == 0m)
        {
            throw NotRate(source.Code);
        }

        // Секунд в одній одиниці знаменника: h → 3600. Ділення — ОСТАННІМ кроком
        // і на ціле число секунд: 3348 / 3600 = 0.93 рівно.
        var secondsPerDenominator = denominator.FactorToBase / second.FactorToBase;
        var inNumerator = folded / secondsPerDenominator;

        var numerator = Spec(units, numeratorId);
        var target = Spec(units, to);
        var value = converter.Convert(inNumerator, numerator, target, explicitConversion: null);

        return new BoundaryValue(
            value, source.Code, target.Code, denominator.Code, secondsPerDenominator, Factor(numerator, target));
    }

    /// <summary>Чисельник і знаменник похідної одиниці; <c>null</c> — одиниця не похідна.</summary>
    private static (int Numerator, int Denominator)? RateParts(UnitCatalogSnapshot units, int unitId)
    {
        foreach (var (key, id) in units.Derived)
        {
            if (id != unitId)
            {
                continue;
            }

            var bar = key.IndexOf('|', StringComparison.Ordinal);
            if (bar > 0
                && int.TryParse(key.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
                && int.TryParse(key.AsSpan(bar + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var denominator))
            {
                return (numerator, denominator);
            }
        }

        return null;
    }

    private static BusinessRuleException NotRate(string code)
        => new(
            ErrorCodes.UnitDimensionMismatch,
            $"Інтеграл за часом має сенс лише для швидкості «величина / час»; одиниця джерела «{code}» такою не є.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-UOM-0422.integralSourceNotRate",
                ["code"] = code,
            });

    /// <summary>Множник переходу; <c>null</c> — є зсув (температура), множником не описується.</summary>
    private static decimal? Factor(UnitSpec from, UnitSpec to)
        => from.OffsetToBase == 0m && to.OffsetToBase == 0m && to.FactorToBase != 0m
            ? from.FactorToBase / to.FactorToBase
            : null;

    /// <summary>Одиниця довідника у формі, потрібній конверсії.</summary>
    private static UnitSpec Spec(UnitCatalogSnapshot units, int unitId)
    {
        var unit = units.Units.Values.FirstOrDefault(u => u.Id == unitId)
                   ?? throw new BusinessRuleException(
                       ErrorCodes.SourceUnitChanged,
                       $"Одиниці {unitId} немає в довіднику: конверсія на межі неможлива.",
                       new Dictionary<string, object?>
                       {
                           ["messageKey"] = "err.ECR-INT-0422.unitMissingFromSnapshot",
                           ["unitId"] = unitId.ToString(CultureInfo.InvariantCulture),
                       });

        return new UnitSpec(unit.Id, unit.Code, unit.DimensionId, unit.FactorToBase, unit.OffsetToBase);
    }
}

/// <summary>Значення на межі разом з описом конверсії.</summary>
/// <param name="Value">Значення в цільовій одиниці мапінгу.</param>
/// <param name="FromCode">Одиниця джерела; <c>null</c> — конверсії не було.</param>
/// <param name="ToCode">Цільова одиниця; <c>null</c> — конверсії не було.</param>
/// <param name="DenominatorCode">Знаменник швидкості для інтеграла (<c>h</c>); <c>null</c> — не інтеграл.</param>
/// <param name="SecondsPerDenominator">Секунд в одиниці знаменника (<c>3600</c>); <c>null</c> — не інтеграл.</param>
/// <param name="Factor">
/// Множник переходу (для інтеграла — від чисельника до цілі); <c>null</c> —
/// конверсії не було або вона зі зсувом.
/// </param>
public sealed record BoundaryValue(
    decimal Value,
    string? FromCode,
    string? ToCode,
    string? DenominatorCode,
    decimal? SecondsPerDenominator,
    decimal? Factor)
{
    /// <summary>Чи змінила межа одиницю значення.</summary>
    public bool IsConverted => FromCode is not null;

    /// <summary>Без конверсії: значення лягає як є.</summary>
    /// <param name="value">Значення.</param>
    public static BoundaryValue Unchanged(decimal value) => new(value, null, null, null, null, null);

    /// <summary>Опис для журналу: <c>Sm3_per_h×s→Sm3 ÷3600 (s/h) ×1</c>.</summary>
    public string Describe()
    {
        if (!IsConverted)
        {
            return string.Empty;
        }

        var factor = Factor is { } f ? $" ×{f.ToString(CultureInfo.InvariantCulture)}" : " (зі зсувом)";
        return SecondsPerDenominator is { } seconds
            ? $"{FromCode}×s→{ToCode} ÷{seconds.ToString(CultureInfo.InvariantCulture)} (s/{DenominatorCode}){factor}"
            : $"{FromCode}→{ToCode}{factor}";
    }
}
