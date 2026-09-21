// src/Ecr.Application/Registries/GetRegistryUsageHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;

namespace Ecr.Application.Registries;

/// <summary>
/// «Де використано» визначення довідника (директива №15, <c>BE-24</c>).
/// </summary>
/// <remarks>
/// ⛔ Питання ставиться ПЕРЕД тим, як чіпати довідник: перевипустити опис,
/// перемкнути master чи вивести довідник з обігу сьогодні можна наосліп —
/// система не каже, скільки колонок шаблонів і полів сусідніх довідників на
/// нього спираються. Відповідь потрібна до дії, а не у вигляді відмови після.
///
/// ⚠ Право — <c>Registry.EditDefinition</c>, а не <c>Registry.View</c>: той
/// самий висновок, що й для одиниць (<c>UnitUsageHandler</c>). Перелік
/// залежних — підготовка до зміни опису, і читати його є сенс лише тому, хто
/// може цю зміну зробити.
/// </remarks>
public sealed class GetRegistryUsageHandler(
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на зміну ОПИСУ довідника (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.EditDefinition";

    /// <summary>Перші <see cref="UsageResponse.PageSize"/> посилань і їх загальна кількість.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає — <c>ECR-REG-0404</c>.</exception>
    public async Task<UsageResponse> HandleAsync(string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⚠ Довідник шукається ДО підрахунку. Нуль посилань на неіснуючий код
        // читався б як «нічого не зламається» — найгірша з можливих відповідей
        // на друкарську помилку в коді довідника.
        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника «{code}» не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registry",
                    ["registryCode"] = code,
                });

        return await registries
            .FindDefinitionUsageAsync(definition.Id, UsageResponse.PageSize, ct)
            .ConfigureAwait(false);
    }
}
