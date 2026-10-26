// src/Ecr.Application/Ports/IMonthColumnRouter.cs

using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Q-17 (варіант А): власність комірок таблиць «місяці в колонках» місячного проєкту — одне джерело правди
/// на рік замість копії в кожному екземплярі періоду.
/// </summary>
/// <remarks>
/// <para>
/// Модель власності (рішення людини/координатора 26.10, <c>.sync-local/reports/Q17-DESIGN-2026-10-26.md</c>):
/// </para>
/// <list type="bullet">
/// <item>місячний стовпець <c>m</c> (<c>ColumnDef.IsMonthColumn</c>, <c>MonthNumber = m</c>) — власник
/// екземпляр періоду <c>PeriodKey(рік, m)</c>;</item>
/// <item>введений не-місячний стовпець — власник «якір року»: найменший ключ календаря проєкту
/// (<c>doc.Period</c>) у цьому році;</item>
/// <item>обчислювані стовпці (<c>ColumnDef.IsComputed</c>) і комірки з <c>IsCalculated = 1</c> — завжди
/// в екземплярі перегляду.</item>
/// </list>
/// <para>
/// Застосовується лише коли разом: прапор <c>Collection:MonthColumnRouting</c> увімкнено, проєкт
/// <c>Monthly</c>, таблиця <c>LayoutKind = MonthsInColumns</c>, і потрібний період має рядок у <c>doc.Period</c>.
/// Нема Period-рядка (<c>ECR-PRD-0422</c>) → власник — екземпляр перегляду (старе зберігання).
/// </para>
/// <para>
/// ⛔ Прапор за замовчуванням ВИМКНЕНО: тоді жоден метод не ходить до бази, а
/// <see cref="GetDocumentAsync"/> і <see cref="ResolveInstancesAsync"/> повертають «не застосовується».
/// </para>
/// </remarks>
public interface IMonthColumnRouter
{
    /// <summary>Прапор <c>Collection:MonthColumnRouting</c>. Вимкнено — решта методів нічого не роблять.</summary>
    public bool Enabled { get; }

    /// <summary>Таблиця підпадає під маршрутизацію: <c>LayoutKind = MonthsInColumns</c> і не видалена.</summary>
    /// <param name="table">Опис таблиці зі знімка метаданих.</param>
    public bool AppliesToTable(TableDef table);

    /// <summary>
    /// Документ місячного проєкту, до якого маршрутизація застосовна; <c>null</c> — прапор вимкнено, проєкт не
    /// <c>Monthly</c> або документа немає. Результат кешується на життя роутера (scope).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<MonthRoutingDocument?> GetDocumentAsync(long documentId, CancellationToken ct);

    /// <summary>
    /// Екземпляри таблиць (ОДНИМ запитом), до яких маршрутизація застосовна, — за ідентифікатором. Екземпляр, якого
    /// немає серед гарячих (архів, неіснуючий), або з не місячного проєкту, у результат не потрапляє.
    /// </summary>
    /// <param name="tableInstanceIds">Ідентифікатори екземплярів.</param>
    /// <param name="periodKey">Період екземплярів, якщо він відомий викликачеві (ключ партиції); інакше <c>null</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Вимкнений прапор — порожній результат БЕЗ звернення до бази. Ідентичність екземпляра незмінна, тому відповідь (і «немає») кешується на scope.</remarks>
    public Task<IReadOnlyDictionary<long, MonthRoutingInstance>> ResolveInstancesAsync(
        IReadOnlyCollection<long> tableInstanceIds, PeriodKey? periodKey, CancellationToken ct);

    /// <summary>
    /// «Якір року»: найменший ключ календаря проєкту в році; <c>null</c> — у році немає жодного рядка періоду.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="year">Рік.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Календар року читається одним запитом і кешується на scope.</remarks>
    public Task<PeriodKey?> AnchorOfAsync(int projectId, int year, CancellationToken ct);

    /// <summary>
    /// Період-власник введеної комірки колонки <paramref name="column"/> для екземпляра перегляду.
    /// </summary>
    /// <param name="document">Документ (див. <see cref="GetDocumentAsync"/>).</param>
    /// <param name="table">Таблиця колонки.</param>
    /// <param name="column">Колонка.</param>
    /// <param name="viewPeriod">Період екземпляра перегляду.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Місячний <c>m</c> → <c>PeriodKey(рік, m)</c>; введений не-місячний → якір року; обчислюваний, таблиця поза
    /// маршрутизацією, перегляд поза календарем або власника немає в календарі → <paramref name="viewPeriod"/>.
    /// </returns>
    public Task<PeriodKey> OwnerPeriodAsync(
        MonthRoutingDocument document, TableDef table, ColumnDef column, PeriodKey viewPeriod, CancellationToken ct);

    /// <summary>Чи має колонка власника, відмінного від екземпляра перегляду (<c>OwnerPeriod != viewPeriod</c>).</summary>
    /// <param name="document">Документ.</param>
    /// <param name="table">Таблиця колонки.</param>
    /// <param name="column">Колонка.</param>
    /// <param name="viewPeriod">Період екземпляра перегляду.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<bool> HasForeignOwnerAsync(
        MonthRoutingDocument document, TableDef table, ColumnDef column, PeriodKey viewPeriod, CancellationToken ct);

    /// <summary>Усі колонки таблиці з чужим власником для екземпляра перегляду, з періодами-власниками.</summary>
    /// <param name="document">Документ.</param>
    /// <param name="table">Таблиця.</param>
    /// <param name="viewPeriod">Період екземпляра перегляду.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Порожній перелік — таблиця не платить нічого понад звичайне читання.</remarks>
    public Task<IReadOnlyList<ForeignColumn>> ForeignColumnsAsync(
        MonthRoutingDocument document, TableDef table, PeriodKey viewPeriod, CancellationToken ct);
}

/// <summary>Документ місячного проєкту, до якого застосовна маршрутизація.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="ProjectId">Проєкт (календар періодів — за ним).</param>
/// <param name="TemplateVersionId">Версія шаблону проєкту (знімок метаданих).</param>
public sealed record MonthRoutingDocument(long DocumentId, int ProjectId, int TemplateVersionId);

/// <summary>Екземпляр таблиці, до якого застосовна маршрутизація.</summary>
/// <param name="TableInstanceId">Екземпляр.</param>
/// <param name="Document">Документ екземпляра.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="PeriodKey">Період екземпляра (він же період перегляду).</param>
public sealed record MonthRoutingInstance(
    long TableInstanceId, MonthRoutingDocument Document, int TableDefId, PeriodKey PeriodKey);

/// <summary>Колонка з чужим власником і період цього власника.</summary>
/// <param name="Column">Колонка.</param>
/// <param name="OwnerPeriod">Період екземпляра-власника.</param>
public sealed record ForeignColumn(ColumnDef Column, PeriodKey OwnerPeriod);
