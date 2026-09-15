// src/Ecr.Application/Ports/IColumnDefSearchStore.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Одна знахідка пошуку колонки (директива "пошук колонки за назвою
/// замість голого ColumnDefId").
/// </summary>
/// <param name="Id">Ідентифікатор колонки — те саме значення, що йде у
/// <c>ColumnDefId</c> обов'язкових вхідних колонок і прив'язок методології.</param>
/// <param name="Code">Код колонки. Унікальний У МЕЖАХ ТАБЛИЦІ, не глобально —
/// тому в підписі результату йде РАЗОМ із кодом таблиці й аркуша.</param>
/// <param name="HeaderL10n">Заголовок колонки мовами каталогу.</param>
/// <param name="TableDefId">Таблиця колонки.</param>
/// <param name="TableCode">Код таблиці — для підпису результату.</param>
/// <param name="SheetDefId">Аркуш таблиці.</param>
/// <param name="SheetCode">Код аркуша — для підпису результату.</param>
/// <param name="TemplateVersionId">Версія шаблону, якій належить аркуш.</param>
public sealed record ColumnDefSearchResult(
    int Id,
    string Code,
    LocalizedText HeaderL10n,
    int TableDefId,
    string TableCode,
    int SheetDefId,
    string SheetCode,
    int TemplateVersionId);

/// <summary>
/// Пошук колонок за назвою чи кодом — поза межами однієї таблиці (директива
/// "пошук колонки за назвою замість голого ColumnDefId").
/// </summary>
/// <remarks>
/// ⛔ З'явився тому, що <c>MethodologyRequiredInputsPanel</c> і
/// <c>MethodologyBindingsPanel</c> приймали <c>ColumnDefId</c> голим числом
/// у <c>NumberInput</c> — адміністратор мав пам'ятати внутрішній ідентифікатор
/// напам'ять, на відміну від решти полів методології (наприклад, вибір
/// одиниці виміру в Constants — searchable). Прив'язка методології не
/// прив'язана до ОДНІЄЇ таблиці (<c>TableDefId</c> виводиться із самої
/// колонки — <c>SaveCalculationBindingHandler</c>), тому пошук — наскрізний
/// по всіх версіях шаблонів, а не по одній таблиці.
/// </remarks>
public interface IColumnDefSearchStore
{
    /// <summary>Шукає колонки за підрядком коду чи будь-якого перекладу заголовка.</summary>
    /// <param name="query"><c>null</c> або порожній — без фільтра, перші за кодом.</param>
    /// <param name="limit">Стеля кількості результатів.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<ColumnDefSearchResult>> SearchAsync(
        string? query, int limit, CancellationToken ct);
}
