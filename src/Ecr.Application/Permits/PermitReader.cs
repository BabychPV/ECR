// src/Ecr.Application/Permits/PermitReader.cs
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Rows;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits;

/// <summary>Шапка дозволу, прочитана з запису реєстру шапки.</summary>
/// <param name="Entry">Запис (без відстеження).</param>
/// <param name="Number">Номер.</param>
/// <param name="Location">Розташування.</param>
/// <param name="IssueDate">Дата видачі.</param>
/// <param name="ActualEnd">Фактична дата закінчення.</param>
/// <param name="Flags">Прапорці звітних форм.</param>
/// <param name="Version">Початок останнього системного періоду запису й значень (жетон конкуренції).</param>
internal sealed record PermitHeader(
    RegistryEntry Entry,
    string Number,
    string? Location,
    DateOnly? IssueDate,
    DateOnly? ActualEnd,
    PermitReportingFlags Flags,
    DateTime? Version)
{
    /// <summary>Стан на дату.</summary>
    /// <param name="asOf">Дата.</param>
    internal string StatusOn(DateOnly asOf) => PermitLifecycle.StatusOf(Entry.IsActive, Entry.ValidTo, ActualEnd, asOf);
}

/// <summary>Таблиця типу з реєстром і полями, потрібними для підсумків.</summary>
/// <param name="Table">Таблиця типу.</param>
/// <param name="Def">Реєстр таблиці.</param>
/// <param name="PermitField">Поле композиції <c>PERMIT</c>.</param>
/// <param name="TotalField">Головний числовий стовпець; <c>null</c> - такого немає.</param>
internal sealed record PermitTableView(PermitTypeTable Table, RegistryDef Def, RegistryFieldDef PermitField, RegistryFieldDef? TotalField);

/// <summary>Тип разом із реєстром шапки й таблицями.</summary>
/// <param name="Type">Тип.</param>
/// <param name="Header">Реєстр шапки (з полями).</param>
/// <param name="Tables">Таблиці в порядку складу.</param>
internal sealed record PermitTypeView(PermitTypeDef Type, RegistryDef Header, IReadOnlyList<PermitTableView> Tables);

