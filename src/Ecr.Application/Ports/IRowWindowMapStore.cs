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
}
