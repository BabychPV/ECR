// src/Ecr.Application/Registries/Export/ExportRegistryHandler.cs
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Rows;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Registries.Export;

/// <summary>Готовий файл експорту довідника.</summary>
/// <param name="FileName">Ім'я файлу для <c>Content-Disposition</c>.</param>
/// <param name="ContentType">Тип вмісту.</param>
/// <param name="Content">Вміст з початку. ⚠ Закриває викликач (для книги це й видаляє тимчасовий файл).</param>
/// <param name="Rows">Записів у файлі.</param>
public sealed record RegistryExportFile(string FileName, string ContentType, Stream Content, int Rows);

/// <summary>
/// Експорт записів довідника в CSV або XLSX (RT-16, FEATURE-REGISTRY-TABLES §7.1). Право
/// <c>Registry.View</c> або грант <c>Read</c> на довідник.
/// </summary>
/// <remarks>
/// ⛔ Записи — ТІ САМІ, що бачить сітка на <c>asOf</c>: відбір робить <see cref="GetRegistryRowsHandler"/>
/// (видимість пікера, частини композиції лише з видимим батьком). Друге правило відбору для файлу
/// розійшлося б із екраном.
/// <para>
/// ⛔ Посилання — КОДАМИ, а не Id: <c>Lookup</c> — код запису-цілі, поле <c>Unit</c> — код одиниці. Id
/// нічого не каже людині й не переноситься між базами.
/// </para>
/// <para>
/// ⚠ CSV — рівно те, що приймає імпорт (<c>POST …/entries/import</c>): <c>code</c> і коди полів, числа
/// інваріантно без втрати знаків, дати <c>yyyy-MM-dd</c>. Експорт → імпорт того самого файлу нічого не
/// змінює. Книга XLSX — для людини: додатково назва й вікно чинності (<c>@name</c>, <c>@validFrom</c>,
/// <c>@validTo</c>; <c>@</c> не буває в коді поля).
/// </para>
/// <para>
/// ⚠ <c>includeChildren</c> (ФВ-8.16): частини композиції їдуть ОКРЕМИМИ таблицями, а не колонками
/// батька — інакше файл не імпортувався б назад. Кожна таблиця — той самий CSV, що експорт дочірнього
/// довідника окремо; поле композиції несе КОД батька, тож частина після імпорту батька розв'язується
/// на той самий запис.
/// </para>
/// <para>
/// Подія <see cref="ExportedEventType"/> — до віддачі файлу, незалежна від транзакції (C4): дані
/// пішли, навіть якщо завантаження обірвалося.
/// </para>
/// </remarks>
public sealed class ExportRegistryHandler(
    IRegistryStore registries,
    GetRegistryRowsHandler rows,
    IRegistryKeyStore keys,
    IRegistryWorkbookWriter workbooks,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Стеля записів, коли конфіг (<c>Registries:ExportMaxRows</c>) не задає іншої.</summary>
    public const int DefaultExportMaxRows = 50_000;

    /// <summary>Тип події журналу безпеки.</summary>
    public const string ExportedEventType = "RegistryExported";

    /// <summary>Формат CSV.</summary>
    public const string Csv = "csv";

    /// <summary>Формат XLSX.</summary>
    public const string Xlsx = "xlsx";

    /// <summary>Тип вмісту CSV.</summary>
    public const string CsvContentType = "text/csv";

    /// <summary>Тип вмісту архіву CSV з частинами композиції (<c>includeChildren</c>).</summary>
    public const string ZipContentType = "application/zip";

    /// <summary>Тип вмісту XLSX.</summary>
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Колонка коду запису — та сама, яку шукає імпорт CSV.</summary>
    public const string CodeColumn = "code";

    /// <summary>Службові колонки книги XLSX.</summary>
    public const string NameColumn = "@name";

    /// <summary>Перший чинний день.</summary>
    public const string ValidFromColumn = "@validFrom";

    /// <summary>Перший НЕчинний день.</summary>
    public const string ValidToColumn = "@validTo";

    /// <summary>Записів на одне читання значень — стеля <see cref="IRegistryRowsQuery.ReadRowsAsync"/>.</summary>
    private const int Chunk = 500;

    /// <summary>Будує файл експорту.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="format"><c>csv</c> або <c>xlsx</c> (без регістру).</param>
    /// <param name="asOf">Бізнес-дата чинності; <c>null</c> — сьогодні (UTC).</param>
    /// <param name="maxRows">Стеля записів.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-REG-0404</c>: довідника немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-REQ-0422</c>: невідомий формат (<c>registryExportFormatUnknown</c>) або записів понад
    /// стелю (<c>registryExportTooLarge</c>).
    /// </exception>
    public Task<RegistryExportFile> HandleAsync(
        string registryCode, string? format, DateOnly? asOf, int maxRows, CancellationToken ct)
        => HandleAsync(registryCode, format, asOf, includeChildren: false, maxRows, ct);

    /// <summary>Будує файл експорту; з <paramref name="includeChildren"/> — разом із частинами композиції.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="format"><c>csv</c> або <c>xlsx</c> (без регістру).</param>
    /// <param name="asOf">Бізнес-дата чинності; <c>null</c> — сьогодні (UTC).</param>
    /// <param name="includeChildren">
    /// Додати дочірні довідники композиції (ФВ-8.16), рекурсивно: CSV — архів ZIP із файлом на
    /// довідник, XLSX — аркуш на довідник. Батько завжди перший — у порядку імпорту.
    /// </param>
    /// <param name="maxRows">Стеля записів — на ВСІ довідники файлу разом.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-REG-0404</c>: довідника немає.</exception>
    /// <exception cref="AccessDeniedException">Немає читання дочірнього довідника (грант чи заборона — однаково).</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-REQ-0422</c>: невідомий формат (<c>registryExportFormatUnknown</c>) або записів понад
    /// стелю (<c>registryExportTooLarge</c>).
    /// </exception>
    public async Task<RegistryExportFile> HandleAsync(
        string registryCode, string? format, DateOnly? asOf, bool includeChildren, int maxRows, CancellationToken ct)
    {
        var profile = await RegistryAccess
            .RequireAsync(access, currentUser, Permission, GrantLevel.Read, new RegistryLookup(registries, registryCode), ct)
            .ConfigureAwait(false);

        var kind = (format ?? Csv).Trim().ToLowerInvariant();
        if (kind is not (Csv or Xlsx))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Формату експорту довідника «{format}» немає: csv або xlsx.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.registryExportFormatUnknown",
                    ["format"] = format,
                });
        }

        var date = asOf ?? DateOnly.FromDateTime(clock.UtcNow);
        var root = await rows.SelectAsync(Request(registryCode, date), ct).ConfigureAwait(false);
        var selected = new List<(RegistryDef Definition, List<RegistryEntry> Ordered)> { root };

        if (includeChildren)
        {
            foreach (var child in await ChildrenAsync(root.Definition, ct).ConfigureAwait(false))
            {
                // ⛔ Читання КОЖНОГО дочірнього — те саме правило, що й для батька: грант на батька
                // не відкриває його частин, а заборона на частину не обходиться експортом батька.
                // ⛔ S18: відмова одна — `403 permission` без ідентифікатора частини — і для гранта, і для
                // заборони. Доти заборонена частина давала `404 registryId` з її `registryDefId`:
                // відповідь про довідник, якого людина не називала й не бачить.
                if (!RegistryAccess.CanAccess(profile, Permission, GrantLevel.Read, child.Id))
                {
                    throw RegistryAccess.Denied(Permission);
                }

                // Частину видно рівно тоді, коли видно батька (D-155): усі видимі частини на asOf — це
                // рівно частини записів батька, які вже лягли у файл.
                selected.Add(await rows.SelectAsync(Request(child.Code, date), ct).ConfigureAwait(false));
            }
        }

        // Стеля — ДО читання значень і до файлу: 422, а не обрізаний файл.
        var total = selected.Sum(s => s.Ordered.Count);
        if (total > maxRows)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"У довіднику «{root.Definition.Code}» на {date:yyyy-MM-dd} записів {total}, а стеля експорту — {maxRows}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.registryExportTooLarge",
                    ["registryCode"] = root.Definition.Code,
                    ["total"] = total.ToString(CultureInfo.InvariantCulture),
                    ["max"] = maxRows.ToString(CultureInfo.InvariantCulture),
                });
        }

        var xlsx = kind == Xlsx;
        var sheets = new List<(RegistryDef Definition, List<RegistryFieldDef> Fields, List<IReadOnlyList<string?>> Table)>();
        foreach (var (definition, ordered) in selected)
        {
            var fields = definition.Fields.OrderBy(f => f.Ordinal).ThenBy(f => f.Id).ToList();
            sheets.Add((definition, fields, await TableAsync(definition, fields, ordered, xlsx, ct).ConfigureAwait(false)));
        }

        // ⛔ C4: подія-СПРОБА (дані покидають систему), а не результат зміни.
        await audit.WriteIndependentSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                ExportedEventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new
                {
                    registryDefId = root.Definition.Id,
                    registryCode = root.Definition.Code,
                    format = kind,
                    asOf = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    rows = total,
                    includeChildren,
                    registries = sheets.Select(s => new { code = s.Definition.Code, rows = s.Table.Count }).ToList(),
                }),
                currentUser.UserId ?? 0,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        var baseName = $"registry-{root.Definition.Code}-{date:yyyyMMdd}";
        if (!xlsx)
        {
            if (!includeChildren)
            {
                var (_, fields, table) = sheets[0];
                return new RegistryExportFile($"{baseName}.csv", CsvContentType, ToCsv(fields, table), total);
            }

            return new RegistryExportFile($"{baseName}.zip", ZipContentType, ToZip(sheets), total);
        }

        var book = await workbooks
            .WriteAsync([.. sheets.Select(s => Workbook(s.Definition, s.Fields, s.Table))], ct)
            .ConfigureAwait(false);
        return new RegistryExportFile($"{baseName}.xlsx", XlsxContentType, book, total);
    }

    /// <summary>
    /// Дочірні довідники композиції, рекурсивно, у порядку імпорту: батько раніше за свої частини,
    /// сусіди — за кодом. Цикл у даних (його не пускає опис) зупиняє обхід.
    /// </summary>
    private async Task<List<RegistryDef>> ChildrenAsync(RegistryDef root, CancellationToken ct)
    {
        var all = await registries.ListDefinitionsAsync(ct).ConfigureAwait(false);
        var byParent = all
            .Select(d => (Child: d, Field: d.Fields.FirstOrDefault(f =>
                f.RelationKind == RegistryRelationKind.Composition && f.RefRegistryDefId is not null)))
            .Where(x => x.Field is not null)
            .ToLookup(x => x.Field!.RefRegistryDefId!.Value, x => x.Child);

        var result = new List<RegistryDef>();
        var seen = new HashSet<int> { root.Id };
        void Walk(int parentId)
        {
            foreach (var child in byParent[parentId].OrderBy(d => d.Code, StringComparer.Ordinal))
            {
                if (seen.Add(child.Id))
                {
                    result.Add(child);
                    Walk(child.Id);
                }
            }
        }

        Walk(root.Id);
        return result;
    }

    /// <summary>Запит відбору: той самий, що в сітки на <c>asOf</c>, без пошуку й фільтрів.</summary>
    private static RegistryRowsRequest Request(string registryCode, DateOnly date)
        => new(registryCode, date, AsOfUtc: null, ParentEntryId: null, Search: null,
            new Dictionary<string, string>(), new CursorRequest(Chunk));

    /// <summary>Рядки файлу одного довідника: <c>code</c>, (для книги — службові колонки), поля.</summary>
    private async Task<List<IReadOnlyList<string?>>> TableAsync(
        RegistryDef definition, List<RegistryFieldDef> fields, List<RegistryEntry> ordered, bool xlsx, CancellationToken ct)
    {
        var table = new List<IReadOnlyList<string?>>(ordered.Count);

        foreach (var chunk in ordered.Chunk(Chunk))
        {
            var page = await rows.ProjectAsync(definition, chunk, asOfUtc: null, ct).ConfigureAwait(false);
            var targets = page
                .SelectMany(r => fields.Where(f => f.DataType == CellDataType.Lookup)
                    .Select(f => r.Values.GetValueOrDefault(f.Code)?.Value))
                .Select(v => long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (long?)null)
                .OfType<long>()
                .Distinct()
                .ToList();
            var codes = targets.Count == 0
                ? new Dictionary<long, string>()
                : await keys.FindEntryCodesAsync(targets, ct).ConfigureAwait(false);

            foreach (var row in page)
            {
                var line = new List<string?>(fields.Count + 4) { row.Code };
                if (xlsx)
                {
                    line.Add(row.Display);
                    line.Add(Iso(row.ValidFrom));
                    line.Add(Iso(row.ValidTo));
                }

                line.AddRange(fields.Select(f => Cell(f, row.Values.GetValueOrDefault(f.Code), codes)));
                table.Add(line);
            }
        }

        return table;
    }

    /// <summary>Аркуш книги одного довідника.</summary>
    private static RegistryWorkbook Workbook(
        RegistryDef definition, List<RegistryFieldDef> fields, List<IReadOnlyList<string?>> table)
    {
        var columns = new List<RegistryWorkbookColumn>
        {
            new(CodeColumn, CellDataType.String),
            new(NameColumn, CellDataType.String),
            new(ValidFromColumn, CellDataType.Date),
            new(ValidToColumn, CellDataType.Date),
        };
        columns.AddRange(fields.Select(f => new RegistryWorkbookColumn(
            f.Code, f.DataType is CellDataType.Lookup or CellDataType.Unit ? CellDataType.String : f.DataType)));
        return new RegistryWorkbook(definition.Code, columns, table);
    }

    /// <summary>Значення комірки: посилання — кодом, решта — у поданні <c>GET …/rows</c>.</summary>
    private static string? Cell(RegistryFieldDef field, RegistryRowValueDto? value, IReadOnlyDictionary<long, string> codes)
    {
        if (value is null)
        {
            return null;
        }

        return field.DataType switch
        {
            // Запис-ціль видалено фізично (не буває) — лишається Id: порожнеча збрехала б «не задано».
            CellDataType.Lookup => long.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                                  && codes.TryGetValue(id, out var code)
                ? code
                : value.Value,
            CellDataType.Unit => value.Display ?? value.Value,
            _ => value.Value,
        };
    }

    /// <summary>CSV за RFC 4180, UTF-8 із BOM (інакше Excel читає кирилицю як cp1251).</summary>
    private static MemoryStream ToCsv(IReadOnlyList<RegistryFieldDef> fields, List<IReadOnlyList<string?>> table)
        => new(CsvBytes(fields, table), writable: false);

    /// <summary>Байти CSV одного довідника — рівно те, що приймає <c>POST …/entries/import</c>.</summary>
    private static byte[] CsvBytes(IReadOnlyList<RegistryFieldDef> fields, List<IReadOnlyList<string?>> table)
    {
        var text = new StringBuilder();
        text.Append(CsvFormat.Row([CodeColumn, .. fields.Select(f => f.Code)]));
        foreach (var line in table)
        {
            text.Append(CsvFormat.Row([.. line]));
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text.ToString())];
    }

    /// <summary>
    /// Архів CSV із частинами композиції: <c>01-БАТЬКО.csv</c>, <c>02-ЧАСТИНА.csv</c>… — номер задає
    /// порядок імпорту (батько раніше, інакше посилання частини на нього не розв'яжеться).
    /// </summary>
    private static MemoryStream ToZip(
        List<(RegistryDef Definition, List<RegistryFieldDef> Fields, List<IReadOnlyList<string?>> Table)> sheets)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            for (var i = 0; i < sheets.Count; i++)
            {
                var (definition, fields, table) = sheets[i];
                var entry = zip.CreateEntry(ZipEntryName(i, definition.Code), CompressionLevel.Optimal);
                using var output = entry.Open();
                output.Write(CsvBytes(fields, table));
            }
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>Ім'я файлу довідника в архіві: номер у порядку імпорту і код.</summary>
    internal static string ZipEntryName(int index, string registryCode)
        => $"{(index + 1).ToString("D2", CultureInfo.InvariantCulture)}-{registryCode}.csv";

    private static string? Iso(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
