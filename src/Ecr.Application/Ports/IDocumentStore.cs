using Ecr.Application.Common;
using Ecr.Domain.Entities.Documents;

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
public sealed record DocumentSummary(
    long Id,
    int ProjectId,
    string BusinessKey,
    DateTime CreatedAt,
    int SheetCount,
    IReadOnlyDictionary<string, string> SheetStates);

/// <summary>Порушення правила складу документа.</summary>
/// <param name="SheetGroup">Група аркушів.</param>
/// <param name="RuleKind">Вид правила: 0 <c>RequiresAll</c>, 1 <c>RequiresOne</c>, 2 <c>Optional</c>.</param>
/// <param name="Detail">Що саме не так.</param>
public sealed record CompositionViolation(string SheetGroup, byte RuleKind, string Detail);

/// <summary>Читання і створення документів.</summary>
public interface IDocumentStore
{
    /// <summary>Додає документ разом зі складом аркушів.</summary>
    public Task AddAsync(Document document, CancellationToken ct);

    /// <summary>Документ за ідентифікатором; <c>null</c> — не існує.</summary>
    public Task<DocumentSummary?> FindAsync(long documentId, PeriodKeyFilter period, CancellationToken ct);

    /// <summary>Сторінка документів проєкту.</summary>
    public Task<PagedResult<DocumentSummary>> ListAsync(
        int? projectId, PeriodKeyFilter period, CursorRequest page, CancellationToken ct);

    /// <summary>
    /// Перевіряє склад за <c>SheetGroupRule</c> (ФВ-3.2).
    /// </summary>
    /// <returns>Порожній перелік — склад коректний.</returns>
    public Task<IReadOnlyList<CompositionViolation>> ValidateCompositionAsync(
        int templateVersionId, IReadOnlyList<int> sheetDefIds, CancellationToken ct);

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
