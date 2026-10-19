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
/// перегляд імпорту): збій дозволу не повинен ламати відкриття документа. Дозволу немає - екземпляр лишається без
/// рядків і з попередженням, виняток не кидається.
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
}
