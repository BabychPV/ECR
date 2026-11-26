// src/Ecr.Application/GasComposition/GasCompositionValidation.cs
using System.Globalization;
using Ecr.Domain.Errors;

namespace Ecr.Application.GasComposition;

/// <summary>Компонент складу у запиті: код і мол.%.</summary>
/// <param name="Code">Код компонента (<c>COMPONENT</c>).</param>
/// <param name="MolPct">Мол.%, 0…100.</param>
public sealed record GasComponentValue(string Code, decimal MolPct);

/// <summary>Значення версії складу, які вводить людина.</summary>
/// <param name="TemperatureC">Температура, °C; <c>null</c> — невідома.</param>
/// <param name="DensityOp">Робоча густина (HYSYS); <c>null</c> — невідома.</param>
/// <param name="MwHysys">Молярна маса (HYSYS); <c>null</c> — невідома.</param>
/// <param name="Z">Коефіцієнт стисливості; <c>null</c> — невідомий.</param>
/// <param name="Components">Компоненти; нульові ігноруються (нуль = рядка немає).</param>
/// <param name="AcceptSumDeviation">Людина підтвердила, що Σ поза 100 ± 0.5 (або складу немає) — так і має бути.</param>
public sealed record GasVersionValues(
    decimal? TemperatureC,
    decimal? DensityOp,
    decimal? MwHysys,
    decimal? Z,
    IReadOnlyList<GasComponentValue> Components,
    bool AcceptSumDeviation);

/// <summary>Перевірені значення: ненульові компоненти за кодом і Σ.</summary>
/// <param name="Components">Ненульові компоненти, код → мол.% (порядок запиту).</param>
/// <param name="Sum">Σ мол.%.</param>
/// <param name="NoComposition">Складу немає (Σ = 0).</param>
public sealed record GasValidatedValues(IReadOnlyDictionary<string, decimal> Components, decimal Sum, bool NoComposition);

/// <summary>
/// Перевірка значень версії (D-NEW): FLERT не перевіряв нічого; тут — діапазони, дублі, відомі компоненти і Σ = 100 ± 0.5
/// або явне підтвердження.
/// </summary>
/// <remarks>
/// ⚠ Σ поза допуском — НЕ безумовна помилка: у FLERT 18 версій з Σ ≠ 100 і 16 без складу, вони мусять лишатися
/// редагованими (температура, дата). Тому відмова лише без підтвердження <see cref="GasVersionValues.AcceptSumDeviation"/>.
/// </remarks>
public static class GasCompositionValidation
{
    /// <summary>Ключ: мол.% поза 0…100.</summary>
    public const string ComponentRangeKey = "err.ECR-REG-0422.gasCompositionComponentRange";

    /// <summary>Ключ: компонент повторюється.</summary>
    public const string ComponentDuplicateKey = "err.ECR-REG-0422.gasCompositionComponentDuplicate";

    /// <summary>Ключ: невідомий компонент.</summary>
    public const string ComponentUnknownKey = "err.ECR-REG-0422.gasCompositionComponentUnknown";

    /// <summary>Ключ: Σ поза допуском і не підтверджено.</summary>
    public const string SumDeviationKey = "err.ECR-REG-0422.gasCompositionSumDeviation";

    /// <summary>Ключ: властивість поза діапазоном (температура нижче абсолютного нуля, від'ємні ρ/MW/Z).</summary>
    public const string PropertyRangeKey = "err.ECR-REG-0422.gasCompositionPropertyRange";

    private const decimal AbsoluteZeroC = -273.15m;

    /// <summary>Перевіряє значення.</summary>
    /// <param name="values">Значення.</param>
    /// <param name="knownComponents">Коди компонентів довідника <c>COMPONENT</c>.</param>
    /// <exception cref="Errors.BusinessRuleException"><c>ECR-REG-0422</c> з ключем причини.</exception>
    public static GasValidatedValues Validate(GasVersionValues values, IReadOnlySet<string> knownComponents)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(knownComponents);

        if (values.TemperatureC is { } t && t <= AbsoluteZeroC)
        {
            throw Invalid(PropertyRangeKey, "Температура нижче абсолютного нуля.", ("field", "T_C"), ("value", Text(t)));
        }

        foreach (var (field, value) in new[] { ("DENSITY_OP", values.DensityOp), ("MW_HYSYS", values.MwHysys), ("Z", values.Z) })
        {
            if (value is { } v && v < 0m)
            {
                throw Invalid(PropertyRangeKey, $"«{field}» не може бути від'ємним.", ("field", field), ("value", Text(v)));
            }
        }

        var components = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in values.Components ?? [])
        {
            var code = component.Code?.Trim() ?? string.Empty;
            if (!knownComponents.Contains(code))
            {
                throw Invalid(ComponentUnknownKey, $"Компонента «{code}» немає в довіднику компонентів.", ("component", code));
            }

            if (!seen.Add(code))
            {
                throw Invalid(ComponentDuplicateKey, $"Компонент «{code}» указано двічі.", ("component", code));
            }

            if (component.MolPct is < 0m or > 100m)
            {
                throw Invalid(ComponentRangeKey, $"Мол.% компонента «{code}» мусить бути від 0 до 100.",
                    ("component", code), ("value", Text(component.MolPct)));
            }

            if (component.MolPct != 0m)
            {
                components[CanonicalCode(knownComponents, code)] = component.MolPct;
            }
        }

        var sum = components.Values.Sum();
        var noComposition = components.Count == 0;
        if ((noComposition || Math.Abs(sum - 100m) > GasCompositionRules.SumTolerance) && !values.AcceptSumDeviation)
        {
            throw Invalid(SumDeviationKey,
                $"Σ мол.% = {Text(sum)} — поза 100 ± {Text(GasCompositionRules.SumTolerance)}. Виправте склад або підтвердьте збереження.",
                ("sum", Text(sum)));
        }

        return new GasValidatedValues(components, sum, noComposition);
    }

    /// <summary>Код у тому написанні, яке має довідник (коди без урахування регістру).</summary>
    private static string CanonicalCode(IReadOnlySet<string> known, string code)
        => known.FirstOrDefault(k => string.Equals(k, code, StringComparison.Ordinal))
           ?? known.First(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase));

    private static string Text(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    private static Errors.BusinessRuleException Invalid(string messageKey, string message, params (string Key, string Value)[] parameters)
        => GasCompositionRules.Rule(ErrorCodes.RegistrySwitchInOpenPeriod, messageKey, message, parameters);
}
