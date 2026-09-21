// src/Ecr.Application/Units/CreateUnitHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Units;

/// <summary>
/// Заведення нової одиниці довідника <c>uom.Unit</c> (UI-аудит, lane 4:
/// «жоден обліковий запис, включно з повноправним адміністратором, не міг
/// додати одиницю виміру жодним шляхом, доступним людині» — той самий клас
/// дефекту, що вже виправлений для довідників, `Q-200`).
/// </summary>
/// <remarks>
/// ⛔ Нова одиниця — ЗАВЖДИ похідна (<c>IsBase = false</c>), не базова:
/// у розмірності вже є рівно одна базова одиниця, заведена seed-ом
/// (`kg`, `m3`, `J`, `s`, `K`, `mol`, `one`), і друга базова зробила б
/// конверсію в межах розмірності неоднозначною (від якої з двох рахувати
/// множник?). Форма СВІДОМО не пропонує цей вибір — не бракує поля, а
/// заборонено те, що ламає модель.
/// </remarks>
public sealed class CreateUnitHandler(
    IUnitStore units,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на заведення одиниці (`02-contracts.md` §9).</summary>
    public const string Permission = "Uom.EditCatalog";

    /// <summary>Заводить нову похідну одиницю.</summary>
    /// <param name="code">Код, унікальний серед одиниць.</param>
    /// <param name="symbol">Позначення мовами каталогу (<c>kg</c>, <c>т</c>).</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="dimensionId">Розмірність — має існувати в `uom.Dimension`.</param>
    /// <param name="factorToBase">Множник переходу до базової одиниці розмірності.</param>
    /// <param name="offsetToBase">Зсув; ненульовий лише для одиниць температури.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// Код уже зайнято — <c>ECR-UOM-4091</c>; множник переходу не додатний —
    /// <c>ECR-UOM-0422</c>.
    /// </exception>
    /// <exception cref="NotFoundException">Розмірності з таким ідентифікатором немає.</exception>
    public async Task<Unit> HandleAsync(
        string code,
        IReadOnlyDictionary<string, string> symbol,
        IReadOnlyDictionary<string, string> name,
        byte dimensionId,
        decimal factorToBase,
        decimal offsetToBase,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(name);

        await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var unitCode = EcrCode.Create(code);

        // ⛔ Унікальність коду — той самий прецедент, що й `CreateRegistryHandler`:
        // код — те, чим на одиницю посилаються формули й довідник (`UnitRef.Code`),
        // і мовчазний дублікат зробив би це посилання неоднозначним.
        var clash = await units.FindUnitByCodeAsync(unitCode.Value, ct).ConfigureAwait(false);
        if (clash is not null)
        {
            throw new BusinessRuleException(
                ErrorCodes.UnitCodeTaken,
                $"Одиниця «{unitCode.Value}» уже існує (ідентифікатор {clash.Id}).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-UOM-4091.unitCodeTaken",
                    ["code"] = unitCode.Value,
                    ["id"] = clash.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Нульовий (і від'ємний) множник — ВІДМОВА на вході, а не помилка
        // десь у конверсії через пів року. `factorToBase = 0` згортає
        // `(value × factor) + offset` до КОНСТАНТИ: кожне значення, конвертоване
        // з цієї одиниці, дає те саме число, тихо, без жодної помилки, і йде в
        // поданий регуляторний звіт. Порожнє числове поле форми = 0, тож це не
        // теоретичний випадок, а типова помилка вводу.
        if (factorToBase <= 0m)
        {
            throw new BusinessRuleException(
                "ECR-UOM-0422",
                $"Множник переходу до базової одиниці мусить бути додатним, а не {factorToBase}: "
                + "нуль згортає конверсію до константи, від'ємний — перевертає знак величини.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-UOM-0422",
                    ["code"] = unitCode.Value,
                    ["factorToBase"] = factorToBase.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        if (!await units.DimensionExistsAsync(dimensionId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                ErrorCodes.UnitDimensionNotFound, $"Розмірності з ідентифікатором {dimensionId} немає в довіднику.");
        }

        var unit = new Unit(
            unitCode,
            new LocalizedText(new Dictionary<string, string>(symbol, StringComparer.OrdinalIgnoreCase)),
            new LocalizedText(new Dictionary<string, string>(name, StringComparer.OrdinalIgnoreCase)),
            dimensionId,
            isBase: false,
            factorToBase,
            offsetToBase);

        units.AddUnit(unit);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return unit;
    }
}
