// src/Ecr.Application/Permits/UpdatePermitHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Rows;
using Ecr.Application.Registries.Rules;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits;

/// <summary>
/// Змінює шапку дозволу, поки він чернетка або чинний (не закінчений). Право <c>Registry.EditData</c> (або грант
/// <c>Write</c>) на реєстр шапки (Permits, PM-04).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Оптимістична конкуренція - токен запису шапки (<see cref="RegistryRowVersion"/>: початок останнього системного
/// періоду запису й значень), звіряється під блокуванням запису в транзакції збереження: хто програв, дістає
/// <c>409 ECR-PRM-4096 changed</c>, а не мовчки затирає чужу правку.
/// </para>
/// <para>
/// ⚠ Статус не змінюється (чернетка лишається чернеткою): його міняють лише <c>activate</c> / <c>end</c>.
/// </para>
/// </remarks>
public sealed class UpdatePermitHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    IPermitStore stats,
    RegistryEntryWriter writer,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    IRegistryRuleEngine? ruleEngine = null)
{
    /// <summary>Право на зміну даних реєстрів.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Змінює шапку.</summary>
    /// <param name="permitId">Дозвіл.</param>
    /// <param name="request">Нові значення шапки й токен.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c>: поля, чинність.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4096</c> (токен, закінчений дозвіл), <c>ECR-PRM-4095</c> (дубль).</exception>
    public async Task<PermitDto> HandleAsync(long permitId, UpdatePermitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (type, entry) = await new PermitLocator(permits, registries, access, currentUser)
            .FindAsync(permitId, Permission, GrantLevel.Write, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var number = PermitInput.Number(request.Number);
        var location = PermitInput.Location(request.Location);
        PermitInput.Validity(request.ValidFrom, request.ValidTo);

        var reader = new PermitReader(permits, registries, rows, stats, clock);
        var view = await reader.ViewAsync(type, ct).ConfigureAwait(false) ?? throw PermitErrors.NotFound(permitId);
        ExternalRegistryGuard.EnsureManualEditAllowed(view.Header);

        var all = await reader.HeadersAsync(view, null, ct).ConfigureAwait(false);
        var current = all.FirstOrDefault(h => h.Entry.Id == permitId) ?? throw PermitErrors.NotFound(permitId);
        if (current.StatusOn(reader.Today) == PermitStatuses.Expired)
        {
            throw PermitErrors.NotEditable(permitId);
        }

        var twin = all.FirstOrDefault(h =>
            h.Entry.Id != permitId
            && string.Equals(h.Number, number, StringComparison.OrdinalIgnoreCase)
            && h.Entry.ValidFrom == request.ValidFrom);
        if (twin is not null)
        {
            throw PermitErrors.Duplicate(type.Code, number, request.ValidFrom, twin.Entry.Id);
        }

        // Чинний дозвіл тримає ключ: зміна номера чи вікна не має перетнути інший чинний дозвіл того самого номера (4092).
        // Чернетка ключ не тримає, її вікно звіряє активація.
        if (current.Entry.IsActive
            && PermitLifecycle.ActiveOverlap(all, permitId, number, new ValidityWindow(request.ValidFrom, request.ValidTo)) is { } other)
        {
            throw PermitErrors.Overlap(permitId, other.Entry.Id, number);
        }

        var values = PermitInput.HeaderValues(number, location, request.IssueDate, request.ActualEnd, request.ReportingFlags, clearEmpty: true);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // Блокування запису, потім звірка токена: читання версії - новим оператором, тож бачить уже закомічену чужу правку.
            await rows.LockEntriesAsync([permitId], innerCt).ConfigureAwait(false);
            await reader.RequireVersionAsync(permitId, request.RowVersion, innerCt).ConfigureAwait(false);

            var changes = (await writer.ApplyValuesAsync(view.Header, entry, values, prefetch: null, innerCt).ConfigureAwait(false)).ToList();
            if (entry.Window != new ValidityWindow(request.ValidFrom, request.ValidTo))
            {
                var old = entry.Window;
                entry.SetValidity(request.ValidFrom, request.ValidTo);
                changes.Add(new RegistryValueFieldChange(
                    RegistryEntryWriter.ValidityFieldCode, PermitWindowText.Of(old), PermitWindowText.Of(entry.Window)));
            }

            entry.Rename(new LocalizedText(MergedName(entry.DisplayL10n, number)));
            try
            {
                await writer.SaveEntryAsync(view.Header, entry, changes, userId, innerCt).ConfigureAwait(false);
            }
            catch (BusinessRuleException ex) when (ex.ErrorCode == Ecr.Domain.Errors.ErrorCodes.RegistryKeyConflict)
            {
                // Гонка з паралельною зміною чи активацією між звіркою вище й записом ключа - та сама відмова.
                var otherId = long.TryParse(ex.Details?.GetValueOrDefault("entryId") as string, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;
                throw PermitErrors.Overlap(permitId, otherId, number);
            }

            if (ruleEngine is not null)
            {
                (await ruleEngine.EvaluateAsync(view.Header, [entry.Id], [], businessDate: null, innerCt).ConfigureAwait(false)).ThrowIfErrors();
            }
        }, ct).ConfigureAwait(false);

        var updated = (await reader.HeadersAsync(view, [permitId], ct).ConfigureAwait(false)).Single();
        return await reader.DtoAsync(view, updated, reader.Today, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> MergedName(LocalizedText current, string number)
    {
        var merged = new Dictionary<string, string>(current.Values, StringComparer.OrdinalIgnoreCase)
        {
            [Localization.UiStringResolver.DefaultLanguage] = number,
        };
        return merged;
    }
}

/// <summary>Вікно чинності для події аудиту: <c>[2026-01-01, 2027-01-01)</c>.</summary>
internal static class PermitWindowText
{
    /// <summary>Текст вікна.</summary>
    /// <param name="window">Вікно.</param>
    internal static string Of(ValidityWindow window)
        => $"[{window.FromInclusive?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "-∞"}, "
           + $"{window.ToExclusive?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "∞"})";
}
