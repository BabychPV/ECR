// src/Ecr.Application/Ports/ISourceEventMapStore.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>Таблиця-ціль мапінгу подій разом з версією шаблону, якій вона належить.</summary>
/// <param name="Table">Таблиця.</param>
/// <param name="TemplateVersionId">Версія шаблону (через аркуш).</param>
public sealed record EventMapTableInfo(TableDef Table, int TemplateVersionId);

/// <summary>Документ-адресат мапінгу подій.</summary>
/// <param name="ProjectId">Проєкт документа.</param>
/// <param name="TemplateVersionId">Версія шаблону проєкту.</param>
public sealed record EventMapDocumentInfo(int ProjectId, int TemplateVersionId);

/// <summary>Фільтр таблиці подій (HSE301 A6, FEATURE-HSE301-VIEW §4.7).</summary>
/// <param name="MapIds">Мапінги, які користувач бачить; порожньо — нічого.</param>
/// <param name="Statuses">Лише ці стани; порожньо — усі.</param>
/// <param name="FromUtc">Початок події не раніше (включно); <c>null</c> — без межі.</param>
/// <param name="ToUtc">Початок події раніше (виключно); <c>null</c> — без межі.</param>
/// <param name="PeriodKey">Лише цей період; <c>null</c> — усі.</param>
public sealed record SourceEventLinkFilter(
    IReadOnlyCollection<int> MapIds,
    IReadOnlyCollection<SourceEventLinkStatus> Statuses,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int? PeriodKey);

/// <summary>Рядок таблиці подій із даними мапінгу й документа, потрібними для показу.</summary>
/// <param name="Link">Зв'язок «подія ↔ рядок».</param>
/// <param name="DocumentId">Документ мапінгу.</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="ProjectId">Проєкт документа.</param>
/// <param name="TimeZoneId">Пояс проєкту.</param>
public sealed record SourceEventLinkRow(
    SourceEventLink Link, long DocumentId, string DocumentKey, int ProjectId, string TimeZoneId);

/// <summary>Сторінка таблиці подій.</summary>
/// <param name="Rows">Рядки (на один більше за ліміт, якщо є наступна сторінка).</param>
/// <param name="TotalCount">Скільки подій під фільтром.</param>
public sealed record SourceEventLinkPage(IReadOnlyList<SourceEventLinkRow> Rows, int TotalCount);

/// <summary>
/// Сховище мапінгу подій джерела (<c>ext.SourceEvent*</c>) для API налаштування й таблиці подій
/// (HSE301 A6, FEATURE-HSE301-VIEW §4.7).
/// </summary>
public interface ISourceEventMapStore
{
    /// <summary>Таблиця й версія її шаблону; <c>null</c> — таблиці немає або її видалено.</summary>
    public Task<EventMapTableInfo?> FindTargetTableAsync(int tableDefId, CancellationToken ct);

    /// <summary>Документ і версія шаблону його проєкту; <c>null</c> — документа немає.</summary>
    public Task<EventMapDocumentInfo?> FindDocumentAsync(long documentId, CancellationToken ct);

    /// <summary>Колонки за ідентифікаторами (без видалених); відсутні в словнику не повертаються.</summary>
    public Task<IReadOnlyDictionary<int, ColumnDef>> FindColumnsAsync(IReadOnlyCollection<int> ids, CancellationToken ct);

    /// <summary>Довідники записів: ідентифікатор запису → довідник. Видалені записи не повертаються.</summary>
    public Task<IReadOnlyDictionary<long, int>> FindRegistryEntryDefsAsync(
        IReadOnlyCollection<long> entryIds, CancellationToken ct);

    /// <summary>Мапінг з полями й відповідностями значень — відстежуваний; <c>null</c> — немає.</summary>
    public Task<SourceEventMap?> FindMapAsync(int id, CancellationToken ct);

    /// <summary>Мапінги (без відстеження): усі або лише сутності.</summary>
    public Task<IReadOnlyList<SourceEventMap>> ListMapsAsync(int? sourceEntityId, CancellationToken ct);

    /// <summary>Чи є вже мапінг цієї трійки «сутність, документ, таблиця» (<c>UQ_SourceEventMap</c>).</summary>
    public Task<bool> MapExistsAsync(int sourceEntityId, long documentId, int tableDefId, CancellationToken ct);

    /// <summary>Заводить мапінг і повертає його зі присвоєним <c>Id</c>.</summary>
    public Task<SourceEventMap> AddMapAsync(SourceEventMap map, CancellationToken ct);

    /// <summary>Зберігає зміни відстежуваного мапінгу.</summary>
    public Task SaveAsync(CancellationToken ct);

    /// <summary>Скільки зв'язків «подія ↔ рядок» має мапінг.</summary>
    public Task<int> CountLinksAsync(int mapId, CancellationToken ct);

    /// <summary>
    /// Позначає всі поля мапінгу й їхні відповідності до видалення при наступному <see cref="SaveAsync"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ Обов'язково ПЕРЕД <see cref="SourceEventMap.ReplaceFields"/>: усі зовнішні ключі моделі — <c>Restrict</c>
    /// (без каскаду в базі й у EF), тож просте очищення колекції EF відхиляє як «розірваний обов'язковий зв'язок».
    /// </remarks>
    public void ReleaseFields(SourceEventMap map);

    /// <summary>
    /// Позначає сам мапінг зміненим, щоб наступний <see cref="SaveAsync"/> підняв його <c>RowVersion</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ AN-40 / L9-06: правка лише полів чи відповідностей (дочірні таблиці) рядок <c>ext.SourceEventMap</c> не
    /// змінює, тож без цієї позначки версія лишалася б старою — і друга правка з тим самим <c>rowVersion</c> пройшла
    /// б перевірку, затерши першу.
    /// </remarks>
    public void MarkChanged(SourceEventMap map);

    /// <summary>Видаляє мапінг разом з полями й відповідностями значень.</summary>
    public Task RemoveMapAsync(SourceEventMap map, CancellationToken ct);

    /// <summary>Чи має сутність хоч один активний мапінг подій.</summary>
    public Task<bool> HasActiveMapAsync(int sourceEntityId, CancellationToken ct);

    /// <summary>Сторінка зв'язків: новіші за початком події першими; курсор — непрозорий.</summary>
    public Task<SourceEventLinkPage> ReadLinksAsync(
        SourceEventLinkFilter filter, string? cursor, int limit, CancellationToken ct);

    /// <summary>Курсор наступної сторінки після рядка.</summary>
    public string NextCursor(SourceEventLink last);
}
