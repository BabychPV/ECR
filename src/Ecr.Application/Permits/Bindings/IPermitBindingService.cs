// src/Ecr.Application/Permits/Bindings/IPermitBindingService.cs
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.Bindings;

/// <summary>Підсумок створення рядків документа з дозволу.</summary>
/// <param name="Rows">Скільки рядків створено.</param>
/// <param name="Warnings">Попередження: дозволу немає, стелю рядків урізано, поле не читається тощо.</param>
public sealed record PermitMaterialization(int Rows, IReadOnlyList<PermitBindingIssue> Warnings)
{
    /// <summary>Нічого не створено, попереджень немає.</summary>
    public static PermitMaterialization None { get; } = new(0, []);
}

/// <summary>
/// Рантайм прив'язок аркуша до дозволів (Permits, PM-10): рядки й ЗАБЛОКОВАНІ ліміти таблиці документа беруться з
/// дозволу, чинного для документа.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Єдина реалізація плану з переглядом PM-09 (<see cref="PermitPlanBuilder"/>): перегляд не розходиться з тим, що
/// створює документ.
/// </para>
/// <para>
/// ⛔ Кличеться з <c>RowStore.EnsureTableInstancesAsync</c>, який викликають і шляхи ЧИТАННЯ (зріз таблиць, експорт,
/// перегляд імпорту). Збій ПЛАНУВАННЯ (вибір дозволу, читання довідників) відкриття не ламає: екземпляр лишається без
/// рядків і з попередженням <c>planFailed</c>. Дозволу немає - те саме, з попередженням <c>noPermit</c>. Збій ЗАПИСУ
/// (рядки, комірки, аудит) виходить назовні й відкочує транзакцію викликача разом із щойно створеними екземплярами.
/// </para>
/// </remarks>
public interface IPermitBindingService
{
    /// <summary>
    /// Створює рядки з дозволу для ЩОЙНО створених екземплярів таблиць: рядки <c>P&lt;id&gt;</c> і значення
    /// зіставлених колонок (походження <c>Permit</c>).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="createdTableDefIds">Описи таблиць, екземпляри яких щойно створено (і збережено) у цій транзакції.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки створено й чому щось не створено.</returns>
    /// <remarks>
    /// ⚠ Викликач тримає спільний замок структури документа й транзакцію; без прив'язки до жодної з таблиць - один
    /// дешевий запит і вихід.
    /// </remarks>
    public Task<PermitMaterialization> MaterializeAsync(
        long documentId, PeriodKey periodKey, IReadOnlyCollection<int> createdTableDefIds, CancellationToken ct);

    /// <summary>План прив'язки для документа: який дозвіл обрано й які рядки він породжує (читання, нічого не пише).</summary>
    /// <param name="view">Прив'язка.</param>
    /// <param name="documentId">Документ: з його шапки беруться об'єкт чи дозвіл.</param>
    /// <param name="periodEnd">Останній день періоду документа.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>⚠ Той самий план, що створює <see cref="MaterializeAsync"/> і показує перегляд PM-09.</remarks>
    public Task<PermitBindingPlan> PlanAsync(PermitBindingView view, long documentId, DateOnly periodEnd, CancellationToken ct);

    /// <summary>
    /// Оновлює екземпляр таблиці за планом: ДОДАЄ рядки <c>P&lt;id&gt;</c>, яких ще немає, і перезаписує ліміти/довідкові
    /// значення лише там, де остання зміна комірки - від дозволу. Рядків НЕ видаляє.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці прив'язки.</param>
    /// <param name="view">Прив'язка.</param>
    /// <param name="plan">План з <see cref="PlanAsync"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Кличеться ВСЕРЕДИНІ транзакції запису, під замком структури й аркуша (обробник перевірив права й стан).
    /// </remarks>
    public Task<PermitRefreshResult> RefreshAsync(
        long documentId, PeriodKey periodKey, long tableInstanceId, PermitBindingView view, PermitBindingPlan plan, CancellationToken ct);
}

/// <summary>Підсумок оновлення екземпляра з дозволу.</summary>
/// <param name="Added">Скільки рядків додано.</param>
/// <param name="Updated">У скількох наявних рядках змінено значення.</param>
/// <param name="RemovedFromPermit">Ключі наявних рядків <c>P&lt;id&gt;</c>, яких у дозволі більше немає (НЕ видалені).</param>
/// <param name="Warnings">Попередження.</param>
/// <param name="ChangedCells">Змінені комірки (рядок, колонка) - насіння перерахунку.</param>
public sealed record PermitRefreshResult(
    int Added,
    int Updated,
    IReadOnlyList<string> RemovedFromPermit,
    IReadOnlyList<PermitBindingIssue> Warnings,
    IReadOnlyList<(long RowId, int ColumnId)> ChangedCells);
