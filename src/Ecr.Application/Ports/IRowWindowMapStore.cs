// src/Ecr.Application/Ports/IRowWindowMapStore.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;

namespace Ecr.Application.Ports;

/// <summary>
/// Сховище прив'язок «атрибут PI → колонка, вікно = рядок» (<c>ext.RowWindowMap</c> з джерелами) для API
/// налаштування (HSE301 A1, FEATURE-HSE301-VIEW §4.4).
/// </summary>
public interface IRowWindowMapStore
{
    /// <summary>Колонки за ідентифікаторами (без видалених); відсутні в словнику не повертаються.</summary>
    public Task<IReadOnlyDictionary<int, ColumnDef>> FindColumnsAsync(IReadOnlyCollection<int> ids, CancellationToken ct);

    /// <summary>Прив'язка з джерелами — відстежувана; <c>null</c> — немає.</summary>
    public Task<RowWindowMap?> FindMapAsync(int id, CancellationToken ct);

    /// <summary>Прив'язки (без відстеження): за таблицею й/або сутністю джерела; <c>null</c> — без обмеження.</summary>
    public Task<IReadOnlyList<RowWindowMap>> ListMapsAsync(int? tableDefId, int? sourceEntityId, CancellationToken ct);

    /// <summary>Чи вже є прив'язка на цю колонку-ціль (<c>UQ_RowWindowMap_Target</c>).</summary>
    public Task<bool> TargetTakenAsync(int tableDefId, int targetColumnDefId, CancellationToken ct);

    /// <summary>Заводить прив'язку й повертає її з присвоєним <c>Id</c> (джерела додаються до збереження).</summary>
    public Task<RowWindowMap> AddMapAsync(RowWindowMap map, CancellationToken ct);

    /// <summary>Зберігає зміни відстежуваної прив'язки.</summary>
    public Task SaveAsync(CancellationToken ct);

    /// <summary>
    /// Позначає всі джерела прив'язки до видалення при наступному <see cref="SaveAsync"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ ПЕРЕД <see cref="RowWindowMap.ClearSources"/> і повторним <see cref="RowWindowMap.AddSource"/>: ключ
    /// <c>FK_RWS_Map</c> обов'язковий, тож просте очищення колекції EF відхиляє як «розірваний зв'язок».
    /// </remarks>
    public void ReleaseSources(RowWindowMap map);

    /// <summary>Скільки записів провенансу (<c>ext.RowWindowValue</c>) має прив'язка.</summary>
    public Task<int> CountValuesAsync(int mapId, CancellationToken ct);

    /// <summary>Видаляє прив'язку разом із джерелами.</summary>
    public Task RemoveMapAsync(RowWindowMap map, CancellationToken ct);

    /// <summary>
    /// Екземпляри таблиці в періодах <c>Open</c>/<c>Grace</c> — адресати підтягування після того, як прив'язку
    /// завели, змінили чи відновили (аудит I1-02); не більше <paramref name="limit"/>, за зростанням Id.
    /// </summary>
    /// <param name="tableDefId">Таблиця прив'язки.</param>
    /// <param name="limit">Стеля екземплярів.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<RowWindowFetchRequest>> OpenInstancesAsync(int tableDefId, int limit, CancellationToken ct);

    /// <summary>
    /// Знімає чинність із підтягнутих (<c>Fetched</c>/<c>Partial</c>) записів провенансу прив'язки за атрибутом
    /// у названих екземплярах — після зміни конфігурації згортки, якої провенанс не містить (X3-04).
    /// </summary>
    /// <param name="rowWindowMapId">Прив'язка.</param>
    /// <param name="instances">Екземпляри (відкриті періоди).</param>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourceField">Шлях атрибута.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки записів знято.</returns>
    /// <remarks>
    /// ⚠ <c>NotApplicable</c>/<c>InvalidWindow</c>/<c>KeptManual</c>/<c>NoData</c>/<c>SourceError</c> не знімаються:
    /// від конфігурації згортки вони не залежать (чи повторюються за своєю політикою), а <c>KeptManual</c> — рішення людини.
    /// </remarks>
    public Task<int> SupersedeFoldedValuesAsync(
        int rowWindowMapId, IReadOnlyList<RowWindowFetchRequest> instances, int sourceEntityId, string sourceField, CancellationToken ct);
}
