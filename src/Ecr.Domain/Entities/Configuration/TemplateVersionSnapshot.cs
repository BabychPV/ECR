namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Незмінний знімок структури версії для кешу метаданих. Будується один раз
/// і живе під ключем <c>v{id}:r{rev}</c> — інвалідація не потрібна (D-16).
/// </summary>
/// <param name="TemplateVersionId">Версія шаблону.</param>
/// <param name="PresentationRevision">Ревізія презентаційного шару.</param>
/// <param name="Sheets">Аркуші з таблицями, колонками і рядками.</param>
/// <param name="ColumnsById">Плоский індекс колонок для швидкого доступу.</param>
/// <param name="RowsByKey">Індекс рядків: <c>(TableDefId, RowKey)</c> → <see cref="RowDef"/>.</param>
public sealed record TemplateVersionSnapshot(
    int TemplateVersionId,
    int PresentationRevision,
    IReadOnlyList<SheetDef> Sheets,
    IReadOnlyDictionary<int, ColumnDef> ColumnsById,
    IReadOnlyDictionary<(int TableDefId, string RowKey), RowDef> RowsByKey)
{
    /// <summary>Ключ кешу.</summary>
    public string CacheKey => $"v{TemplateVersionId}:r{PresentationRevision}";

    /// <summary>
    /// Поля шапки документа (рівень версії, не таблиці); порожній список —
    /// нормальний стан для версій без визначеної шапки.
    /// </summary>
    public IReadOnlyList<HeaderFieldDef> HeaderFields { get; init; } = [];

    /// <summary>
    /// D-230: у версії є активний зв'язок виду Rollup або Check. Прапор їде проєкцією в наявному запиті
    /// аркушів (<c>MetadataCache.LoadAsync</c>) — нового звернення до бази він не додає; шлях без таких
    /// зв'язків нічого зв'язкового не читає.
    /// </summary>
    public bool HasActiveRollupOrCheck { get; init; }

    /// <summary>
    /// PM-11c: у версії є чинна (<c>IsActive</c>) прив'язка таблиці аркуша до дозволу. Їде проєкцією в запиті аркушів
    /// (нового звернення до бази побудова не додає); <c>GET /documents/{id}/tables</c> без прапора не питає прив'язок
    /// узагалі. Прив'язки міняються лише у ЧЕРНЕТЦІ, і обидва записувачі (<c>SavePermitBindingHandler</c>,
    /// <c>DeletePermitBindingHandler</c>) скидають знімок після коміту - застарілого прапора немає (на іншому вузлі
    /// знімок чернетки живе не довше за вікно ревізії, як і решта структури чернетки).
    /// </summary>
    public bool HasActivePermitBindings { get; init; }

    /// <summary>
    /// ✎ LAND-YEAR L0a: псевдоніми аргументів методологій на місячні колонки (<c>cfg.MonthInputAlias</c>) — за
    /// <c>(TableDefId, MonthNumber)</c>. Вантажаться лише для версії з розкладкою <c>YearWithMonthColumns</c> (інша
    /// розкладка не платить запиту); порожньо — псевдонімів немає.
    /// </summary>
    /// <remarks>
    /// Запуск методології періоду місяця <c>m</c> додає аргумент <c>@Alias</c> зі значенням колонки псевдоніма
    /// (<c>CalculationInputBuilder</c>): після перебудови Land під рік коди селекторів (<c>Land_GasConsumption</c>) зникли,
    /// а методології читають аргументи за цими іменами.
    /// </remarks>
    public IReadOnlyDictionary<(int TableDefId, int MonthNumber), IReadOnlyList<MonthInputAlias>> MonthInputAliases { get; init; }
        = new Dictionary<(int TableDefId, int MonthNumber), IReadOnlyList<MonthInputAlias>>();

    /// <summary>Псевдоніми таблиці для місяця <paramref name="monthNumber"/>; порожньо — немає.</summary>
    /// <param name="tableDefId">Таблиця.</param>
    /// <param name="monthNumber">Місяць 1..12.</param>
    public IReadOnlyList<MonthInputAlias> MonthInputAliasesFor(int tableDefId, int monthNumber)
        => MonthInputAliases.TryGetValue((tableDefId, monthNumber), out var aliases) ? aliases : [];
}
