using Ecr.Application.Common;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>Документ у переліку.</summary>
/// <remarks>
/// ⛔ Статусу тут немає (<c>D-93</c>). Зведений стан рахується запитом до
/// <c>wf.ApprovalState</c> і віддається окремим полем
/// <paramref name="SheetStates"/>: скалярний статус був би другим джерелом
/// істини і рано чи пізно показав би <c>Approved</c> на документі, половина
/// аркушів якого ще в <c>Draft</c>.
/// </remarks>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="BusinessKey">Бізнес-ключ, унікальний у межах проєкту.</param>
/// <param name="CreatedAt">Момент створення.</param>
/// <param name="SheetCount">Скільки аркушів у складі.</param>
/// <param name="SheetStates">Стан робочого процесу: аркуш → статус.</param>
/// <param name="NameL10n">
/// Людське ім'я документа мовами каталогу; <c>null</c> — не задано
/// (директива "людське ім'я документа"). Показується ПОРУЧ із
/// <paramref name="BusinessKey"/>, не замість нього.
/// </param>
/// <param name="ModifiedAt">Остання зміна (UTC); <c>null</c> — шлях читання її не несе.</param>
/// <param name="ModifiedByDisplayName">Хто змінив; <c>null</c> — користувача вже немає.</param>
/// <param name="ErrorCount">
/// Помилки з ОСТАННЬОГО збереженого підсумку перевірки за період (<c>BE-09</c>).
/// ⛔ <c>null</c> — документ за цей період не перевіряли (або період не
/// задано); це НЕ нуль: «0 зауважень» під неперевіреним документом — та сама
/// неправда, що <c>A7-28</c>.
/// </param>
/// <param name="WarningCount">Попередження звідти ж; <c>null</c> — за тим самим правилом.</param>
public sealed record DocumentSummary(
    long Id,
    int ProjectId,
    string BusinessKey,
    DateTime CreatedAt,
    int SheetCount,
    IReadOnlyDictionary<string, string> SheetStates,
    LocalizedText? NameL10n = null,
    DateTime? ModifiedAt = null,
    string? ModifiedByDisplayName = null,
    int? ErrorCount = null,
    int? WarningCount = null);

/// <summary>Порушення правила складу документа.</summary>
/// <param name="SheetGroup">Група аркушів.</param>
/// <param name="RuleKind">Вид правила: 0 <c>RequiresAll</c>, 1 <c>RequiresOne</c>, 2 <c>Optional</c>.</param>
/// <param name="Detail">Що саме не так.</param>
public sealed record CompositionViolation(string SheetGroup, byte RuleKind, string Detail);

/// <summary>
/// Сире правило складу документа — без перевірки конкретного вибору
/// аркушів (директива "live-попередження про порушення SheetGroupRule").
/// </summary>
/// <param name="SheetGroup">Група, якої стосується правило.</param>
/// <param name="RuleKind">
/// Вид правила: <c>0 RequiresAll</c>, <c>1 RequiresOne</c>, <c>2 Excludes</c>.
/// </param>
/// <param name="TargetGroup">Група-ціль; заповнена лише для <c>Excludes</c>.</param>
/// <remarks>
/// ⚠ Віддається СИРИМ правилом, а не готовим вердиктом
/// (<see cref="CompositionViolation"/>): вердикт залежить від вибору
/// аркушів, якого на момент читання структури клієнт ще не зробив.
/// Клієнт (<c>CreateDocumentModal.tsx</c>) рахує порушення локально при
/// кожній зміні чекбоксів — сервер лишається останньою лінією правди
/// через <see cref="IDocumentStore.ValidateCompositionAsync"/>.
/// </remarks>
public sealed record SheetGroupRuleSummary(string SheetGroup, byte RuleKind, string? TargetGroup);

/// <summary>Читання і створення документів.</summary>
public interface IDocumentStore
{
    /// <summary>Додає документ разом зі складом аркушів.</summary>
    public Task AddAsync(Document document, CancellationToken ct);

    /// <summary>Документ за ідентифікатором; <c>null</c> — не існує.</summary>
    public Task<DocumentSummary?> FindAsync(long documentId, PeriodKeyFilter period, CancellationToken ct);

