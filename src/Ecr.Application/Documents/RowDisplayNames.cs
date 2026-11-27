using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Documents;

/// <summary>
/// ✎ integ-row-label-messages (follow-up): назва рядка для відмов сервера — «N» або «N (назва)», де N — номер рядка на
/// екрані (1-based, <b>той самий порядок, що в зрізі таблиці</b>: рядки шаблону за <c>RowDef.Ordinal</c>, далі доданих
/// людиною за <c>TableRow.Id</c> — <c>GetTableSliceHandler</c>, клієнт показує index+1) і підпис рядка шаблону мовою
/// читача. Слова «row» тут немає: шаблони каталогу пишуть «row {rowKey}» / «rows: {rowKeys}» самі.
/// </summary>
/// <remarks>
/// Нічого не питає в бази: викликач передає вже прочитані <c>(ключ → Id)</c> живих рядків екземпляра, тож ціна — нуль
/// запитів понад ті, що в нього є (чи один батчевий, див. <c>IRowLockStore.LiveRowsAsync</c>). Рядок, якого вже немає
/// (чи ще не збережений), у відповідь не потрапляє — <see cref="RowKeyText.Humanize"/> каже про нього «(added by a user)».
/// </remarks>
public static class RowDisplayNames
{
    /// <summary>Назви запитаних рядків; служові GUID-ключі, що є серед живих; короткі коди не чіпаються.</summary>
    /// <param name="liveRows">Живі рядки екземпляра: ключ → <c>TableRow.Id</c>.</param>
    /// <param name="templateRows">Рядки шаблону таблиці (порядок і підписи).</param>
    /// <param name="keys">Ключі з відмови.</param>
    /// <param name="language">Мова читача для підпису.</param>
    public static IReadOnlyDictionary<string, string> Of(
        IEnumerable<KeyValuePair<string, long>> liveRows,
        IEnumerable<RowDef> templateRows,
        IEnumerable<string> keys,
        string? language)
    {
        ArgumentNullException.ThrowIfNull(liveRows);
        ArgumentNullException.ThrowIfNull(templateRows);
        ArgumentNullException.ThrowIfNull(keys);

        var wanted = keys.Where(RowKeyText.ContainsOpaqueKey).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return result;
        }

        var defs = templateRows.Where(r => !r.IsDeleted).GroupBy(r => r.RowKeyValue, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var position = 0;
        foreach (var row in liveRows
                     .OrderBy(r => defs.TryGetValue(r.Key, out var d) ? d.Ordinal : int.MaxValue)
                     .ThenBy(r => r.Value))
        {
            position++;
            if (!wanted.Contains(row.Key))
            {
                continue;
            }

            var label = defs.TryGetValue(row.Key, out var def) ? def.LabelL10n.Get(language ?? "en") : null;
            var number = position.ToString(CultureInfo.InvariantCulture);
            result[row.Key] = string.IsNullOrWhiteSpace(label) ? number : $"{number} ({label})";
        }

        return result;
    }

    /// <summary>Подробиці відмови з доданою подробицею <c>rowNames</c>; нічого називати — ті самі подробиці.</summary>
    /// <param name="details">Подробиці виробника.</param>
    /// <param name="names">Результат <see cref="Of"/>.</param>
    public static IReadOnlyDictionary<string, object?> Merge(
        IReadOnlyDictionary<string, object?>? details, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var source = details ?? new Dictionary<string, object?>();
        if (names.Count == 0)
        {
            return source;
        }

        return new Dictionary<string, object?>(source, StringComparer.Ordinal)
        {
            [RowKeyText.RowNamesParameter] = RowKeyText.EncodeNames(names),
        };
    }

    /// <summary>
    /// Та сама відмова з подробицею <c>rowNames</c> (ключ → «N (назва)»): шлях тексту (<see cref="RowKeyText.Humanize"/>)
    /// підставить її замість службового ключа. Нічого називати — повертає відмову без змін.
    /// </summary>
    /// <param name="refusal">Відмова виробника повідомлення.</param>
    /// <param name="names">Результат <see cref="Of"/>.</param>
    public static BusinessRuleException WithNames(BusinessRuleException refusal, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        ArgumentNullException.ThrowIfNull(names);

        if (names.Count == 0)
        {
            return refusal;
        }

        return new BusinessRuleException(refusal.ErrorCode, refusal.Message, Merge(refusal.Details, names));
    }
}
