// src/Ecr.Application/Permits/EndPermitHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits;

/// <summary>
/// Достроково завершує чинний дозвіл: <c>ValidTo</c> і <c>Actual end</c> стають датою завершення (Permits, PM-04).
/// Право <c>Registry.EditData</c> (або грант <c>Write</c>) на реєстр шапки.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Лише чинний (<c>Active</c> на сьогодні); причина - не коротша за <see cref="MinReason"/> символів, іде в журнал
/// структури разом з датою. Дата завершення - ПЕРШИЙ нечинний день: суворо після початку й не пізніше за теперішній кінець
/// (продовжувати чинність - справа <c>PUT</c>, а не «завершення»).
/// </para>
/// <para>
/// ⚠ Рядки таблиць не чіпаються: закінчений дозвіл лишається читабельним, а його періоди - ті, що були.
/// </para>
/// </remarks>
public sealed class EndPermitHandler(
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

    /// <summary>Найменша довжина причини.</summary>
    public const int MinReason = 10;

    /// <summary>Завершує дозвіл.</summary>
    /// <param name="permitId">Дозвіл.</param>
    /// <param name="request">Причина й дата завершення.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c>: причина, дата.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4096 notActive</c>.</exception>
    public async Task<PermitDto> HandleAsync(long permitId, EndPermitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (type, entry) = await new PermitLocator(permits, registries, access, currentUser)
            .FindAsync(permitId, Permission, GrantLevel.Write, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length < MinReason)
        {
            throw PermitErrors.EndReasonTooShort(MinReason);
        }

        var reader = new PermitReader(permits, registries, rows, stats, clock);
        var view = await reader.ViewAsync(type, ct).ConfigureAwait(false) ?? throw PermitErrors.NotFound(permitId);
        ExternalRegistryGuard.EnsureManualEditAllowed(view.Header);
        var endDate = request.EndDate ?? reader.Today;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await rows.LockEntriesAsync([permitId], innerCt).ConfigureAwait(false);
            var current = (await reader.HeadersAsync(view, [permitId], innerCt).ConfigureAwait(false)).FirstOrDefault(h => h.Entry.Id == permitId)
                ?? throw PermitErrors.NotFound(permitId);
            var status = current.StatusOn(reader.Today);
            if (status != PermitStatuses.Active)
            {
                throw PermitErrors.NotActive(permitId, status);
            }

            var from = current.Entry.ValidFrom ?? DateOnly.MinValue;
            if (endDate <= from || (current.Entry.ValidTo is { } to && endDate > to))
            {
                throw PermitErrors.EndDateOutOfRange(endDate, from, current.Entry.ValidTo);
            }

            var old = current.Entry.Window;
            entry.SetValidity(current.Entry.ValidFrom, endDate);
            var changes = new List<RegistryValueFieldChange>
            {
                new(RegistryEntryWriter.ValidityFieldCode, PermitWindowText.Of(old), PermitWindowText.Of(entry.Window)),
            };
            changes.AddRange(await writer.ApplyValuesAsync(
                view.Header, entry, new Dictionary<string, object?> { ["Actual_End_Date"] = endDate }, prefetch: null, innerCt).ConfigureAwait(false));
            await writer.SaveEntryAsync(view.Header, entry, changes, userId, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "dic.RegistryEntry",
                    EntityId: checked((int)permitId),
                    ChangeClass: ChangeClass.Breaking,
                    Operation: "EndPermit",
                    OldJson: System.Text.Json.JsonSerializer.Serialize(new { validTo = old.ToExclusive }),
                    NewJson: System.Text.Json.JsonSerializer.Serialize(new { validTo = endDate, type = type.Code, number = current.Number }),
                    ChangeReason: reason,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var ended = (await reader.HeadersAsync(view, [permitId], ct).ConfigureAwait(false)).Single();
        return await reader.DtoAsync(view, ended, reader.Today, ct).ConfigureAwait(false);
    }
}
