// src/Ecr.Domain/Entities/Calculations/RowActivityFilterSpec.cs
using System.Text.RegularExpressions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Фільтр активності рядків методології (D-425): «рахувати лише рядки, статус яких — запис X довідника Y».
/// </summary>
/// <remarks>
/// ⛔ Opt-in на контейнері <c>calc.Methodology</c> (<see cref="Methodology.RowActivityFilter"/>), а не умова в
/// правилах: легасі-фільтр <c>Land_Status = 'Running'</c> жив у коді матчера, а не в правилах AF, тому правило
/// імпортованої методології його не містить. Значення — пара «код довідника : код запису» (не Id: Id запису
/// залежить від бази). Колонки статусу рушій знаходить за довідником (колонки з <c>LookupRegistryDefId</c> цього
/// довідника), а не за кодом колонки: у Land вона то <c>Land_Status</c>, то <c>Status</c>.
/// </remarks>
/// <param name="RegistryCode">Код довідника статусів.</param>
/// <param name="EntryCode">Код запису, який вважається «активним».</param>
public sealed partial record RowActivityFilterSpec(string RegistryCode, string EntryCode)
{
    /// <summary>Максимальна довжина серіалізованого значення (колонка <c>nvarchar(200)</c>).</summary>
    public const int MaxLength = 200;

    /// <summary>Каноничний текст <c>КОД_ДОВІДНИКА:КОД_ЗАПИСУ</c>.</summary>
    public string Value => $"{RegistryCode}:{EntryCode}";

    /// <summary>Розбирає значення; <c>null</c> — формат не той.</summary>
    /// <param name="value">Текст <c>КОД_ДОВІДНИКА:КОД_ЗАПИСУ</c>.</param>
    public static RowActivityFilterSpec? TryParse(string? value)
    {
        if (value is null || value.Length > MaxLength)
        {
            return null;
        }

        var match = Shape().Match(value);

        return match.Success ? new RowActivityFilterSpec(match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    /// <summary>
    /// Чи проходить рядок фільтр: УСІ колонки статусу рядка мусять мати значення, рівне активному запису.
    /// </summary>
    /// <param name="statusTexts">Значення колонок статусу рядка (текст <c>ValueRegistryEntryId</c>); <c>null</c> — немає комірки.</param>
    /// <param name="activeEntryIdText">Id активного запису текстом (так, як його бачить предикат правила).</param>
    /// <returns>
    /// <c>false</c> для порожнього статусу: у легасі рядок без статусу не рахується ніде
    /// (SQL type1 <c>= 'Running'</c>; type2 відсікає <c>ECW_C12_02</c>).
    /// </returns>
    public static bool Admits(IReadOnlyList<string?> statusTexts, string activeEntryIdText)
    {
        ArgumentNullException.ThrowIfNull(statusTexts);

        return statusTexts.Count > 0
               && statusTexts.All(t => t is not null && string.Equals(t, activeEntryIdText, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^([A-Za-z0-9_]{1,64}):([A-Za-z0-9_\-]{1,64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
