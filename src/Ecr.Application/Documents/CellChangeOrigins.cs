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

    /// <summary>
    /// Імпорт книги Excel (<c>ExcelImporter</c>): значення, які людина ввела в
    /// книгу й свідомо застосувала.
    /// </summary>
    /// <remarks>
    /// ⛔ D2-01 (аудит 09.10b): для сторожів «правка людини» інтеграції і синку
    /// подій (<c>D-118</c>, <c>D-187</c>) імпорт — така сама правка людини, як
    /// <see cref="UserEdit"/>. Клієнт заявити його через PATCH не може
    /// (<see cref="RequireClientOrigin"/>).
    /// </remarks>
    public const string Import = "Import";

    /// <summary>
    /// Імпорт книги Excel, у якому людина СВІДОМО перезаписала чужу правку,
    /// зроблену після експорту (AN-114, D-338: прапорець «перезаписати» на
    /// конфліктному рядку перегляду).
    /// </summary>
    /// <remarks>
    /// ⚠ Для сторожів «правка людини» — те саме, що <see cref="Import"/>
    /// (<see cref="HumanOriginsSql"/>); клієнт через PATCH його не заявить
    /// (<see cref="RequireClientOrigin"/>). Окреме значення — лише щоб журнал
    /// відрізняв «повернула старе число, не знаючи» (цього AN-103 вже не пускає)
    /// від «знала й перезаписала».
    /// </remarks>
    public const string ImportOverwrite = "ImportOverwrite";

    /// <summary>
    /// Походження, які сторожі інтеграції вважають рішенням людини, — готовим
    /// списком для <c>IN (…)</c> у сирому SQL над <c>aud.CellChange</c> (журнал
    /// не є сутністю EF).
    /// </summary>
    /// <remarks>
    /// ⛔ D2-01: сторожі порівнювали <c>Origin</c> з літералом <c>UserEdit</c> у трьох
    /// місцях (<c>IntegrationCellPatcher.ManualCellsAsync</c>,
    /// <c>SourceEventSyncJob.RecheckRemovalAsync</c>/<c>ManualRowKeysAsync</c>) і
    /// не бачили <see cref="Import"/>: інтеграція перезаписувала імпортоване, а
    /// синк жорстко видаляв рядки, доповнені імпортом. Один перелік тут — щоб
    /// наступне «людське» походження не довелося шукати по SQL
    /// (<c>HumanOriginSqlLiteralTests</c> стежить, щоб літерал не повернувся).
    /// <para>
    /// ⚠ Той самий перелік, що <see cref="IsHuman"/> (підпис автора в конфлікті
    /// PATCH, AN-115): SQL-сторож і екран мусять однаково відповідати на
    /// «людина чи система».
    /// </para>
    /// </remarks>
    public const string HumanOriginsSql = "N'" + UserEdit + "', N'" + Import + "', N'" + ImportOverwrite + "'";

    /// <summary>
    /// Чи зробила зміну з цим походженням ЛЮДИНА: <see cref="UserEdit"/>,
    /// <see cref="Import"/>, <see cref="ImportOverwrite"/>. Перерахунок,
    /// інтеграція, міграція — система.
    /// </summary>
    /// <remarks>
    /// ⛔ AN-115: підпис автора в діалозі конфлікту (<c>PatchCellsHandler</c>)
    /// визнавав людиною лише <see cref="UserEdit"/>, тож чуже значення з книги
    /// Excel підписувалося «system», хоча його ввела й застосувала конкретна
    /// людина. Перелік збігається з <see cref="HumanOriginsSql"/>.
    /// </remarks>
    /// <param name="origin">Походження з <c>aud.CellChange.Origin</c>.</param>
    public static bool IsHuman(string? origin) =>
        string.Equals(origin, UserEdit, StringComparison.Ordinal)
        || string.Equals(origin, Import, StringComparison.Ordinal)
        || string.Equals(origin, ImportOverwrite, StringComparison.Ordinal);

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
