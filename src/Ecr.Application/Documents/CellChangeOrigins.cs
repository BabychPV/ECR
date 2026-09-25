// src/Ecr.Application/Documents/CellChangeOrigins.cs
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents;

/// <summary>
/// Походження зміни комірки (<c>aud.CellChange.Origin</c>) і те, яке з них
/// може заявити КЛІЄНТ через <c>PATCH …/cells</c> (B-05).
/// </summary>
/// <remarks>
/// ⛔ Поле <c>origin</c> приходило з тіла запиту і без жодної перевірки лягало
/// в журнал та в логіку конфлікту «людина/система»
/// (<c>PatchCellsHandler</c>: розбіжність із правкою НЕ-людини не показується
/// як чужа правка). Тобто будь-хто з правом на запис міг підписати свою правку
/// як <c>Recalculation</c> — і в журналі, і на екрані конфлікту вона виглядала
/// б як робота системи — або як довільний рядок <c>NOPE</c>.
///
/// ⚠ Перевіряє КОНТРОЛЕР, а не обробник: той самий обробник законно кличуть
/// системні шляхи з іншими значеннями — імпорт (<c>Import</c>,
/// <c>ExcelImporter</c>) і інтеграція (<c>Integration</c>,
/// <c>IntegrationCellPatcher</c>); перерахунок пише повз нього
/// (<c>Recalculation</c>, <c>RecalculationService</c>). Клієнт
/// (<c>features/grid/useCellPatch.ts</c>) шле лише <see cref="UserEdit"/>.
/// </remarks>
public static class CellChangeOrigins
{
    /// <summary>Правка людини в сітці — єдине, що може заявити клієнт.</summary>
    public const string UserEdit = "UserEdit";

    /// <summary>Скільки символів значення повертається в тексті відмови.</summary>
    private const int EchoLength = 32;

    /// <summary>
    /// Вимагає, щоб походження, заявлене клієнтом, було людським.
    /// </summary>
    /// <param name="origin">Значення з тіла запиту.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REQ-0422</c> — інше походження.</exception>
    public static void RequireClientOrigin(string? origin)
    {
        if (string.Equals(origin, UserEdit, StringComparison.Ordinal))
        {
            return;
        }

        // ⚠ Значення повертається обрізаним: воно прийшло з мережі, і текст
        // відмови не має ставати дзеркалом для довільного вмісту.
        var echoed = origin is null
            ? string.Empty
            : origin.Length > EchoLength ? origin[..EchoLength] : origin;

        throw new BusinessRuleException(
            ErrorCodes.RequestInvalid,
            $"Походження «{echoed}» клієнт заявити не може: правка людини записується як {UserEdit}.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-REQ-0422.cellOriginNotAllowed",
                ["origin"] = echoed,
                ["allowed"] = UserEdit,
            });
    }
}
