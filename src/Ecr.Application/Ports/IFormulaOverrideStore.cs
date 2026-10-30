using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// ✎ D-461: «ручне значення замість формули» — прапорець формули шаблону (<c>cfg.FormulaDef.AllowManualOverride</c>)
/// і позначки комірок документа (<c>doc.CellFormulaOverride</c>).
/// </summary>
/// <remarks>
/// ⚠ Записи (<see cref="ApplyAsync"/>, <see cref="SetAllowManualOverrideAsync"/>) йдуть у ПОТОЧНІЙ транзакції
/// контексту викликача: позначка, значення й журнал комітяться разом.
/// </remarks>
public interface IFormulaOverrideStore
{
    /// <summary>Формула версії шаблону; <c>null</c> — у версії такої немає (або вона видалена).</summary>
    public Task<FormulaOverrideTarget?> FindFormulaAsync(int templateVersionId, int formulaId, CancellationToken ct);

    /// <summary>Ставить прапорець «Дозволити ручне значення» — і в опублікованій версії (без нової версії).</summary>
    public Task SetAllowManualOverrideAsync(int formulaId, bool allow, CancellationToken ct);

    /// <summary>Скільки комірок (і в скількох документах) зараз мають ручне значення замість цієї формули.</summary>
    public Task<FormulaOverrideCounts> CountAsync(FormulaOverrideTarget target, CancellationToken ct);

    /// <summary>Чи є позначки ручних значень у документах версії шаблону (EXISTS; кешує викликач на ревізію знімка).</summary>
    public Task<bool> AnyForVersionAsync(int templateVersionId, CancellationToken ct);

    /// <summary>Позначки екземпляра таблиці (лише наявних рядків) з іменем автора — для підказки в сітці.</summary>
    public Task<IReadOnlyList<FormulaOverrideMark>> ReadInstanceAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Позначки на адресах (запис батчу: що було ручним до правки).</summary>
    public Task<IReadOnlyDictionary<CellAddress, CellFormulaOverride>> ReadAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct);

    /// <summary>Адреси з ручним значенням у екземплярах таблиць одного періоду (перерахунок документа).</summary>
    public Task<IReadOnlySet<CellAddress>> ReadKeysAsync(
        IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Блокує (до кінця поточної транзакції) і повертає позначки на адресах — ПЕРЕД записом перерахунку: правка, що
    /// ставить позначку паралельно, або вже закомічена (позначку видно), або чекає на коміт перерахунку.
    /// </summary>
    public Task<IReadOnlySet<CellAddress>> LockKeysAsync(IReadOnlyCollection<CellAddress> addresses, CancellationToken ct);

    /// <summary>Позначки всіх документів проєкту (лише наявних рядків) зі значенням комірки — перенос версії.</summary>
    public Task<IReadOnlyList<ProjectFormulaOverride>> ReadProjectAsync(int projectId, CancellationToken ct);

    /// <summary>
    /// Знімає позначки й робить комірки знову обчисленими (<c>IsCalculated = 1</c>: перерахунок перезапише їх за
    /// формулою) — у поточній транзакції. Перенос версії, коли формула цільової версії ручного значення не дозволяє.
    /// </summary>
    public Task ReleaseAsync(IReadOnlyList<CellAddress> addresses, CancellationToken ct);

    /// <summary>Ставить/оновлює позначки й знімає їх — у поточній транзакції.</summary>
    public Task ApplyAsync(
        IReadOnlyList<CellFormulaOverride> upserts, IReadOnlyList<CellAddress> removals, CancellationToken ct);
}

/// <summary>Формула шаблону з погляду «ручного значення»: де вона рахує і чи дозволяє ручне значення.</summary>
/// <param name="FormulaId">Формула.</param>
/// <param name="TemplateVersionId">Версія шаблону.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="Scope">Область (Column/Row/Cell).</param>
/// <param name="ColumnDefId">Колонка цілі; <c>null</c> — формула рядка «у всіх колонках».</param>
/// <param name="RowKey">Ключ рядка цілі (Row/Cell); <c>null</c> — формула колонки.</param>
/// <param name="AllowManualOverride">Прапорець зараз.</param>
public sealed record FormulaOverrideTarget(
    int FormulaId,
    int TemplateVersionId,
    int TableDefId,
    Domain.Enums.FormulaScope Scope,
    int? ColumnDefId,
    string? RowKey,
    bool AllowManualOverride);

/// <summary>Позначка ручного значення для зрізу.</summary>
/// <param name="TableRowId">Рядок.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="Reason">Чинна причина.</param>
/// <param name="SetByUserId">Хто поставив.</param>
/// <param name="SetByDisplayName">Ім'я автора; <c>null</c> — користувача вже немає.</param>
/// <param name="SetAt">Коли (UTC).</param>
public sealed record FormulaOverrideMark(
    long TableRowId, int ColumnDefId, string Reason, int SetByUserId, string? SetByDisplayName, DateTime SetAt);

/// <summary>Позначка ручного значення в документі проєкту (перенос версії).</summary>
/// <param name="Address">Комірка.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="Value">Поточне значення текстом (для журналу); <c>null</c> — комірки немає.</param>
public sealed record ProjectFormulaOverride(CellAddress Address, long DocumentId, string RowKey, string? Value);

/// <summary>Скільки ручних значень замість формули.</summary>
/// <param name="Documents">Документів, де є хоч одне.</param>
/// <param name="Cells">Комірок усього.</param>
public sealed record FormulaOverrideCounts(int Documents, int Cells);
