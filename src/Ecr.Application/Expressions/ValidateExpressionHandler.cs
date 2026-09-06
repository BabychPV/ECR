using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Expressions;

/// <summary>
/// Перевіряє один вираз — те саме, що зробить публікація, і на тих самих
/// позиціях (<c>ФВ-9.15a</c>).
/// </summary>
/// <remarks>
/// ⛔ Обробник НЕ має власного переліку перевірок: він кличе
/// <see cref="PublishChecks.CheckExpression"/> — той самий метод, яким
/// публікація перевіряє кожну формулу версії. Другий перелік розійшовся б із
/// першим, і розбіжність була б видима не як помилка, а як довіра до зеленого
/// редактора, після якого публікація відмовляє без пояснення, що змінилося.
///
/// ⚠ Позиції — **зміщення в символах від початку виразу**, нуль-базовані
/// (<c>ExpressionDiagnostic.Position</c>). Не рядок і колонка: вираз
/// однорядковий за побудовою, а перерахунок у рядок/колонку на сервері
/// означав би, що клієнт мусить повторити ту саму арифметику назад.
/// </remarks>
public sealed class ValidateExpressionHandler(
    IRepository<TemplateVersion, int> versions,
    IUnitCatalog unitCatalog,
    IFormulaEngine formulaEngine,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>
    /// Право на перевірку виразу (<c>02-contracts.md</c> §9).
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Calculation.View</c>, а не право редагування: перевірка нічого не
    /// змінює, і вимагати за неї право запису означало б, що подивитися на
    /// пояснення чужої формули не може ніхто, крім її автора.
    ///
    /// ⛔ Коли передано версію шаблону, ДОДАТКОВО вимагається
    /// <c>Template.View</c>: діагностика називає коди колонок і рядків
    /// («посилання не резолвиться», «колонка обов'язкова»), тобто віддає
    /// структуру. Без цієї другої перевірки ендпоінт був би обхідним шляхом до
    /// структури для того, хто права на неї не має.
    /// </remarks>
    public const string Permission = "Calculation.View";

    /// <summary>Перевіряє вираз.</summary>
    /// <param name="request">Вираз, діалект і місце в структурі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Зауваження, тип результату і перелік пропущених перевірок.</returns>
    public async Task<ExpressionValidationDto> HandleAsync(
        ExpressionValidationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var diagnostics = new List<ExpressionDiagnostic>();
        var skipped = new List<string>();

        var scope = request.TemplateVersionId is { } versionId
            ? await StructuredScopeAsync(versionId, ct).ConfigureAwait(false)
            : SyntaxOnlyScope(skipped);

        var result = PublishChecks.CheckExpression(
            request.Expression,
            request.Dialect,
            new ExpressionSite(request.TableDefId, request.RowKey, request.ColumnDefId),
            scope,
            diagnostics);

        // ⛔ Названо ЗАВЖДИ, навіть за повного оточення: цикл є властивістю
        // версії, а не виразу, і жодна перевірка одного тексту на нього не
        // відповідає (`02b` §12 п. 4). Мовчання тут читалося б як «циклу
        // немає», і публікація відмовляла б після зеленого редактора.
        skipped.Add(SkippedCycle);

        if (request.TemplateVersionId is not null && request.TableDefId is null)
        {
            // Вираз ще нікуди не прив'язаний: резолвити відносні посилання
            // («[Jan]» — колонка ТОГО САМОГО рядка) немає відносно чого.
            skipped.Add(SkippedReferences);
        }

        return new ExpressionValidationDto(
            [.. diagnostics.Select(d => new DiagnosticInfo(d.Code, d.Message, d.Position, d.Length))],
            result?.Expression.ResultType.ToString(),
            [.. skipped.Distinct()]);
    }

    /// <summary>Перевірка посилань пропущена.</summary>
    public const string SkippedReferences = "References";

    /// <summary>Перевірка типів пропущена.</summary>
    public const string SkippedTypes = "Types";

    /// <summary>Перевірка одиниць пропущена.</summary>
    public const string SkippedUnits = "Units";

    /// <summary>Перевірка ациклічності неможлива для одного виразу.</summary>
    public const string SkippedCycle = "Cycle";

    /// <summary>
    /// Оточення без структури: перевіряється лише те, що не потребує версії.
    /// </summary>
    /// <remarks>
    /// ⚠ Це не «полегшений режим», а чесна межа. Синтаксис, склад функцій,
    /// кількість аргументів і заборонені в діалекті посилання перевіряються
    /// повністю; посилання, типи й одиниці — ні, і саме тому вони названі в
    /// <c>SkippedChecks</c>.
    /// </remarks>
    private ExpressionScope SyntaxOnlyScope(List<string> skipped)
    {
        skipped.Add(SkippedReferences);
        skipped.Add(SkippedTypes);
        skipped.Add(SkippedUnits);

        return new ExpressionScope(
            formulaEngine, new Dictionary<int, TableDef>(), Extractor: null,
            TypeContext: null, UnitContext: null);
    }

    /// <summary>
    /// Повне оточення: структура версії, типи колонок і каталог одиниць.
    /// </summary>
    /// <remarks>
    /// ⛔ Знімок будується з АГРЕГАТА версії (<see cref="PublishChecks.Snapshot"/>),
    /// а не з кешу метаданих. Причина тверда: редактор відкривають над
    /// ЧЕРНЕТКОЮ, а кеш тримає лише опубліковані версії — з нього чернетка не
    /// прийшла б узагалі, і перевірка мовчки працювала б над попередньою
    /// редакцією структури.
    /// </remarks>
    private async Task<ExpressionScope> StructuredScopeAsync(int templateVersionId, CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Template.View", ct)
            .ConfigureAwait(false);

        var version = await versions.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var snapshot = PublishChecks.Snapshot(version);
        var catalogue = await unitCatalog.GetAsync(ct).ConfigureAwait(false);

        var tables = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .Take(MaxTables)
            .ToDictionary(t => t.Id);

        return new ExpressionScope(
            formulaEngine,
            tables,
            new DependencyExtractor(new ReferenceResolver(snapshot), new RangeExpander()),
            new SnapshotTypeContext(snapshot),
            new SnapshotUnitContext(snapshot, catalogue));
    }

    /// <summary>
    /// Стеля таблиць у знімку.
    /// </summary>
    /// <remarks>
    /// ⚠ Не захист від зловмисника, а межа запиту: версія з тисячами таблиць
    /// означала б, що інтерактивна перевірка одного виразу читає всю структуру
    /// на кожен натиск клавіші. Чинний шаблон має 21 результатну таблицю.
    /// </remarks>
    private const int MaxTables = 2_000;
}