    /// <summary>Сторінка документів проєкту.</summary>
    /// <param name="projectId">Фільтр за проєктом; <c>null</c> — усі.</param>
    /// <param name="period">Період для зведеного стану аркушів.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="visibleProjectIds">
    /// Проєкти, на які в користувача є грант читання. <c>null</c> — без
    /// фільтра за грантами (лише для інтеграційних перевірок самого запиту).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Гранти враховуються ЗАПИТОМ, а не лише постфільтром у обробнику, і це
    /// не оптимізація. Постфільтр давав дві діри одночасно (аудит 2026-09-16,
    /// §3.3): <c>TotalCount</c> рахувався по ВСІХ проєктах системи — тобто
    /// користувач з грантом на один проєкт бачив загальну кількість документів
    /// у чужих, — і сторінка віддавала менше за <c>page.Limit</c> видимих
    /// елементів, поки <c>NextCursor</c> вказував далі в НЕфільтрованій
    /// послідовності: «N з TotalCount» водночас неправильне й небезпечне.
    /// </remarks>
    public Task<PagedResult<DocumentSummary>> ListAsync(
        int? projectId,
        PeriodKeyFilter period,
        CursorRequest page,
        IReadOnlyCollection<int>? visibleProjectIds,
        CancellationToken ct);

    /// <summary>
    /// Перевіряє склад за <c>SheetGroupRule</c> (ФВ-3.2).
    /// </summary>
    /// <returns>Порожній перелік — склад коректний.</returns>
    public Task<IReadOnlyList<CompositionViolation>> ValidateCompositionAsync(
        int templateVersionId, IReadOnlyList<int> sheetDefIds, CancellationToken ct);

    /// <summary>
    /// Усі правила складу версії, без перевірки конкретного вибору
    /// (директива "live-попередження про порушення SheetGroupRule").
    /// </summary>
    public Task<IReadOnlyList<SheetGroupRuleSummary>> GetGroupRulesAsync(
        int templateVersionId, CancellationToken ct);

    /// <summary>Наступний вільний бізнес-ключ у межах проєкту.</summary>
    public Task<string> NextBusinessKeyAsync(int projectId, int templateVersionId, CancellationToken ct);

    /// <summary>Чи входить аркуш у СКЛАД цього документа.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Питає `doc.DocumentSheet` (склад, зафіксований при створенні
    /// документа), а не `wf.ApprovalState` (історію подань): аркуш, який іще
    /// НІКОЛИ не подавали, не має рядка стану, і саме тому подання
    /// неіснуючого аркуша минуло без жодної відмови — перевіряти було
    /// нічим (директива №09 §6.4, `S-17`).
    /// </remarks>
    public Task<bool> HasSheetAsync(long documentId, int sheetDefId, CancellationToken ct);

    /// <summary>Проєкт документа; <c>null</c> — документа немає.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Окремий метод, а не <see cref="FindAsync"/>: подання і затвердження
    /// потребують РІВНО проєкту — щоб знайти зрізи звітності за той самий
    /// період (<c>H-23b</c>). Тягнути заради одного числа склад аркушів і їхні
    /// стани означало б платити трьома запитами за той, що читає одну колонку.
    /// </remarks>
    public Task<int?> FindProjectIdAsync(long documentId, CancellationToken ct);

    /// <summary>Версія шаблону, за якою живе документ.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.NotFoundException">Документа немає.</exception>
    /// <remarks>
    /// ⛔ Заведений тому, що <c>SubmissionSnapshot.TemplateVersionId</c>
    /// писався НУЛЕМ (директива №09 `W8` п.5): іммутабельний зріз подання —
    /// це відповідь на питання «за якою структурою це подавали», і без версії
    /// він на нього не відповідає. Нуль при цьому не помітний нізвідки: він
    /// виглядає як значення, і виявиться неправдою лише тоді, коли зріз
    /// знадобиться — тобто через рік, при звірці.
    ///
    /// ⚠ Один запит через ланцюг документ → проєкт: версія живе на ПРОЄКТІ,
    /// а не на документі (той самий ланцюг, що в
    /// <c>IRowStore.ResolveTableInstanceAsync</c>).
    /// </remarks>
    public Task<int> GetTemplateVersionIdAsync(long documentId, CancellationToken ct);

    /// <summary>
    /// Фіксує зміну документа: <c>ModifiedAt</c> і <c>ModifiedByUserId</c>.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="userId">Хто змінив.</param>
    /// <param name="utcNow">Момент зміни в UTC.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ До <c>H-23d</c> <c>Document.Touch</c> не кликав НІХТО: обидві дати
    /// документа назавжди лишалися моментом створення. У переліку документів
    /// їх видно кожному, і саме тому це дорожче, ніж здається, — колонка, яка
    /// показує неправду, знецінює й сусідні, правдиві.
    ///
    /// ⚠ Зберігає не сам: «дотик» має лягти тим самим комітом, що й зміна,
    /// яка його викликала. Окремий коміт дав би документ із новою датою і без
    /// нових даних, якби транзакція далі впала.
    /// </remarks>
    public Task TouchAsync(long documentId, int userId, DateTime utcNow, CancellationToken ct);
}

/// <summary>Період, за який показувати стан аркушів; <c>null</c> — не показувати.</summary>
/// <param name="Value">Ключ періоду.</param>
public readonly record struct PeriodKeyFilter(int? Value);
