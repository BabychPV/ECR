// src/Ecr.Application/Calculations/MethodologyUnitChecks.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;

namespace Ecr.Application.Calculations;

/// <summary>
/// Перевірка одиниці, яку конфігуратор методології ставить на константу,
/// вихід або формулу (B-01, четвертий раунд UX).
/// </summary>
/// <remarks>
/// ⛔ Доти неіснуючий <c>unitId</c> доходив до бази і падав на
/// <c>FK_MC_Unit</c> / <c>FK_MO_Unit</c> / <c>FK_MF_Unit</c> — 500
/// «зверніться до адміністратора» на описку в номері. Перевіряється ДО зміни
/// сутності знімком довідника (<see cref="IUnitCatalog"/> кешований, один
/// запит на всі одиниці), а не походом по одиниці.
/// </remarks>
public static class MethodologyUnitChecks
{
    /// <summary>Відмовляє, якщо одиниці з таким ідентифікатором немає.</summary>
    /// <param name="units">Довідник одиниць.</param>
    /// <param name="unitId">Одиниця з запиту; <c>null</c> — нічого перевіряти.</param>
    /// <param name="code">Код константи, виходу чи формули — для повідомлення.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0422</c>, ключ <c>unknownUnit</c>.</exception>
    public static async Task RequireKnownAsync(
        IUnitCatalog units, int? unitId, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(units);

        if (unitId is not { } id)
        {
            return;
        }

        var catalogue = await units.GetAsync(ct).ConfigureAwait(false);
        if (catalogue.Units.Values.Any(u => u.Id == id))
        {
            return;
        }

        throw new BusinessRuleException(
            "ECR-CALC-0422",
            $"«{code}»: одиниці {id.ToString(CultureInfo.InvariantCulture)} у довіднику немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0422.unknownUnit",
                ["code"] = code,
                ["unitId"] = id.ToString(CultureInfo.InvariantCulture),
            });
    }
}
