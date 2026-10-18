// src/Ecr.Application/Permits/RenewPermitHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits;

/// <summary>
/// Поновлює дозвіл: нова ЧЕРНЕТКА з копією шапки й УСІХ рядків усіх таблиць (Permits, PM-04). Право
/// <c>Registry.EditData</c> (або грант <c>Write</c>) на реєстр шапки.
/// </summary>
/// <remarks>
/// <para>
/// Нове вікно: початок = <c>ValidTo</c> поточного (півінтервал: день, коли попередній уже не діє, - перший день нового),
/// кінець = рівно через рік від початку, якщо не названо інше. Номер той самий: дубль - збіг номера з початком (4095), тож
/// друге поновлення того самого дозволу відмовляє. Дата видачі і фактична дата завершення НЕ копіюються (це відомості
/// конкретного дозволу). Старий дозвіл не змінюється.
/// </para>
/// <para>
/// ⚠ Копіювання - ОДНА транзакція: рядки таблиць пишуться наявним писачем реєстрів (<c>RegistryEntryWriter.WriteAsync</c>:
/// типи, обов'язковість, складені ключі), таблиця за таблицею в порядку сестринських посилань, щоб посилання
/// <c>SamePermitTable</c> вказували на НОВІ рядки сестринської таблиці, а не на рядки старого дозволу. Збій будь-якого рядка
/// відкочує все, включно з новою шапкою.
/// </para>
/// </remarks>
public sealed class RenewPermitHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    IPermitStore stats,
    RegistryEntryWriter writer,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну даних реєстрів.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Поновлює дозвіл.</summary>
    /// <param name="permitId">Поточний дозвіл (чинний чи закінчений).</param>
    /// <param name="request">Необов'язковий кінець нового дозволу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4096 notRenewable</c> (чернетка), <c>ECR-PRM-4095</c> (вже поновлено).</exception>
    public async Task<PermitDto> HandleAsync(long permitId, RenewPermitRequest? request, CancellationToken ct)
    {
        var (type, _) = await new PermitLocator(permits, registries, access, currentUser)
            .FindAsync(permitId, Permission, GrantLevel.Write, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var reader = new PermitReader(permits, registries, rows, stats, clock);
        var view = await reader.ViewAsync(type, ct).ConfigureAwait(false) ?? throw PermitErrors.NotFound(permitId);
        ExternalRegistryGuard.EnsureManualEditAllowed(view.Header);
        foreach (var table in view.Tables)
        {
            ExternalRegistryGuard.EnsureManualEditAllowed(table.Def);
        }

        var all = await reader.HeadersAsync(view, null, ct).ConfigureAwait(false);
        var source = all.FirstOrDefault(h => h.Entry.Id == permitId) ?? throw PermitErrors.NotFound(permitId);
        if (!source.Entry.IsActive || source.Entry.ValidTo is not { } newFrom)
        {
            throw PermitErrors.NotRenewable(permitId);
        }

        var newTo = request?.ValidTo ?? newFrom.AddYears(1);
        PermitInput.Validity(newFrom, newTo);

        var twin = all.FirstOrDefault(h =>
            string.Equals(h.Number, source.Number, StringComparison.OrdinalIgnoreCase) && h.Entry.ValidFrom == newFrom);
        if (twin is not null)
        {
            throw PermitErrors.Duplicate(type.Code, source.Number, newFrom, twin.Entry.Id);
        }

        var values = PermitInput.HeaderValues(source.Number, source.Location, null, null, source.Flags, clearEmpty: false);
        long createdId = 0;
        var copied = 0;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await rows.LockEntriesAsync([permitId], innerCt).ConfigureAwait(false);
            var draft = await PermitDraftWriter
                .CreateAsync(registries, writer, view, values, source.Number, newFrom, newTo, userId, innerCt).ConfigureAwait(false);
            createdId = draft.Id;
            copied = await CopyRowsAsync(view, permitId, draft.Id, userId, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "dic.RegistryEntry",
                    EntityId: checked((int)draft.Id),
                    ChangeClass: ChangeClass.Guarded,
                    Operation: "RenewPermit",
                    OldJson: System.Text.Json.JsonSerializer.Serialize(new { id = permitId }),
                    NewJson: System.Text.Json.JsonSerializer.Serialize(new { id = draft.Id, type = type.Code, number = source.Number, rows = copied }),
                    ChangeReason: $"Дозвіл «{source.Number}» ({type.Code}) поновлено: {newFrom:yyyy-MM-dd} — {newTo:yyyy-MM-dd}, рядків скопійовано: {copied}.",
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var created = (await reader.HeadersAsync(view, [createdId], ct).ConfigureAwait(false)).Single();
        return await reader.DtoAsync(view, created, reader.Today, ct).ConfigureAwait(false);
    }

    private async Task<int> CopyRowsAsync(PermitTypeView view, long oldPermitId, long newPermitId, int userId, CancellationToken ct)
    {
        var tableById = view.Tables.ToDictionary(t => t.Def.Id);
        var children = (await registries.ListCompositionChildrenAsync([oldPermitId], ct).ConfigureAwait(false))
            .Select(c => c.Entry)
            .Where(e => tableById.ContainsKey(e.RegistryDefId))
            .OrderBy(e => e.Id)
            .ToList();
        if (children.Count == 0)
        {
            return 0;
        }

        var valuesByEntry = (await registries.ListValuesForEntriesAsync([.. children.Select(c => c.Id)], ct).ConfigureAwait(false))
            .ToLookup(v => v.RegistryEntryId);
        var created = new Dictionary<long, long>(); // старий рядок -> новий
        var total = 0;

        foreach (var table in InSiblingOrder(view.Tables))
        {
            var olds = children.Where(c => c.RegistryDefId == table.Def.Id).ToList();
            if (olds.Count == 0)
            {
                continue;
            }

            var first = await registries.NextEntryCodesAsync(olds.Count, ct).ConfigureAwait(false);
            var batch = new List<RegistryEntryWrite>(olds.Count);
            for (var i = 0; i < olds.Count; i++)
            {
                var old = olds[i];
                batch.Add(new RegistryEntryWrite(
                    RegistryEntryWriter.AutoCode(first + i),
                    RowValues(table, valuesByEntry[old.Id], newPermitId, tableById, created))
                {
                    DisplayName = old.DisplayL10n.Get(UiStringResolver.DefaultLanguage),
                });
            }

            var result = await writer.WriteAsync(new RegistryEntryWriteBatch(table.Def.Id, batch) { CreateOnly = true }, ct).ConfigureAwait(false);
            if (!result.Applied)
            {
                var failure = result.Errors[0];
                throw PermitErrors.RenewRowInvalid(table.Table.Code, olds[failure.Row - 1].Code, failure.MessageKey);
            }

            foreach (var row in result.Rows)
            {
                created[olds[row.Row - 1].Id] = row.Entry.Id;
            }

            total += olds.Count;
        }

        return total;
    }

    /// <summary>Значення рядка нового дозволу: усе, крім посилання на дозвіл, - з рядка старого; сестринські посилання - на нові рядки.</summary>
    private static Dictionary<string, object?> RowValues(
        PermitTableView table,
        IEnumerable<RegistryValue> source,
        long newPermitId,
        Dictionary<int, PermitTableView> tableById,
        Dictionary<long, long> created)
    {
        var fields = table.Def.Fields.ToDictionary(f => f.Id);
        var values = new Dictionary<string, object?> { [table.PermitField.Code] = newPermitId };

        foreach (var value in source)
        {
            if (!fields.TryGetValue(value.RegistryFieldDefId, out var field) || field.Id == table.PermitField.Id)
            {
                continue;
            }

            object? raw = field.DataType switch
            {
                CellDataType.String => value.ValueString,
                CellDataType.Int or CellDataType.Decimal => value.ValueNumeric,
                CellDataType.Bool => value.ValueBool,
                CellDataType.Date => value.ValueDate,
                CellDataType.Unit => value.ValueUnitId,
                CellDataType.Lookup when value.ValueRefEntryId is { } target
                    => field.RefRegistryDefId is { } refDef && tableById.ContainsKey(refDef) && created.TryGetValue(target, out var copy) ? copy : target,
                _ => null,
            };

            if (raw is not null)
            {
                values[field.Code] = raw;
            }
        }

        return values;
    }

    /// <summary>Таблиці в порядку, де сестринська ціль посилання йде РАНІШЕ за таблицю, що посилається (цикли заборонені описом типу).</summary>
    private static List<PermitTableView> InSiblingOrder(IReadOnlyList<PermitTableView> tables)
    {
        var ids = tables.Select(t => t.Def.Id).ToHashSet();
        var needs = tables.ToDictionary(
            t => t.Def.Id,
            t => t.Def.Fields
                .Where(f => f.DataType == CellDataType.Lookup && f.RefRegistryDefId is { } r && r != t.Def.Id && ids.Contains(r))
                .Select(f => f.RefRegistryDefId!.Value)
                .ToHashSet());

        var ordered = new List<PermitTableView>();
        var done = new HashSet<int>();
        while (ordered.Count < tables.Count)
        {
            var next = tables.Where(t => !done.Contains(t.Def.Id)).FirstOrDefault(t => needs[t.Def.Id].IsSubsetOf(done))
                ?? tables.First(t => !done.Contains(t.Def.Id)); // цикл (дані повз опис) - у вихідному порядку, а не нескінченність
            ordered.Add(next);
            done.Add(next.Def.Id);
        }

        return ordered;
    }
}
