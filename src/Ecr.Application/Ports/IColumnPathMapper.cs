// src/Ecr.Application/Ports/IColumnPathMapper.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Переклад <c>ColumnDefId</c> однієї версії шаблону в Id тієї самої колонки в іншій версії — за шляхом
/// «код аркуша → код таблиці → код колонки» (C1, клон версії шаблону і ключі методологій).
/// </summary>
/// <remarks>
/// ⚠ Правила й обов'язкові входи опублікованої версії методології ключуються <c>ColumnDefId</c> тієї версії шаблону,
/// у якій їх писали, а сама версія методології незмінна й обирається ЧАСОМ, а не версією шаблону. Тому Id у сховищі
/// лишаються, а при ЧИТАННІ перекладаються на колонки версії документа. Ідентичність за кодами — та сама, що в
/// перенесенні проєкту між версіями (<c>VersionMigrationPlanner</c>) і в клоні версії.
/// </remarks>
public interface IColumnPathMapper
{
    /// <summary>Знаходить у цільовій версії колонки з тими самими шляхами, що в названих колонок.</summary>
    /// <param name="sourceColumnIds">Id колонок з правил/вимог методології (будь-якої версії шаблону).</param>
    /// <param name="targetTemplateVersionId">Версія шаблону, у якій живе документ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Для кожної колонки, що має відповідник у цільовій версії (не вилучений), — його Id; колонка цільової версії
    /// відображається сама на себе. Колонки без відповідника (шлях змінено або вилучено, невідомий Id) у результаті
    /// ВІДСУТНІ — викликач мусить обробити це явно, а не мовчки.
    /// </returns>
    public Task<IReadOnlyDictionary<int, int>> MapToVersionAsync(
        IReadOnlyCollection<int> sourceColumnIds, int targetTemplateVersionId, CancellationToken ct);

    /// <summary>Версії шаблону, яким належать названі таблиці (через аркуш).</summary>
    /// <param name="tableDefIds">Таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Таблиця → версія шаблону; невідомої таблиці в результаті немає.</returns>
    public Task<IReadOnlyDictionary<int, int>> GetTemplateVersionsOfTablesAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct);

    /// <summary>
    /// V-4: «родовід» колонок — Id ТІЄЇ САМОЇ колонки (шлях «аркуш → таблиця → колонка») в усіх версіях її шаблону
    /// плюс джерела пар <c>cfg.ColumnRename</c>, що ведуть у будь-який із них (транзитивно). Для читачів журналу
    /// <c>aud.CellChange</c>, який після переносу проєкту лишається на Id старої версії (журнал незмінний).
    /// </summary>
    /// <param name="columnIds">Колонки (зазвичай — видимі колонки поточної версії документа).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Для кожної названої колонки — непорожній перелік, що містить і її саму (невідомий Id — лише він сам).
    /// Колонка з тим самим кодом в ІНШІЙ таблиці чи аркуші в родовід не входить.
    /// </returns>
    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> LineageAsync(
        IReadOnlyCollection<int> columnIds, CancellationToken ct);
}
