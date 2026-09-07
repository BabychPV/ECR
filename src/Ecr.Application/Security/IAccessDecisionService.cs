// src/Ecr.Application/Security/IAccessDecisionService.cs

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>
/// Єдина точка рішень про доступ. Поєднує RBAC, стан періоду, правила періодів
/// шаблону, статус документа, структурні і бізнес-обмеження (ФВ-6.8).
/// </summary>
/// <summary>Крок маршруту погодження, якого чекає аркуш.</summary>
/// <param name="StepId">Ідентифікатор кроку.</param>
/// <param name="Ordinal">Порядковий номер кроку в маршруті, від 1.</param>
/// <param name="RoleId">Роль, яка затверджує на цьому кроці.</param>
/// <param name="NextStepId">Наступний крок; <c>null</c> — цей останній.</param>
/// <param name="TotalSteps">Скільки кроків у маршруті — для підпису «крок 2 з 3».</param>
public sealed record ApprovalStepView(
    int StepId, int Ordinal, int RoleId, int? NextStepId, int TotalSteps);

/// <summary>Доступ до рядка, якого ще немає.</summary>
/// <param name="Row">
/// Рішення без огляду на колонку: період, проєкт, аркуш, вікно доступу, грант
/// на рівні проєкту / аркуша / таблиці.
/// </param>
/// <param name="Columns">
/// Рішення по кожній колонці таблиці для цього рядка. Порожньо, коли в
/// таблиці немає жодної колонки.
/// </param>
public sealed record NewRowAccess(
    EditDecision Row, IReadOnlyDictionary<int, EditDecision> Columns);

public interface IAccessDecisionService
{
    /// <summary>Будує профіль прав користувача. Викликається раз на сесію.</summary>
    public Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct);

    /// <summary>Чи може користувач читати документ.</summary>
    public Task<EditDecision> CanReadDocumentAsync(AccessProfile profile, long documentId, CancellationToken ct);

    /// <summary>Чи може користувач редагувати конкретну комірку.</summary>
    public Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct);

    /// <summary>
    /// Пакетна перевірка для відкриття таблиці: повертає рішення на кожну
    /// комірку зрізу одним проходом. Поштучний виклик <see cref="CanEditCellAsync"/>
    /// у циклі — антипатерн і не вкладається в бюджет.
    /// </summary>
    public Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct);

    /// <summary>
    /// Рішення для рядків, яких у зрізі ще <b>немає</b> — тобто для створення.
    /// </summary>
    /// <param name="profile">Профіль прав користувача.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці, куди додається рядок.</param>
    /// <param name="rowKeys">Ключі рядків, які збираються створити.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ключ рядка → рішення на рядок і на кожну колонку.</returns>
    /// <remarks>
    /// ⛔ Окремий метод потрібен тому, що <see cref="CanEditSliceAsync"/>
    /// принципово не може відповісти на це питання: він ключує рішення
    /// <c>CellAddress</c>, у якій є <c>RowId</c>, а в рядка, якого ще немає,
    /// його немає. Саме через це створення обходило модель доступу цілком:
    /// адрес не збиралося, перевірка пропускалася, і запис у ЗАКРИТИЙ період
    /// проходив із кодом <c>200</c>.
    ///
    /// ⚠ Рішення на рядок і на колонки — різні питання, і потрібні обидва.
    /// «Чи можна тут писати взагалі» (період, проєкт, аркуш, вікно доступу)
    /// не залежить від колонки, і саме його питає <c>CreateRowHandler</c>: у
    /// момент створення рядка колонок ще ніхто не назвав. А от гранти бувають
    /// на колонку (<c>ResourceKind.Column</c>), тож пакетний запис у щойно
    /// створений рядок мусить звірятися по кожній адресі окремо.
    ///
    /// ⚠ Один виклик на ВЕСЬ батч, як і у зрізу: бюджет прав — 50 мс
    /// (<c>ФВ-6.10</c>), і виклик на рядок його не витримає.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, NewRowAccess>> CanCreateRowsAsync(
        AccessProfile profile, long tableInstanceId, IReadOnlyCollection<string> rowKeys, CancellationToken ct);

    /// <summary>Чи може користувач подати аркуш за період на затвердження.</summary>
    public Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Чи може користувач затвердити аркуш за період.</summary>
    public Task<EditDecision> CanApproveAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Крок маршруту погодження, якого чекає аркуш (<c>ФВ-5.17</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Повертає <c>null</c>, коли маршруту немає — і це нормальний,
    /// найчастіший стан: затвердження одноетапне, як було до маршрутів.
    /// </remarks>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<ApprovalStepView?> CurrentApprovalStepAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);
}
