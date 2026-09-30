// src/Ecr.Application/Registries/Rows/GetRegistryRowsHandler.cs
using System.Globalization;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Registries.Rows;

/// <summary>Параметри читання рядків довідника (<c>GET /registries/{code}/rows</c>).</summary>
/// <param name="RegistryCode">Код довідника.</param>
/// <param name="AsOf">Бізнес-дата: чинність записів і батьків композиції; обов'язкова для темпорального.</param>
/// <param name="AsOfUtc">Системний момент (<c>FOR SYSTEM_TIME AS OF</c>); <c>null</c> — поточні дані.</param>
/// <param name="ParentEntryId">Батько композиції (для частини) або каскаду (для решти).</param>
/// <param name="Search">Підрядок коду, назви або текстового поля, без регістру.</param>
/// <param name="FieldFilters">Код поля → точне значення в поданні <see cref="RegistryRowValueDto.Value"/>.</param>
/// <param name="Page">Курсор і розмір сторінки.</param>
public sealed record RegistryRowsRequest(
    string RegistryCode,
    DateOnly AsOf,
    DateTime? AsOfUtc,
    long? ParentEntryId,
    string? Search,
    IReadOnlyDictionary<string, string> FieldFilters,
    CursorRequest Page);

/// <summary>
/// Рядки довідника зі значеннями полів — сторінками за курсором (RT-13, FEATURE-REGISTRY-TABLES §7.1).
/// Право <c>Registry.View</c> або ресурсний грант <c>Read</c> на довідник.
/// </summary>
/// <remarks>
/// ⛔ Видимість — правило пікера (<see cref="RegistryResolver"/>): чинний на <c>asOf</c>, активний, не
/// видалений, а частина композиції — лише коли видно батька, рекурсивно. Інакше редактор показав би
/// склад кейсу, якого пікер і формули вже не бачать.
/// <para>
/// ⚠ Відбір іде в пам'яті над усіма записами довідника (як у пікері — десятки тисяч), а значення
/// читаються лише для сторінки. Курсор — за <c>Id</c>: вставка між сторінками не зсуває межі.
/// </para>
/// </remarks>
public sealed class GetRegistryRowsHandler(
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    RegistryResolver resolver,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає сторінку рядків.</summary>
    /// <param name="request">Параметри.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає — <c>ECR-REG-0404</c>.</exception>
    /// <exception cref="BusinessRuleException">Розмір сторінки, дата чи фільтр — <c>ECR-REQ-0422</c>.</exception>
    public async Task<PagedResult<RegistryRowDto>> HandleAsync(RegistryRowsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await RegistryAccess
            .RequireAsync(
                access, currentUser, Permission, GrantLevel.Read,
                async token => (await registries.FindDefinitionAsync(request.RegistryCode, token).ConfigureAwait(false))?.Id,
                ct)
            .ConfigureAwait(false);

        // ⚠ 422, а не 400: межа сторінки — правило (`ECR-REQ-0422`), як у решті курсорних переліків.
        if (!request.Page.IsValid)
        {
            var max = CursorRequest.MaxLimit.ToString(CultureInfo.InvariantCulture);
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{max}.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange", ["max"] = max });
        }

        var definition = await registries.FindDefinitionAsync(request.RegistryCode, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.RegistryEntryNotFound,
                $"Довідника «{request.RegistryCode}» не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = request.RegistryCode });

        var chain = await CompositionChainAsync(definition, ct).ConfigureAwait(false);

        // Той самий гейт, що в пікері: перелік темпорального довідника (чи частин темпорального
        // батька) залежить від дати, і мовчазне `0001-01-01` збрехало б «порожньо».
        if ((definition.IsTemporal || chain.Exists(link => link.Parent.IsTemporal)) && request.AsOf == default)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Параметр asOf обов'язковий: довідник темпоральний.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.asOfRequired", ["parameter"] = "asOf" });
        }

        var filters = ParseFilters(definition, request.FieldFilters);
        var entries = await rows.ListEntriesAsync(definition.Id, request.AsOfUtc, ct).ConfigureAwait(false);
        var visible = await VisibleAsync(definition, entries, chain, request, ct).ConfigureAwait(false);
        visible = await MatchAsync(definition, visible, request.Search?.Trim(), filters, request.AsOfUtc, ct).ConfigureAwait(false);

        // ⛔ Порядок курсора — Id, а не порядок пікера (Ordinal, Code): «після Id N» має сенс лише в
        // порядку Id. Інакше новий запис із меншим кодом зсунув би межу, і сторінка повторила б рядки.
        var ordered = visible.OrderBy(e => e.Id).ToList();
        var after = DecodeCursor(request.Page.Cursor);
        var page = ordered.Where(e => e.Id > after).Take(request.Page.Limit + 1).ToList();
        var hasMore = page.Count > request.Page.Limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        var slice = await rows.ReadRowsAsync([.. page.Select(e => e.Id)], request.AsOfUtc, ct).ConfigureAwait(false);
        var fields = definition.Fields.ToDictionary(f => f.Id);
        var values = slice.Values.ToLookup(v => v.RegistryEntryId);
        var referenced = slice.Referenced.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());

        var items = page
            .Select(e => new RegistryRowDto(
                e.Id,
                e.Code,
                Display(e),
                e.ParentEntryId,
                e.ValidFrom,
                e.ValidTo,
                RegistryRowVersion.Encode(slice.Versions.GetValueOrDefault(e.Id)),
                Values(values[e.Id], fields, referenced)))
            .ToList();

        return new PagedResult<RegistryRowDto>(items, hasMore ? EncodeCursor(page[^1].Id) : null, ordered.Count);
    }

    /// <summary>Записи, видимі правилом пікера, звужені батьком.</summary>
    private async Task<List<RegistryEntry>> VisibleAsync(
        RegistryDef definition,
        IReadOnlyList<RegistryEntry> entries,
        List<CompositionLink> chain,
        RegistryRowsRequest request,
        CancellationToken ct)
    {
        if (chain.Count == 0)
        {
            IReadOnlyList<RegistryEntryLink> links = request.ParentEntryId is null
                ? []
                : await registries.ListInboundLinksAsync(definition.Id, ct).ConfigureAwait(false);
            return [.. resolver.Select(entries, links, request.AsOf, request.ParentEntryId)];
        }

        // Частина композиції: батько — значення поля композиції (D-155), а не каскад.
        var byId = new Dictionary<long, RegistryEntry>();
        void Remember(IEnumerable<RegistryEntry> set)
        {
            foreach (var entry in set)
            {
                byId[entry.Id] = entry;
            }
        }

        Remember(entries);
        foreach (var link in chain)
        {
            Remember(await rows.ListEntriesAsync(link.Parent.Id, request.AsOfUtc, ct).ConfigureAwait(false));
        }

        var parentOf = (await rows
                .ListFieldValuesAsync([.. chain.Select(link => link.Field.Id)], request.AsOfUtc, ct)
                .ConfigureAwait(false))
            .ToDictionary(v => v.RegistryEntryId, v => v.RefEntryId);
        var children = chain.Select(link => link.Child.Id).ToHashSet();

        var isVisible = resolver.VisibleWithCompositionParents(
            id => byId.TryGetValue(id, out var entry) && resolver.IsSelectable(entry, request.AsOf),
            id => byId.TryGetValue(id, out var entry) && children.Contains(entry.RegistryDefId)
                ? (true, parentOf.GetValueOrDefault(id))
                : (false, null));

        return [.. resolver.Select(entries, [], request.AsOf, null)
            .Where(e => isVisible(e.Id))
            .Where(e => request.ParentEntryId is not { } parent || parentOf.GetValueOrDefault(e.Id) == parent)];
    }

    /// <summary>Пошук підрядком і точні фільтри полів.</summary>
    private async Task<List<RegistryEntry>> MatchAsync(
        RegistryDef definition,
        List<RegistryEntry> visible,
        string? search,
        List<(RegistryFieldDef Field, string Value)> filters,
        DateTime? asOfUtc,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(search) && filters.Count == 0)
        {
            return visible;
        }

        var textFields = string.IsNullOrEmpty(search)
            ? []
            : definition.Fields.Where(f => f.DataType == CellDataType.String).Select(f => f.Id);
        var values = (await rows
                .ListFieldValuesAsync([.. filters.Select(f => f.Field.Id).Union(textFields)], asOfUtc, ct)
                .ConfigureAwait(false))
            .ToLookup(v => v.RegistryEntryId);

        return [.. visible.Where(e =>
            (string.IsNullOrEmpty(search)
             || e.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
             || Display(e).Contains(search, StringComparison.OrdinalIgnoreCase)
             || values[e.Id].Any(v => v.Text?.Contains(search, StringComparison.OrdinalIgnoreCase) == true))
            && filters.TrueForAll(filter => values[e.Id].Any(v =>
                v.RegistryFieldDefId == filter.Field.Id
                && string.Equals(
                    ValueText(filter.Field.DataType, v),
                    filter.Value,
                    filter.Field.DataType == CellDataType.String ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))))];
    }

    /// <summary>Фільтри полів у канонічному поданні значення; хибний фільтр — 422, а не «нічого».</summary>
    private static List<(RegistryFieldDef Field, string Value)> ParseFilters(
        RegistryDef definition, IReadOnlyDictionary<string, string> raw)
    {
        var result = new List<(RegistryFieldDef, string)>();
        foreach (var (code, value) in raw)
        {
            // Порожнє значення — щойно очищене поле фільтра, тобто «фільтра немає».
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var field = definition.Fields.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
            if (field is null || Canonical(field.DataType, value.Trim()) is not { } canonical)
            {
                throw new BusinessRuleException(
                    ErrorCodes.RequestInvalid,
                    $"Фільтр «{code} = {value}» не підходить до довідника «{definition.Code}».",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REQ-0422.registryRowsFilter",
                        ["registryCode"] = definition.Code,
                        ["field"] = code,
                        ["value"] = value,
                    });
            }

            result.Add((field, canonical));
        }

        return result;
    }

    /// <summary>Значення фільтра в тому самому поданні, що й <see cref="ValueText"/>; не розібрано — <c>null</c>.</summary>
    private static string? Canonical(CellDataType type, string text) => type switch
    {
        CellDataType.String => text,
        CellDataType.Int or CellDataType.Decimal
            => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? Number(number) : null,
        CellDataType.Bool => bool.TryParse(text, out var flag) ? Bool(flag) : null,
        CellDataType.Date
            => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null,
        CellDataType.Lookup or CellDataType.Unit
            => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? id.ToString(CultureInfo.InvariantCulture)
                : null,
        _ => null,
    };

    /// <summary>Значення полів рядка за кодами полів.</summary>
    private Dictionary<string, RegistryRowValueDto> Values(
        IEnumerable<RegistryRowValue> values,
        Dictionary<int, RegistryFieldDef> fields,
        Dictionary<long, RegistryEntry> referenced)
    {
        var result = new Dictionary<string, RegistryRowValueDto>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            // Поле, якого вже немає в описі, або значення без даних — не показується.
            if (!fields.TryGetValue(value.RegistryFieldDefId, out var field)
                || ValueText(field.DataType, value) is not { } text)
            {
                continue;
            }

            var display = field.DataType switch
            {
                CellDataType.Lookup when value.RefEntryId is { } target && referenced.TryGetValue(target, out var entry)
                    => Display(entry),
                CellDataType.Unit => value.UnitCode,
                _ => null,
            };

            var unit = field.DataType is CellDataType.Int or CellDataType.Decimal ? value.UnitCode : null;
            result[field.Code] = new RegistryRowValueDto(text, display, unit);
        }

        return result;
    }

    /// <summary>Значення рядком за типом поля; немає значення — <c>null</c>.</summary>
    private static string? ValueText(CellDataType type, RegistryRowValue value) => type switch
    {
        CellDataType.String => value.Text,
        CellDataType.Int or CellDataType.Decimal => value.Numeric is { } number ? Number(number) : null,
        CellDataType.Bool => value.Bool is { } flag ? Bool(flag) : null,
        CellDataType.Date => value.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        CellDataType.Lookup => value.RefEntryId?.ToString(CultureInfo.InvariantCulture),
        CellDataType.Unit => value.UnitId?.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>
    /// ⛔ <c>decimal</c> → рядок напряму, без <c>double</c> (D-30): 16 знаків шкали <c>decimal(34,16)</c>
    /// доходять до клієнта цілими. Хвостові нулі шкали прибрано — <c>49.9999977539011</c>, а не
    /// <c>49.9999977539011000</c>.
    /// </summary>
    private static string Number(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) ? text.TrimEnd('0').TrimEnd('.') : text;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private string Display(RegistryEntry entry) => entry.DisplayL10n.Get(currentUser.Language) ?? entry.Code;

    /// <summary>Курсор — Id останнього рядка сторінки, у форматі решти переліків.</summary>
    private static string EncodeCursor(long id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Зіпсований курсор — початок переліку, а не помилка (як <c>Cursor.Decode</c>).</summary>
    private static long DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        Span<byte> buffer = stackalloc byte[64];
        return Convert.TryFromBase64String(cursor, buffer, out var written)
               && long.TryParse(Encoding.UTF8.GetString(buffer[..written]), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : 0;
    }

    /// <summary>Ланцюжок композиції вгору (<c>D-155</c>); цикл у даних зупиняє обхід.</summary>
    private async Task<List<CompositionLink>> CompositionChainAsync(RegistryDef definition, CancellationToken ct)
    {
        var chain = new List<CompositionLink>();
        var seen = new HashSet<int> { definition.Id };
        var child = definition;

        while (child.Fields.FirstOrDefault(f => f.RelationKind == RegistryRelationKind.Composition
                                                && f.RefRegistryDefId is not null) is { } field
               && seen.Add(field.RefRegistryDefId!.Value)
               && await registries.FindDefinitionByIdAsync(field.RefRegistryDefId.Value, ct).ConfigureAwait(false) is { } parent)
        {
            chain.Add(new CompositionLink(child, field, parent));
            child = parent;
        }

        return chain;
    }

    /// <summary>Ланка композиції: довідник-дитина, його поле композиції і довідник-батько.</summary>
    private sealed record CompositionLink(RegistryDef Child, RegistryFieldDef Field, RegistryDef Parent);
}