/// <summary>Запит на перевірку виразу.</summary>
/// <param name="Expression">Текст виразу.</param>
/// <param name="Dialect">Діалект (<c>D-113</c>).</param>
/// <param name="TemplateVersionId">
/// Версія шаблону; <c>null</c> — перевіряється лише синтаксис.
/// </param>
/// <param name="TableDefId">Таблиця, в якій живе вираз.</param>
/// <param name="RowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
/// <param name="ColumnDefId">Колонка — для підстановки <c>{Month}</c>.</param>
public sealed record ExpressionValidationRequest(
    string Expression,
    ExpressionDialect Dialect,
    int? TemplateVersionId,
    int? TableDefId,
    string? RowKey,
    int? ColumnDefId);

/// <summary>Результат перевірки виразу.</summary>
/// <param name="Diagnostics">
/// Зауваження з позиціями — тими самими, які покаже публікація.
/// </param>
/// <param name="ResultType">
/// Тип результату виразу; <c>null</c> — вираз не розібрався.
/// </param>
/// <param name="SkippedChecks">
/// Які перевірки НЕ виконувалися. ⛔ Порожній перелік зауважень при
/// непорожньому <c>SkippedChecks</c> означає «те, що перевіряли, ціле», а не
/// «вираз правильний»: сплутати ці два твердження означає пообіцяти
/// публікацію, якої не буде.
/// </param>
public sealed record ExpressionValidationDto(
    IReadOnlyList<DiagnosticInfo> Diagnostics,
    string? ResultType,
    IReadOnlyList<string> SkippedChecks);
