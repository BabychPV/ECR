// src/Ecr.Application/Units/UnitUsageHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Errors;

namespace Ecr.Application.Units;

/// <summary>«Де використовується одиниця» (директива №15, BE-15).</summary>
/// <remarks>
/// ⚠ Право — те саме <c>Uom.EditCatalog</c>, що й на зміну довідника: єдиний
/// споживач переліку — діалог видалення, а він відкривається лише тому, хто
/// може видаляти.
/// </remarks>
public sealed class UnitUsageHandler(
    IUnitStore units,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Перші <see cref="UsageResponse.PageSize"/> посилань і загальна кількість.</summary>
    /// <param name="unitId">Одиниця.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Одиниці немає — <c>ECR-UOM-0404</c>.</exception>
    public async Task<UsageResponse> HandleAsync(int unitId, CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, CreateUnitHandler.Permission, ct)
            .ConfigureAwait(false);

        await FindAsync(units, unitId, ct).ConfigureAwait(false);

        return await units.FindUnitUsageAsync(unitId, UsageResponse.PageSize, ct).ConfigureAwait(false);
    }

    /// <summary>Одиниця за ідентифікатором або <c>ECR-UOM-0404</c>.</summary>
    internal static async Task<Unit> FindAsync(IUnitStore units, int unitId, CancellationToken ct)
        => await units.FindUnitByIdAsync(unitId, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               ErrorCodes.UnitNotFound,
               $"Одиниці з ідентифікатором {unitId} немає в довіднику.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-UOM-0404.unitId",
                   ["id"] = unitId.ToString(CultureInfo.InvariantCulture),
               });
}

/// <summary>Видалення одиниці довідника <c>uom.Unit</c> (директива №15, BE-15).</summary>
/// <remarks>
/// ⛔ Одиниця, на яку щось посилається, не видаляється — <c>409 ECR-UOM-0409</c>
/// із переліком залежних у <c>details.references</c>. Зовнішні ключі відхилили
/// б таке видалення й самі, але голим <c>500</c>: людина дізналася б, що «щось
/// пішло не так», а не ЩО саме тримає одиницю.
/// </remarks>
public sealed class DeleteUnitHandler(
    IUnitStore units,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Видаляє одиницю, якщо на неї ніхто не посилається.</summary>
    /// <param name="unitId">Одиниця.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Одиниці немає — <c>ECR-UOM-0404</c>.</exception>
    /// <exception cref="ConcurrencyConflictException">На одиницю посилаються — <c>ECR-UOM-0409</c>.</exception>
    public async Task HandleAsync(int unitId, CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, CreateUnitHandler.Permission, ct)
            .ConfigureAwait(false);

        var unit = await UnitUsageHandler.FindAsync(units, unitId, ct).ConfigureAwait(false);

        var usage = await units.FindUnitUsageAsync(unitId, UsageResponse.PageSize, ct).ConfigureAwait(false);
        if (usage.Total > 0)
        {
            // ⚠ Саме `ConcurrencyConflictException`: статус відповіді береться
            // з ТИПУ винятку, а цифри коду кажуть 409 — той самий висновок, що
            // в `TableRelationHandlers` і `PatchPresentationHandler`.
            throw new ConcurrencyConflictException(
                ErrorCodes.UnitInUse,
                $"Одиниця «{unit.Code}» не видаляється: на неї посилаються — {usage.Total}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-UOM-0409.unitInUse",
                    ["code"] = unit.Code,
                    ["total"] = usage.Total.ToString(CultureInfo.InvariantCulture),
                    ["references"] = usage.Items,
                });
        }

        units.RemoveUnit(unit);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
