// src/Ecr.Application/Registries/Export/ExportRegistryHandler.cs
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Rows;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
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
    public async Task<RegistryExportFile> HandleAsync(
        string registryCode, string? format, DateOnly? asOf, int maxRows, CancellationToken ct)
    {
        await RegistryAccess
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
        var request = new RegistryRowsRequest(
            registryCode, date, AsOfUtc: null, ParentEntryId: null, Search: null,
            new Dictionary<string, string>(), new CursorRequest(Chunk));

        var (definition, ordered) = await rows.SelectAsync(request, ct).ConfigureAwait(false);

        // Стеля — ДО читання значень і до файлу: 422, а не обрізаний файл.
        if (ordered.Count > maxRows)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"У довіднику «{definition.Code}» на {date:yyyy-MM-dd} записів {ordered.Count}, а стеля експорту — {maxRows}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.registryExportTooLarge",
                    ["registryCode"] = definition.Code,
                    ["total"] = ordered.Count.ToString(CultureInfo.InvariantCulture),
                    ["max"] = maxRows.ToString(CultureInfo.InvariantCulture),
                });
        }

        var fields = definition.Fields.OrderBy(f => f.Ordinal).ThenBy(f => f.Id).ToList();
        var xlsx = kind == Xlsx;
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

        // ⛔ C4: подія-СПРОБА (дані покидають систему), а не результат зміни.
        await audit.WriteIndependentSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                ExportedEventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new
                {
                    registryDefId = definition.Id,
                    registryCode = definition.Code,
                    format = kind,
                    asOf = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    rows = table.Count,
                }),
                currentUser.UserId ?? 0,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        var fileName = $"registry-{definition.Code}-{date:yyyyMMdd}.{kind}";
        if (!xlsx)
        {
            return new RegistryExportFile(fileName, CsvContentType, ToCsv(fields, table), table.Count);
        }

        var columns = new List<RegistryWorkbookColumn>
        {
            new(CodeColumn, CellDataType.String),
            new(NameColumn, CellDataType.String),
            new(ValidFromColumn, CellDataType.Date),
            new(ValidToColumn, CellDataType.Date),
        };
        columns.AddRange(fields.Select(f => new RegistryWorkbookColumn(
            f.Code, f.DataType is CellDataType.Lookup or CellDataType.Unit ? CellDataType.String : f.DataType)));

        var book = await workbooks
            .WriteAsync(new RegistryWorkbook(definition.Code, columns, table), ct)
            .ConfigureAwait(false);
        return new RegistryExportFile(fileName, XlsxContentType, book, table.Count);
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
    {
        var text = new StringBuilder();
        text.Append(CsvFormat.Row([CodeColumn, .. fields.Select(f => f.Code)]));
        foreach (var line in table)
        {
            text.Append(CsvFormat.Row([.. line]));
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var stream = new MemoryStream();
        stream.Write(encoding.GetPreamble());
        stream.Write(encoding.GetBytes(text.ToString()));
        stream.Position = 0;
        return stream;
    }

    private static string? Iso(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
