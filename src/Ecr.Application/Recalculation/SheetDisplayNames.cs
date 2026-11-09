using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Functions;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Значення <c>SHEETNAME()</c> для прогону перерахунку: показувана назва кожного аркуша «виду» (оригінал чи копія).
/// </summary>
/// <remarks>
/// ⛔ Одне правило з API, Excel і звітними в'юхами (<see cref="SheetCopyNaming"/>): назва групи (у документі, інакше з
/// шаблону), з номером «N» лише коли в групі є копії. Мова — <c>en</c>, інакше код аркуша — рівно як
/// <c>_SheetDisplayName</c> у <c>rpt.*</c> (05-rpt-views.sql): комірка має одне значення, а не по мові, і звіт по ній
/// має збігатися зі службовою колонкою.
/// </remarks>
public static class SheetDisplayNames
{
    /// <summary>Мова значення <c>SHEETNAME()</c> (та сама, що в <c>rpt.*._SheetDisplayName</c>).</summary>
    public const string Language = "en";

    /// <summary>Назва групи аркуша: назва в документі, інакше з шаблону, мовою <see cref="Language"/>; інакше код.</summary>
    /// <param name="sheet">Аркуш шаблону.</param>
    /// <param name="nameOverride">Назва групи, задана в документі; <c>null</c> — немає.</param>
    public static string BaseName(SheetDef sheet, LocalizedText? nameOverride = null)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var effective = SheetCopyNaming.Effective(sheet.NameL10n ?? new LocalizedText(), nameOverride);
        return effective.Values.TryGetValue(Language, out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : sheet.Code;
    }

    /// <summary>Назви аркушів «виду» прогону: аркуш шаблону → показувана назва.</summary>
    /// <param name="snapshot">Структура версії шаблону.</param>
    /// <param name="groups">Групи аркушів документа (<see cref="IDocumentSheetCopyStore.ListGroupsAsync"/>).</param>
    /// <param name="copyId">Копія, яку рахує прогін; <c>null</c> — прогін оригіналів.</param>
    /// <remarks>
    /// Прогін копії пише лише в таблиці СВОГО аркуша (<c>CopyScope</c>), тож лише цей аркуш отримує номер копії; решта
    /// аркушів виду — оригінали («&lt;Назва&gt; 1», якщо в їхній групі є копії).
    /// </remarks>
    public static IReadOnlyDictionary<int, string> For(
        TemplateVersionSnapshot snapshot, IReadOnlyList<SheetCopyGroupInfo> groups, long? copyId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(groups);

        var bySheet = groups.GroupBy(g => g.SheetDefId).ToDictionary(g => g.Key, g => g.First());
        var names = new Dictionary<int, string>();

        foreach (var sheet in snapshot.Sheets)
        {
            bySheet.TryGetValue(sheet.Id, out var group);
            var baseName = BaseName(sheet, group?.NameOverride);
            var hasCopies = group is { Copies.Count: > 0 };

            var ordinal = copyId is { } id && group?.Copies.FirstOrDefault(c => c.SheetCopyId == id) is { } copy
                ? copy.Ordinal
                : Domain.Entities.Documents.DocumentSheet.OriginalOrdinal;

            names[sheet.Id] = SheetCopyNaming.Display(baseName, ordinal, hasCopies);
        }

        return names;
    }

    /// <summary>
    /// Чи може хоч одна з формул читати назву аркуша — дешево, за текстом виразу (без розбору).
    /// </summary>
    /// <param name="expressions">Тексти виразів формул.</param>
    /// <remarks>
    /// ⚠ Хибнопозитивне (ім'я в рядковому літералі) коштує одне зайве читання назв; хибнонегативного не буває — без
    /// цього слова в тексті виклику функції немає. Гейт тримає прогони без <c>SHEETNAME</c> без жодного нового запиту.
    /// </remarks>
    public static bool MayUse(IEnumerable<string?> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);

        return expressions.Any(e => e is not null
                                    && e.Contains(FunctionRegistry.SheetNameFunction, StringComparison.OrdinalIgnoreCase));
    }
}
