namespace Ecr.Domain.ValueObjects;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): назва аркуша в документі — «&lt;Назва&gt; N».
/// </summary>
/// <remarks>
/// ⛔ Одне правило для відповіді API, Excel і звітних в'юх: поки в групі лише оригінал — «&lt;Назва&gt;» без номера;
/// щойно є копії — оригінал «&lt;Назва&gt; 1», копії «&lt;Назва&gt; 2…». Перейменування групи (<c>NameOverrideL10n</c> на
/// оригіналі) міняє «&lt;Назва&gt;» в усіх. Мова без перекладу в назві документа береться з шаблону.
/// </remarks>
public static class SheetCopyNaming
{
    /// <summary>Назва групи: назва документа, а де її немає (для мови) — назва аркуша шаблону.</summary>
    /// <param name="templateName">Назва аркуша з шаблону.</param>
    /// <param name="nameOverride">Назва групи, задана в документі; <c>null</c> — немає.</param>
    public static LocalizedText Effective(LocalizedText templateName, LocalizedText? nameOverride)
    {
        ArgumentNullException.ThrowIfNull(templateName);

        if (nameOverride is null || nameOverride.Values.Count == 0)
        {
            return templateName;
        }

        var merged = new Dictionary<string, string>(templateName.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var (language, text) in nameOverride.Values)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                merged[language] = text;
            }
        }

        return new LocalizedText(merged);
    }

    /// <summary>Показувана назва одного аркуша групи.</summary>
    /// <param name="groupName">Назва групи (<see cref="Effective"/>).</param>
    /// <param name="ordinal">Номер у групі: 1 — оригінал.</param>
    /// <param name="groupHasCopies">Чи є в групі копії.</param>
    public static LocalizedText Display(LocalizedText groupName, short ordinal, bool groupHasCopies)
    {
        ArgumentNullException.ThrowIfNull(groupName);

        return groupHasCopies
            ? new LocalizedText(groupName.Values.ToDictionary(
                pair => pair.Key, pair => Display(pair.Value, ordinal, groupHasCopies: true), StringComparer.OrdinalIgnoreCase))
            : groupName;
    }

    /// <summary>Роздільник коду аркуша і Id копії в ключі <c>SheetStates</c> (код шаблону його не містить).</summary>
    public const char StateKeySeparator = '#';

    /// <summary>
    /// Ключ стану аркуша в <c>DocumentSummary.SheetStates</c>: <c>КОД</c> для оригінала, <c>КОД#sheetCopyId</c> для копії.
    /// </summary>
    /// <param name="sheetCode">Код аркуша шаблону.</param>
    /// <param name="sheetCopyId">Копія; <c>null</c> — оригінал.</param>
    public static string StateKey(string sheetCode, long? sheetCopyId)
        => sheetCopyId is { } id
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{sheetCode}{StateKeySeparator}{id}")
            : sheetCode;

    /// <summary>Код аркуша шаблону з ключа стану (без <c>#sheetCopyId</c>).</summary>
    /// <param name="stateKey">Ключ з <see cref="StateKey"/>.</param>
    public static string CodeOfStateKey(string stateKey)
    {
        ArgumentNullException.ThrowIfNull(stateKey);
        var at = stateKey.IndexOf(StateKeySeparator, StringComparison.Ordinal);

        return at < 0 ? stateKey : stateKey[..at];
    }

    /// <summary>Показувана назва одного аркуша групи — один рядок.</summary>
    /// <param name="groupName">Назва групи.</param>
    /// <param name="ordinal">Номер у групі: 1 — оригінал.</param>
    /// <param name="groupHasCopies">Чи є в групі копії.</param>
    public static string Display(string groupName, short ordinal, bool groupHasCopies)
        => groupHasCopies
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{groupName} {ordinal}")
            : groupName;
}
