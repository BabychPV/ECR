// src/Ecr.Application/Permits/CreatePermitHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Rules;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits;

/// <summary>
/// Заводить дозвіл ЧЕРНЕТКОЮ: запис реєстру шапки типу з <c>IsActive = 0</c> (Permits, PM-04). Право
/// <c>Registry.EditData</c> (або грант <c>Write</c>) на реєстр шапки типу.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Дозвіл - лише запис шапки; рядки таблиць пишуться наявним <c>entries/batch</c> реєстрів таблиць. Чернетка ключ (тип +
/// номер) не тримає (<c>RegistryKeyService</c>, R2): активація звіряє перетин чинних дозволів.
/// </para>
/// <para>
/// ⚠ Дублем вважається збіг НОМЕРА з ПОЧАТКОМ чинності в межах типу: номер повторюється в поновленнях (їх вікна не
/// перетинаються), тож «унікальний номер» без початку заборонив би саме поновлення.
/// </para>
/// </remarks>
public sealed class CreatePermitHandler(
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
    // ⚠ `ruleEngine` необов'язковий лише для тестів, що будують обробник руками (як у UpsertRegistryEntryHandler);
    // контейнер підставляє його завжди.

    /// <summary>Право на зміну даних реєстрів.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Заводить дозвіл.</summary>
    /// <param name="request">Тип, номер, чинність.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>: типу немає.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c>: поля, чинність, тип не опублікований.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4095</c>: такий дозвіл уже є.</exception>
    public async Task<PermitDto> HandleAsync(CreatePermitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var typeCode = request.TypeCode?.Trim() ?? string.Empty;
        var type = await permits.FindByCodeAsync(typeCode, ct).ConfigureAwait(false);
        if (type is null)
        {
            await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
            throw PermitTypeErrors.NotFound(typeCode);
        }

        try
        {
            await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Write, type.HeaderRegistryDefId, ct).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            throw PermitTypeErrors.NotFound(typeCode);
        }

        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);
        var number = PermitInput.Number(request.Number);
        var location = PermitInput.Location(request.Location);
        PermitInput.Validity(request.ValidFrom, request.ValidTo);

        if (type.Status != PermitTypeStatus.Published)
        {
            throw PermitErrors.TypeNotPublished(type.Code);
        }

        var reader = new PermitReader(permits, registries, rows, stats, clock);
        var view = await reader.ViewAsync(PermitTypeSummary.From(type), ct).ConfigureAwait(false)
            ?? throw PermitTypeErrors.NotFound(typeCode);

        // ⛔ D-211: запис External-довідника - лише синком з AF (шапка дозволу таким не буває, але гард один на всіх писачів).
        ExternalRegistryGuard.EnsureManualEditAllowed(view.Header);

        async Task EnsureNoTwinAsync(CancellationToken token)
        {
            var twin = (await reader.HeadersAsync(view, null, token).ConfigureAwait(false))
                .FirstOrDefault(h => string.Equals(h.Number, number, StringComparison.OrdinalIgnoreCase) && h.Entry.ValidFrom == request.ValidFrom);
            if (twin is not null)
            {
                throw PermitErrors.Duplicate(type.Code, number, request.ValidFrom, twin.Entry.Id);
            }
        }

        // Швидка відмова без транзакції й замка; вирішує перевірка ПІД замком нижче.
        await EnsureNoTwinAsync(ct).ConfigureAwait(false);

        var values = PermitInput.HeaderValues(number, location, request.IssueDate, null, request.ReportingFlags, clearEmpty: false);
        long createdId = 0;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ PA-A-07 (аудит, прохід A): двадцять паралельних POST з тим самим типом, номером і початком проходили перевірку
            // вище обидва (вона бачить лише закомічене) і створювали двадцять чернеток; чернетка ключ не тримає, унікального
            // індексу немає (міграції немає). Замок на (тип, номер) серіалізує створення, а повторна перевірка під ним бачить
            // чернетку переможця, тож решта дістає 409 ECR-PRM-4095, як і послідовний дубль.
            await stats.LockNumberAsync(type.Code, number, innerCt).ConfigureAwait(false);
            await EnsureNoTwinAsync(innerCt).ConfigureAwait(false);

            var entry = await PermitDraftWriter
                .CreateAsync(registries, writer, view, values, number, request.ValidFrom, request.ValidTo, userId, innerCt).ConfigureAwait(false);

            if (ruleEngine is not null)
            {
                (await ruleEngine.EvaluateAsync(view.Header, [entry.Id], [], businessDate: null, innerCt).ConfigureAwait(false)).ThrowIfErrors();
            }

            createdId = entry.Id;
        }, ct).ConfigureAwait(false);

        var created = (await reader.HeadersAsync(view, [createdId], ct).ConfigureAwait(false)).Single();
        return await reader.DtoAsync(view, created, reader.Today, ct).ConfigureAwait(false);
    }
}