/// <summary>
/// Читання дозволів з реєстрів: шапки, стан, підсумки таблиць (Permits, PM-04). Дозвіл - запис реєстру шапки типу, тож
/// окремого сховища екземплярів немає.
/// </summary>
/// <remarks>
/// <para>
/// Головний підсумок таблиці - Σ ПЕРШОГО обов'язкового стовпця типу <c>Decimal</c> за порядком (без поля композиції).
/// Правило одне на систему: це «річний обсяг / ліміт», який показує екран дозволу.
/// </para>
/// </remarks>
internal sealed class PermitReader(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    IPermitStore stats,
    IClock clock)
{
    private const int Chunk = 500;

    /// <summary>Сьогодні (UTC) - дата за замовчуванням для стану.</summary>
    internal DateOnly Today => DateOnly.FromDateTime(clock.UtcNow);

    /// <summary>Тип, реєстр шапки й таблиці.</summary>
    /// <param name="type">Тип.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<PermitTypeView?> ViewAsync(PermitTypeDef type, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);

        var header = await registries.FindDefinitionByIdAsync(type.HeaderRegistryDefId, ct).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        var tables = new List<PermitTableView>();
        foreach (var table in await permits.ListTablesAsync(type.Id, ct).ConfigureAwait(false))
        {
            var def = await registries.FindDefinitionByIdAsync(table.RegistryDefId, ct).ConfigureAwait(false);
            var permitField = def?.Fields.FirstOrDefault(f => f.RelationKind == RegistryRelationKind.Composition);
            if (def is null || permitField is null)
            {
                continue;
            }

            var total = def.Fields
                .Where(f => f.RelationKind != RegistryRelationKind.Composition && f.DataType == CellDataType.Decimal && f.IsRequired)
                .OrderBy(f => f.Ordinal).ThenBy(f => f.Id)
                .FirstOrDefault();
            tables.Add(new PermitTableView(table, def, permitField, total));
        }

        return new PermitTypeView(type, header, tables);
    }

    /// <summary>Усі живі шапки типу або лише названі.</summary>
    /// <param name="view">Тип.</param>
    /// <param name="ids">Лише ці дозволи; <c>null</c> - усі.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<IReadOnlyList<PermitHeader>> HeadersAsync(PermitTypeView view, IReadOnlyCollection<long>? ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);

        var wanted = ids?.ToHashSet();
        var entries = (await rows.ListEntriesAsync(view.Header.Id, asOfUtc: null, ct).ConfigureAwait(false))
            .Where(e => !e.IsDeleted && (wanted is null || wanted.Contains(e.Id)))
            .OrderBy(e => e.Id)
            .ToList();

        var fieldId = view.Header.Fields.ToDictionary(f => f.Code, f => f.Id, StringComparer.Ordinal);
        var result = new List<PermitHeader>(entries.Count);
        foreach (var chunk in entries.Chunk(Chunk))
        {
            var slice = await rows.ReadRowsAsync([.. chunk.Select(e => e.Id)], asOfUtc: null, ct).ConfigureAwait(false);
            var values = slice.Values.ToLookup(v => v.RegistryEntryId);

            foreach (var entry in chunk)
            {
                RegistryRowValue? Of(string code)
                    => fieldId.TryGetValue(code, out var id) ? values[entry.Id].FirstOrDefault(v => v.RegistryFieldDefId == id) : null;

                bool Flag(string code) => Of(code)?.Bool == true;
                DateOnly? Date(string code) => Of(code)?.Date is { } d ? DateOnly.FromDateTime(d) : null;

                result.Add(new PermitHeader(
                    entry,
                    Of("Permit_Number")?.Text ?? entry.Code,
                    Of("Location")?.Text,
                    Date("Issue_Date"),
                    Date("Actual_End_Date"),
                    new PermitReportingFlags(Flag("Form_300"), Flag("Form_301"), Flag("Form_302"), Flag("TOx")),
                    slice.Versions.TryGetValue(entry.Id, out var version) ? version : null));
            }
        }

        return result;
    }

    /// <summary>Повний дозвіл для відповіді.</summary>
    /// <param name="view">Тип.</param>
    /// <param name="header">Шапка.</param>
    /// <param name="asOf">Дата стану.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<PermitDto> DtoAsync(PermitTypeView view, PermitHeader header, DateOnly asOf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(header);

        var tables = new List<PermitTableDto>();
        foreach (var t in view.Tables)
        {
            var stat = (await stats.RowStatsAsync(t.PermitField.Id, t.TotalField?.Id, [header.Entry.Id], ct).ConfigureAwait(false))
                .GetValueOrDefault(header.Entry.Id);
            tables.Add(new PermitTableDto(
                t.Table.Code,
                PermitTypeReader.Copy(t.Table.NameL10n.Values),
                t.Def.Code,
                stat?.Count ?? 0,
                t.TotalField?.Code,
                t.TotalField is null ? null : stat?.Total ?? 0m));
        }

        var status = header.StatusOn(asOf);
        var names = await UserNamesAsync([header], ct).ConfigureAwait(false);

        return new PermitDto(
            header.Entry.Id,
            header.Number,
            view.Type.Code,
            PermitTypeReader.Copy(view.Type.NameL10n.Values),
            view.Type.Version,
            PermitTypeCodec.CategoryName(view.Type.Category),
            header.Location,
            header.IssueDate,
            header.Entry.ValidFrom,
            header.Entry.ValidTo,
            header.ActualEnd,
            status,
            PermitLifecycle.EndsInDays(status, header.Entry.ValidTo, header.ActualEnd, asOf),
            header.Flags,
            tables,
            [],
            header.Version is { } version ? RegistryRowVersion.Encode(version) : string.Empty,
            header.Version,
            header.Entry.ChangedByUserId is { } user ? names.GetValueOrDefault(user) : null);
    }

    /// <summary>Рядки переліку для одного типу (підсумки таблиць - по таблиці одним запитом).</summary>
    /// <param name="view">Тип.</param>
    /// <param name="asOf">Дата стану.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<IReadOnlyList<PermitListItemDto>> ListItemsAsync(PermitTypeView view, DateOnly asOf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);

        var headers = await HeadersAsync(view, null, ct).ConfigureAwait(false);
        if (headers.Count == 0)
        {
            return [];
        }

        var rowCounts = new Dictionary<long, int>();
        foreach (var t in view.Tables)
        {
            foreach (var (permit, stat) in await stats.RowStatsAsync(t.PermitField.Id, null, null, ct).ConfigureAwait(false))
            {
                rowCounts[permit] = rowCounts.GetValueOrDefault(permit) + stat.Count;
            }
        }

        var names = await UserNamesAsync(headers, ct).ConfigureAwait(false);
        return [.. headers.Select(h =>
        {
            var status = h.StatusOn(asOf);
            return new PermitListItemDto(
                h.Entry.Id,
                h.Number,
                view.Type.Code,
                PermitTypeReader.Copy(view.Type.NameL10n.Values),
                h.Location,
                h.IssueDate,
                h.Entry.ValidFrom,
                h.Entry.ValidTo,
                h.ActualEnd,
                status,
                PermitLifecycle.EndsInDays(status, h.Entry.ValidTo, h.ActualEnd, asOf),
                rowCounts.GetValueOrDefault(h.Entry.Id),
                view.Tables.Count,
                0, // Привʼязок до аркушів ще немає (PM-09): рахувати нічого, 0 - правда, а не заглушка.
                h.Version,
                h.Entry.ChangedByUserId is { } user ? names.GetValueOrDefault(user) : null);
        })];
    }

    /// <summary>Токен запису шапки зараз; <c>null</c> - запису немає.</summary>
    /// <param name="permitId">Дозвіл.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<string?> CurrentVersionAsync(long permitId, CancellationToken ct)
    {
        var slice = await rows.ReadRowsAsync([permitId], asOfUtc: null, ct).ConfigureAwait(false);
        return slice.Versions.TryGetValue(permitId, out var version) ? RegistryRowVersion.Encode(version) : null;
    }

    /// <summary>Токен із запиту збігається з актуальним, інакше <c>409 ECR-PRM-4096 changed</c>.</summary>
    /// <param name="permitId">Дозвіл.</param>
    /// <param name="rowVersion">Токен з запиту.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task RequireVersionAsync(long permitId, string? rowVersion, CancellationToken ct)
    {
        var current = await CurrentVersionAsync(permitId, ct).ConfigureAwait(false) ?? throw PermitErrors.NotFound(permitId);
        if (!string.Equals(current, rowVersion, StringComparison.Ordinal))
        {
            throw PermitErrors.Changed(permitId, current);
        }
    }

    private async Task<IReadOnlyDictionary<int, string>> UserNamesAsync(IReadOnlyCollection<PermitHeader> headers, CancellationToken ct)
        => await registries
            .GetUserDisplayNamesAsync([.. headers.Where(h => h.Entry.ChangedByUserId is not null).Select(h => h.Entry.ChangedByUserId!.Value).Distinct()], ct)
            .ConfigureAwait(false);
}
