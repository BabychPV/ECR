// src/Ecr.Application/Ports/ICalculationBindingStore.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>
/// Прив'язки результатів методології до колонок документа
/// (<c>cfg.CalculationBinding</c>, <c>D-69</c>).
/// </summary>
/// <remarks>
/// ⛔ Порт окремий від <see cref="IMethodologyDraftStore"/> навмисно, і межа тут
/// не за зручністю, а за ВЛАСНИКОМ. Усе, що вміє той порт, належить ВЕРСІЇ
/// методології і живе в схемі <c>calc</c>; прив'язка належить ШАБЛОНУ — вона
/// посилається на <c>cfg.ColumnDef</c>, переживає всі версії методології
/// одразу (ключ <c>MethodologyId</c>, не <c>MethodologyVersionId</c>) і
/// клонуванням версії не копіюється взагалі.
///
/// ⛔ Без цього порту прив'язку не створювало НІЩО — ні обробник, ні тест, —
/// і <c>RecalculationJob.BindingsAsync</c> тихо повертав порожній перелік:
/// перерахунок документа завершувався успіхом, не порахувавши жодного числа
/// (директива №09, <c>W6</c> §3).
/// </remarks>
public interface ICalculationBindingStore
{
    /// <summary>
    /// Прив'язка за своєю адресою — трійкою <c>UQ_CalculationBinding</c>;
    /// відстежувана, <c>null</c> — такої ще немає.
    /// </summary>
    /// <param name="columnDefId">Колонка-приймач.</param>
    /// <param name="methodologyId">Методологія-джерело.</param>
    /// <param name="outputCode">Який саме вихід методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Прив'язка або <c>null</c>.</returns>
    public Task<CalculationBinding?> FindAsync(
        int columnDefId, int methodologyId, string outputCode, CancellationToken ct);

    /// <summary>Усі прив'язки методології, включно з вимкненими.</summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Прив'язки в порядку колонки й коду виходу.</returns>
    /// <remarks>
    /// ⚠ Вимкнені теж: невидима в редакторі прив'язка — це та сама відповідь
    /// «розрахунок нічого не дав», яку неможливо пояснити.
    /// </remarks>
    public Task<IReadOnlyList<CalculationBinding>> ListAsync(
        int methodologyId, CancellationToken ct);

    /// <summary>
    /// Таблиця, якій належить колонка; <c>null</c> — колонки немає або її видалено.
    /// </summary>
    /// <param name="columnDefId">Колонка-приймач.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор таблиці або <c>null</c>.</returns>
    /// <remarks>
    /// ⛔ <c>TableDefId</c> прив'язки НЕ приймається ззовні, а виводиться тут.
    /// Обидва поля вже є в рядку, і розійтися вони можуть лише мовчки:
    /// <c>RecalculationJob.BindingsAsync</c> шукає екземпляри таблиці за
    /// <c>TableDefId</c>, а значення кладе в колонку — прив'язка з чужим
    /// <c>TableDefId</c> просто не спрацювала б, не давши жодної помилки.
    /// </remarks>
    public Task<int?> FindTableOfColumnAsync(int columnDefId, CancellationToken ct);

    /// <summary>Ставить прив'язку в чергу на вставку; зберігає <c>IUnitOfWork</c>.</summary>
    /// <param name="binding">Нова прив'язка.</param>
    public void Add(CalculationBinding binding);
}
