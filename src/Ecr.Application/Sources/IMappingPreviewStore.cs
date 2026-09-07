// src/Ecr.Application/Sources/IMappingPreviewStore.cs

namespace Ecr.Application.Sources;

/// <summary>
/// Матеріал для перегляду мапінгу на реальних рядках джерела (<c>ФВ-13.14</c>).
/// </summary>
/// <remarks>
/// ⛔ Порт віддає **сирий матеріал**, а не готовий перегляд. Уся логіка —
/// згортання точок і пошук розривів — лежить у застосунку і перевіряється без
/// бази. Якби класифікацію розривів робив SQL, її неможливо було б перевірити
/// інакше ніж прогоном на живій схемі, а саме ця логіка і є вимогою.
/// <para>
/// ⚠ Реальні рядки беруться з <c>ext.RawDataPoint</c> — того, що джерело вже
/// віддало. Читання «наживо, до першого збору» тут навмисно немає: воно
/// вимагає доступного джерела (<c>C-1</c>, <c>C-2</c>), а перегляд, який
/// падає разом із мережею, не показав би нічого саме тоді, коли потрібен.
/// </para>
/// </remarks>
public interface IMappingPreviewStore
{
    /// <summary>
    /// Читає сутність, її мапінги, реальні точки вікна і колонки цільових
    /// таблиць.
    /// </summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="fromUtc">Початок вікна, включно.</param>
    /// <param name="toUtc">Кінець вікна, виключно.</param>
    /// <param name="maxPoints">Стеля точок; перевищення позначається прапорцем.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>null</c> — сутності немає або вона вимкнена.</returns>
    public Task<MappingPreviewData?> LoadAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc, int maxPoints, CancellationToken ct);
}

/// <summary>Сирий матеріал перегляду.</summary>
/// <param name="Entity">Сутність джерела.</param>
/// <param name="Maps">Активні мапінги полів цієї сутності.</param>
/// <param name="Points">Реальні точки вікна, за зростанням мітки часу.</param>
/// <param name="IsTruncated">Точок у вікні більше за стелю: згортання неповне.</param>
/// <param name="Columns">Колонки таблиць, у які цілить хоча б один мапінг.</param>
public sealed record MappingPreviewData(
    SourceEntityRef Entity,
    IReadOnlyList<FieldMapRef> Maps,
    IReadOnlyList<RawPointRef> Points,
    bool IsTruncated,
    IReadOnlyList<TargetColumnRef> Columns);

/// <summary>Сутність джерела в перегляді.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код у джерелі.</param>
/// <param name="DisplayName">Підпис із каталогу джерела.</param>
public sealed record SourceEntityRef(int Id, string Code, string? DisplayName);

/// <summary>
/// Мапінг поля разом із тим, що застали на його цілі.
/// </summary>
/// <remarks>
/// ⚠ <paramref name="TargetColumnExists"/> — окреме поле, а не
/// <c>TargetColumnCode is null</c>: мапінг на **видалену** колонку і мапінг
/// без колонки взагалі — різні дефекти, і лікуються вони по-різному.
/// </remarks>
/// <param name="Id">Ідентифікатор мапінгу.</param>
/// <param name="SourceField">Поле в джерелі; воно ж <c>SourcePath</c> точки.</param>
/// <param name="TargetRowKey">Рядок-адресат; <c>null</c> — не матеріалізується.</param>
/// <param name="TargetColumnDefId">Колонка-адресат.</param>
/// <param name="TargetColumnCode">Код колонки; <c>null</c> — колонки не знайдено.</param>
/// <param name="TargetColumnExists">Чи колонка ще існує і не видалена.</param>
/// <param name="Aggregation">Спосіб згортання точок періоду.</param>
/// <param name="SourceUnitCode">Одиниця джерела, оголошена в мапінгу.</param>
/// <param name="TargetUnitCode">Одиниця, в якій значення лягає в ECR.</param>
public sealed record FieldMapRef(
    int Id,
    string SourceField,
    string? TargetRowKey,
    int? TargetColumnDefId,
    string? TargetColumnCode,
    bool TargetColumnExists,
    string? Aggregation,
    string? SourceUnitCode,
    string? TargetUnitCode);

/// <summary>Реальний рядок джерела — точка <c>ext.RawDataPoint</c> як є.</summary>
/// <param name="SourcePath">Шлях атрибута в джерелі.</param>
/// <param name="Timestamp">Мітка часу точки.</param>
/// <param name="ValueNumeric">Число в одиниці ДЖЕРЕЛА (<c>ФВ-16.10</c>).</param>
/// <param name="ValueString">Текст для нечислових атрибутів.</param>
/// <param name="Quality">Якість за класифікацією джерела.</param>
public sealed record RawPointRef(
    string SourcePath,
    DateTime Timestamp,
    decimal? ValueNumeric,
    string? ValueString,
    string? Quality);

/// <summary>
/// Колонка цільової таблиці разом із тим, чи стоїть за нею хоч щось.
/// </summary>
/// <remarks>
/// ⛔ Три джерела наповнення перевіряються **разом**: мапінг інтеграції,
/// прив'язка результату методології (<c>D-69</c>) і формула шаблону. Порожня
/// перевірка по одному з них оголосила б розривом кожну обчислювану колонку —
/// і перелік розривів перестали б читати.
/// </remarks>
/// <param name="TableDefId">Таблиця, якій належить колонка.</param>
/// <param name="ColumnDefId">Ідентифікатор колонки.</param>
/// <param name="Code">Код колонки.</param>
/// <param name="Header">Заголовок колонки мовою відповіді.</param>
/// <param name="IsReadOnly">Ручний ввід заборонений.</param>
/// <param name="IsComputed">Значення обчислює система.</param>
/// <param name="IsRequired">Колонка обов'язкова до заповнення.</param>
/// <param name="HasFieldMap">У колонку цілить матеріалізований мапінг.</param>
/// <param name="HasCalculationBinding">До колонки прив'язаний вихід методології.</param>
/// <param name="HasFormula">Колонку рахує формула шаблону.</param>
public sealed record TargetColumnRef(
    int TableDefId,
    int ColumnDefId,
    string Code,
    string? Header,
    bool IsReadOnly,
    bool IsComputed,
    bool IsRequired,
    bool HasFieldMap,
    bool HasCalculationBinding,
    bool HasFormula);
