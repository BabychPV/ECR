using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Одне поле шапки документа у вивантаженні (A2-12).</summary>
/// <param name="Code">Код поля.</param>
/// <param name="Label">Підпис поля мовою вивантаження; без підпису — код.</param>
/// <param name="Text">
/// Значення текстом в інваріантній культурі (для CSV/JSON; у списку — КОД запису); <c>null</c> — поле порожнє.
/// </param>
/// <param name="Value">
/// Типізоване значення для книги (<see cref="decimal"/>, <see cref="bool"/>, <see cref="DateTime"/>
/// або <see cref="string"/>; у списку — НАЗВА запису мовою вивантаження, аудит 2026-11 X-3); <c>null</c> — поле порожнє.
/// </param>
/// <param name="IsNumeric">Чи число (CSV не знешкоджує його від формул — так само, як числові колонки).</param>
/// <param name="ContractKey">
/// X-5 (D-477): ключ секції «Contract» (<see cref="HeaderSheetLayout"/>); <c>null</c> — поле поза секцією.
/// </param>
public sealed record HeaderExportRow(
    string Code, string Label, string? Text, object? Value, bool IsNumeric, string? ContractKey = null);

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

    /// <summary>X-4: ключ підпису рядка File Number (той самий, що в картці документа).</summary>
    public const string FileNumberKey = "document.header.contract.fileNumber";

    /// <summary>X-4: ключ підпису рядка версії шаблону (той самий, що в картці документа).</summary>
    public const string VersionKey = "document.header.contract.version";

    /// <summary>X-4: ключ підпису рядка звітного періоду вивантаження.</summary>
    public const string PeriodLabelKey = "export.header.period";

    /// <summary>X-4: підпис рядка File Number.</summary>
    public string FileNumber { get; init; } = "File Number";

    /// <summary>X-4: підпис рядка версії шаблону.</summary>
    public string Version { get; init; } = "Version";

    /// <summary>X-4: підпис рядка звітного періоду.</summary>
    public string Period { get; init; } = "Reporting period";

    /// <summary>Запасні підписи, коли каталогу нема (тести, що збирають експортер вручну).</summary>
    /// <remarks>✎ D-477 (X-5): назва вкладки — «Document header», як заголовок панелі шапки в UI.</remarks>
    public static HeaderExportLabels Fallback { get; } = new("Document header", "Field", "Value", "Code");

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
            Pick(ValueKey, Fallback.Value), Pick(CodeKey, Fallback.Code))
        {
            FileNumber = Pick(FileNumberKey, Fallback.FileNumber),
            Version = Pick(VersionKey, Fallback.Version),
            Period = Pick(PeriodLabelKey, Fallback.Period),
        };
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
    /// <param name="groups">Резолвер груп пермітів: поле-група друкує «номер — тип» (PRMG); <c>null</c> - груп немає.</param>
    /// <param name="contractLayout">
    /// X-5 (D-477): для книги — підпис поля секції Contract «EN — RU» і ключ секції (<see cref="HeaderSheetLayout"/>);
    /// CSV/JSON кличуть без нього й не змінюються.
    /// </param>
    public static async Task<IReadOnlyList<HeaderExportRow>> ReadAsync(
        IDocumentHeaderStore? headers,
        IRegistryStore registries,
        TemplateVersionSnapshot snapshot,
        long documentId,
        string? language,
        CancellationToken ct,
        Permits.Groups.IPermitGroupResolver? groups = null,
        bool contractLayout = false)
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
        var names = new Dictionary<int, IReadOnlyDictionary<long, string>>();
        foreach (var registryId in fields
                     .Where(f => f.DataType == CellDataType.Lookup && f.LookupRegistryDefId is not null)
                     .Select(f => f.LookupRegistryDefId!.Value)
                     .Distinct())
        {
            // Permits PRMG: ціль-група - записи реєстрів-членів, підпис «номер — тип»; решта - код запису.
            var entries = await Permits.Groups.LookupTargetEntries.ListAsync(registries, groups, registryId, ct).ConfigureAwait(false);
            codes[registryId] = entries.GroupBy(e => e.Entry.Id)
                .ToDictionary(g => g.Key, g => Permits.Groups.LookupTargetEntries.Label(g.First().Entry.Code, g.First().TypeCode));

            // X-3: людині в книзі — НАЗВА запису (як у картці документа); група дозволів — «номер — тип», як і була.
            // ✎ Приймання F13 (P3, L24): у книзі — двомовна назва «EN - RU», як стовпець NAME довідників Land.
            names[registryId] = entries.GroupBy(e => e.Entry.Id)
                .ToDictionary(g => g.Key, g => DisplayOf(g.First(), lang, bilingual: contractLayout));
        }

        var rows = new List<HeaderExportRow>(fields.Count);
        foreach (var field in fields)
        {
            var label = field.LabelL10n.Get(lang);
            var shown = string.IsNullOrWhiteSpace(label) ? field.Code : label;
            var value = values.GetValueOrDefault(field.Id);

            // X-5: для книги — підпис «EN — RU» і ключ секції Contract, як у картці документа. CSV/JSON — без змін.
            string? contractKey = null;
            if (contractLayout)
            {
                contractKey = HeaderSheetLayout.ContractKeyOf(field.Code, field.LabelL10n.Values.GetValueOrDefault("en"));
                if (contractKey is not null)
                {
                    shown = HeaderSheetLayout.BilingualLabel(field.LabelL10n, shown);
                }
            }

            rows.Add((value is null || value.IsEmpty
                ? new HeaderExportRow(field.Code, shown, null, null, IsNumeric: false)
                : Row(field, shown, value, codes, names)) with { ContractKey = contractKey });
        }

        return rows;
    }

    /// <summary>
    /// Назва запису мовою вивантаження; порожня — код. Член групи дозволів — «номер — тип» (X-3). Для книги
    /// (<paramref name="bilingual"/>) — «EN - RU», коли запис має обидві назви (<see cref="HeaderSheetLayout.BilingualValue"/>).
    /// </summary>
    private static string DisplayOf(Permits.Groups.LookupTargetEntry entry, string language, bool bilingual)
    {
        if (entry.TypeCode is not null)
        {
            return Permits.Groups.LookupTargetEntries.Label(entry.Entry.Code, entry.TypeCode);
        }

        var display = entry.Entry.DisplayL10n?.Get(language);
        var shown = string.IsNullOrWhiteSpace(display) ? entry.Entry.Code : display;
        return bilingual && entry.Entry.DisplayL10n is { } names ? HeaderSheetLayout.BilingualValue(names, shown) : shown;
    }

    private static HeaderExportRow Row(
        HeaderFieldDef field, string label, DocumentHeaderValueData value,
        Dictionary<int, IReadOnlyDictionary<long, string>> codes,
        Dictionary<int, IReadOnlyDictionary<long, string>> names)
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
                var name = field.LookupRegistryDefId is { } named
                           && names.TryGetValue(named, out var byId)
                           && byId.TryGetValue(entryId, out var display)
                    ? display
                    : code;
                return new HeaderExportRow(field.Code, label, code, name, false);

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
