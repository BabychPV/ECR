using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Одне поле шапки документа у вивантаженні (A2-12).</summary>
/// <param name="Code">Код поля.</param>
/// <param name="Label">Підпис поля мовою вивантаження; без підпису — код.</param>
/// <param name="Text">Значення текстом в інваріантній культурі; <c>null</c> — поле порожнє.</param>
/// <param name="Value">
/// Типізоване значення для книги (<see cref="decimal"/>, <see cref="bool"/>, <see cref="DateTime"/>
/// або <see cref="string"/>); <c>null</c> — поле порожнє.
/// </param>
/// <param name="IsNumeric">Чи число (CSV не знешкоджує його від формул — так само, як числові колонки).</param>
public sealed record HeaderExportRow(string Code, string Label, string? Text, object? Value, bool IsNumeric);

/// <summary>Підписи блоку шапки у книзі xlsx (ключі каталогу <c>export.header.*</c>).</summary>
/// <param name="Sheet">Назва аркуша шапки.</param>
/// <param name="Field">Заголовок стовпця з підписом поля.</param>
/// <param name="Value">Заголовок стовпця зі значенням.</param>
/// <param name="Code">Заголовок стовпця з кодом поля.</param>
public sealed record HeaderExportLabels(string Sheet, string Field, string Value, string Code)
{
    /// <summary>Ключ назви аркуша.</summary>
    public const string SheetKey = "export.header.sheet";

    /// <summary>Ключ заголовка стовпця «поле».</summary>
    public const string FieldKey = "export.header.field";

    /// <summary>Ключ заголовка стовпця «значення».</summary>
    public const string ValueKey = "export.header.value";

    /// <summary>Ключ заголовка стовпця «код».</summary>
    public const string CodeKey = "export.header.code";

    /// <summary>Запасні підписи, коли каталогу нема (тести, що збирають експортер вручну).</summary>
    public static HeaderExportLabels Fallback { get; } = new("Header", "Field", "Value", "Code");

    /// <summary>Підписи мовою запиту з каталогу; відсутній ключ — запасний підпис.</summary>
    public static async Task<HeaderExportLabels> ResolveAsync(
        IUiStringCatalog? catalog, string? language, CancellationToken ct)
    {
        if (catalog is null)
        {
            return Fallback;
        }

        var strings = (await catalog.GetAsync(string.IsNullOrWhiteSpace(language) ? "en" : language, ct)
            .ConfigureAwait(false)).Strings;

        string Pick(string key, string fallback)
            => strings.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) && text != key ? text : fallback;

        return new HeaderExportLabels(
            Pick(SheetKey, Fallback.Sheet), Pick(FieldKey, Fallback.Field),
            Pick(ValueKey, Fallback.Value), Pick(CodeKey, Fallback.Code));
    }
}

/// <summary>
/// Читає поля шапки документа для вивантажень xlsx / CSV / JSON (A2-12).
/// </summary>
/// <remarks>
/// ⚠ Модель доступу. Окремого рівня доступу до поля шапки в системі немає:
/// <c>ResourceKind</c> знає проєкт, аркуш, таблицю, колонку й довідник, а шапка
/// читається за тим самим правилом, що й сам документ (<c>Document.View</c> +
/// видимість документа в <c>GetDocumentHeaderHandler</c>). Експорт ставить той
/// самий бар'єр: <c>ExportDocumentHandler</c> перевіряє видимість документа
/// ДО постановки задачі, тож поля шапки експортуються рівно тому, хто бачить
/// шапку в інтерфейсі. Маскування на рівні поля (як <c>HiddenColumnIds</c> для
/// колонок) з'явиться тут разом із відповідним рівнем доступу.
/// </remarks>
public static class DocumentHeaderExport
{
    /// <summary>Поля шапки версії шаблону за порядком, з їхніми значеннями документа.</summary>
    /// <param name="headers">Сховище значень; <c>null</c> (тести без порту) — шапки нема.</param>
    /// <param name="registries">Довідники для полів типу підстановки.</param>
    /// <param name="snapshot">Метадані версії шаблону документа.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="language">Мова підписів; <c>null</c> — англійська.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<IReadOnlyList<HeaderExportRow>> ReadAsync(
        IDocumentHeaderStore? headers,
        IRegistryStore registries,
        TemplateVersionSnapshot snapshot,
        long documentId,
        string? language,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(snapshot);

        var fields = snapshot.HeaderFields.Where(f => !f.IsDeleted).OrderBy(f => f.Ordinal).ToList();
        if (headers is null || fields.Count == 0)
        {
            return [];
        }

        var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
        var lang = string.IsNullOrWhiteSpace(language) ? "en" : language;

        var codes = new Dictionary<int, IReadOnlyDictionary<long, string>>();
        foreach (var registryId in fields
                     .Where(f => f.DataType == CellDataType.Lookup && f.LookupRegistryDefId is not null)
                     .Select(f => f.LookupRegistryDefId!.Value)
                     .Distinct())
        {
            var entries = await registries.ListEntriesAsync(registryId, ct).ConfigureAwait(false);
            codes[registryId] = entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().Code);
        }

        var rows = new List<HeaderExportRow>(fields.Count);
        foreach (var field in fields)
        {
            var label = field.LabelL10n.Get(lang);
            var shown = string.IsNullOrWhiteSpace(label) ? field.Code : label;
            var value = values.GetValueOrDefault(field.Id);

            rows.Add(value is null || value.IsEmpty
                ? new HeaderExportRow(field.Code, shown, null, null, IsNumeric: false)
                : Row(field, shown, value, codes));
        }

        return rows;
    }

    private static HeaderExportRow Row(
        HeaderFieldDef field, string label, DocumentHeaderValueData value,
        Dictionary<int, IReadOnlyDictionary<long, string>> codes)
    {
        switch (field.DataType)
        {
            case CellDataType.Int or CellDataType.Decimal when value.ValueNumeric is { } number:
                return new HeaderExportRow(field.Code, label, DocumentDataExporter.Number(number, null), number, true);

            case CellDataType.Bool when value.ValueBool is { } flag:
                return new HeaderExportRow(field.Code, label, flag ? "true" : "false", flag, false);

            case CellDataType.Date when value.ValueDate is { } date:
                // A3: поле шапки типу Date — календарна дата; у CSV/JSON без нульового часу
                // (`2026-10-05`, а не `2026-10-05T00:00:00`), формат інваріантний. Типізоване
                // значення для xlsx лишається DateTime.
                return new HeaderExportRow(
                    field.Code, label, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date, false);

            case CellDataType.Lookup when value.ValueRegistryEntryId is { } entryId:
                var code = field.LookupRegistryDefId is { } registry
                           && codes.TryGetValue(registry, out var entries)
                           && entries.TryGetValue(entryId, out var known)
                    ? known
                    : entryId.ToString(CultureInfo.InvariantCulture);
                return new HeaderExportRow(field.Code, label, code, code, false);

            case CellDataType.Unit when value.ValueUnitId is { } unitId:
                var unit = unitId.ToString(CultureInfo.InvariantCulture);
                return new HeaderExportRow(field.Code, label, unit, unit, false);

            default:
                return value.ValueString is { } text
                    ? new HeaderExportRow(field.Code, label, text, text, false)
                    : new HeaderExportRow(field.Code, label, null, null, false);
        }
    }
}
