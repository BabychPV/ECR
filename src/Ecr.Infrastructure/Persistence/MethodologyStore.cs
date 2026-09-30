using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IMethodologyStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <param name="db">Контекст бази.</param>
/// <param name="constantCap">
/// Стеля констант однієї версії (<see cref="GetConstantsAsync"/>). Параметр —
/// лише щоб тест міг перевірити відмову на малій стелі; продукт іде через
/// конструктор з одним параметром і бере <see cref="MaxChildren"/>.
/// </param>
public sealed class MethodologyStore(EcrDbContext db, int constantCap) : IMethodologyStore
{
    /// <summary>Сховище зі стелею констант за замовчуванням.</summary>
    /// <param name="db">Контекст бази.</param>
    public MethodologyStore(EcrDbContext db)
        : this(db, MaxChildren)
    {
    }

    /// <summary>
    /// Стеля вибірки дочірніх записів версії.
    /// </summary>
    /// <remarks>
    /// Формул у методології — десятки, речовин — одиниці. Межа є не тому, що
    /// їх може бути багато, а тому, що помилка в даних без неї виглядала б як
    /// повільність, а не як помилка.
    /// </remarks>
    private const int MaxChildren = 10_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyVersion>> GetPublishedVersionsAsync(
        int methodologyId, CancellationToken ct)
        => await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => v.MethodologyId == methodologyId
                        && v.Status == TemplateVersionStatus.Published)
            .OrderByDescending(v => v.EffectiveFrom)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<MethodologySymbols> GetSymbolsAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        var constants = await db.MethodologyConstants
            .AsNoTracking()
            .Where(c => c.MethodologyVersionId == methodologyVersionId)
            .OrderBy(c => c.Code)
            .Take(MaxChildren)
            .Select(c => new MethodologySymbol(c.Code, c.UnitId, c.Category))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var formulas = await db.MethodologyFormulas
            .AsNoTracking()
            .Where(f => f.MethodologyVersionId == methodologyVersionId)
            .OrderBy(f => f.EvaluationOrder)
            .ThenBy(f => f.Id)
            .Take(MaxChildren)
            .Select(f => new MethodologySymbol(f.Code, f.OutputUnitId, null))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var arguments = await ArgumentsAsync(methodologyVersionId, ct).ConfigureAwait(false);

        return new MethodologySymbols(constants, formulas, arguments);
    }

    /// <summary>
    /// Аргументи <c>@</c> — коди колонок таблиці, до якої прив'язана методологія.
    /// </summary>
    /// <remarks>
    /// ⛔ Шлях довгий і не скорочується: версія → методологія → активні
    /// прив'язки (<c>cfg.CalculationBinding</c>) → таблиці → колонки. Коротшого
    /// немає, бо аргумент — це не властивість методології, а КОНТРАКТ між нею і
    /// таблицею, на якій її запускають (<c>D-69</c>).
    ///
    /// ⚠ Порожній результат — не помилка: методологія без активної прив'язки
    /// справді не має аргументів, які можна назвати. Вигадати їх зі списку
    /// виходів означало б підказувати імена, яких у рядку джерела немає.
    /// </remarks>
    private async Task<IReadOnlyList<MethodologySymbol>> ArgumentsAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        var methodologyId = await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => v.Id == methodologyVersionId)
            .Select(v => (int?)v.MethodologyId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (methodologyId is not { } id)
        {
            return [];
        }

        var tableIds = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.MethodologyId == id && b.IsActive)
            .Select(b => b.TableDefId)
            .Distinct()
            .OrderBy(tableId => tableId)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (tableIds.Count == 0)
        {
            return [];
        }

        return await db.ColumnDefs
            .AsNoTracking()
            .Where(c => tableIds.Contains(c.TableDefId) && !c.IsDeleted)
            .OrderBy(c => c.Code)
            .Take(MaxChildren)
            .Select(c => new MethodologySymbol(c.Code, c.UnitId, c.DataType.ToString()))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyRule>> GetRulesAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyRules
            .AsNoTracking()
            .Where(r => r.MethodologyVersionId == methodologyVersionId && r.IsActive)

            // Порядок за Priority — це і є правило «перший збіг виграє»
            // (ФВ-13.4). Сортувати в пам'яті означало б покластися на те, що
            // база поверне рядки як їй зручно.
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyFormula>> GetFormulasAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyFormulas
            .Where(f => f.MethodologyVersionId == methodologyVersionId)
            .OrderBy(f => f.EvaluationOrder)
            .ThenBy(f => f.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Стеля не обрізає мовчки — вона відмовляє. З аудиту P1 прогін читає
    /// константи ВСІЄЇ версії цим запитом і вибирає кандидата в пам'яті, тож
    /// обрізаний хвіст означав би, що частина констант «не існує»: формула
    /// тихо читала б <c>#REF</c>, і причину не було б видно ніде. Раніше
    /// стеля стояла на один код (<c>ConstantStore</c>) і такого ризику не
    /// несла. Читаємо <c>стеля + 1</c>: зайвий рядок і є доказом переповнення.
    /// </remarks>
    public async Task<IReadOnlyList<MethodologyConstant>> GetConstantsAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        var constants = await db.MethodologyConstants
            .AsNoTracking()
            .Where(c => c.MethodologyVersionId == methodologyVersionId)
            .OrderBy(c => c.Code)
            .ThenBy(c => c.Id)
            .Take(constantCap + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (constants.Count > constantCap)
        {
            throw new Domain.Abstractions.DomainException(
                "ECR-CALC-0422",
                $"Methodology version {methodologyVersionId} has more than {constantCap} constants: "
                + "they cannot all be read, and a constant left out would silently evaluate to #REF.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.constantsOverCap",
                    ["methodologyVersionId"] = methodologyVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["cap"] = constantCap.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return constants;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Версія бібліотеки добирається тим самим правилом, що й будь-яка інша:
    /// остання опублікована з <c>EffectiveFrom ≤ дата</c> (ФВ-9.3). Двох
    /// запитів це не варте, але одного — так: інакше перерахунок за минулий рік
    /// узяв би сьогоднішню редакцію <c>Common</c>, і 265 посилань корпусу
    /// порахували б інші числа, ніж рік тому.
    /// </remarks>
    public async Task<IReadOnlyList<MethodologyLibrary>> ResolveImportsAsync(
        int methodologyVersionId, DateOnly onDate, CancellationToken ct)
    {
        var imported = await db.MethodologyImports
            .AsNoTracking()
            .Where(i => i.MethodologyVersionId == methodologyVersionId)
            .Join(
                db.Methodologies.AsNoTracking(),
                i => i.ImportedMethodologyId,
                m => m.Id,
                (i, m) => new { m.Id, m.Code })
            .OrderBy(x => x.Code)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (imported.Count == 0)
        {
            return [];
        }

        var ids = imported.ConvertAll(x => x.Id);

        // ⚠ Deprecated теж бере участь: версія, виведена з обігу, лишається
        // чинною для періодів, які вона рахувала, — так само, як у
        // `Methodology.VersionOn`. Два різні правила вибору версії розійшлися б
        // на першому ж перерахунку минулого періоду.
        var versions = await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => ids.Contains(v.MethodologyId)
                        && v.EffectiveFrom != null
                        && v.EffectiveFrom <= onDate
                        && (v.Status == TemplateVersionStatus.Published
                            || v.Status == TemplateVersionStatus.Deprecated))
            .OrderByDescending(v => v.EffectiveFrom)
            // ⛔ `Version` у проєкції обов'язковий: без нього правило вибору
            // нижче не має чим розрізнити дві версії від однієї дати, і
            // відповідь визначав би порядок рядків.
            .Select(v => new { v.Id, v.MethodologyId, v.EffectiveFrom, v.Version })
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⛔ Правило вибору — те саме, що в `Methodology.VersionOn` і
        // `MethodologyResolver`, і задане воно один раз у домені. Тут стояло
        // `OrderByDescending(v => v.EffectiveFrom).First()`, тобто третя копія
        // того самого дефекту: за рівних дат вигравав перший рядок вибірки
        // (`H-24d-4`).
        var effective = versions
            .GroupBy(v => v.MethodologyId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(
                        v => new MethodologyVersionKey(v.EffectiveFrom, v.Version, v.Id),
                        MethodologyVersionKey.Currency)
                      .First().Id);

        var versionIds = effective.Values.ToList();

        var formulas = await db.MethodologyFormulas
            .AsNoTracking()
            .Where(f => versionIds.Contains(f.MethodologyVersionId))
            .OrderBy(f => f.MethodologyVersionId)
            .ThenBy(f => f.Id)
            .Select(f => new { f.MethodologyVersionId, f.Code })
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byVersion = formulas
            .GroupBy(f => f.MethodologyVersionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(f => f.Code).ToList());

        return imported.ConvertAll(x =>
        {
            var versionId = effective.TryGetValue(x.Id, out var found) ? found : (int?)null;

            return new MethodologyLibrary(
                x.Id,
                x.Code,
                versionId,
                versionId is { } id && byVersion.TryGetValue(id, out var codes) ? codes : []);
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Зміни лише готуються; записує їх <c>IUnitOfWork</c> у тій самій
    /// транзакції, що й публікацію. Окремий <c>SaveChanges</c> тут означав би,
    /// що ребра графа можуть уціліти після відкоченої публікації.
    /// </remarks>
    public async Task ReplaceDependenciesAsync(
        int fromMethodologyId, IReadOnlyCollection<int> toMethodologyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(toMethodologyIds);

        var existing = await db.MethodologyDependencies
            .Where(d => d.FromMethodologyId == fromMethodologyId)
            .OrderBy(d => d.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var wanted = new HashSet<int>(toMethodologyIds);

        db.MethodologyDependencies.RemoveRange(
            existing.Where(d => !wanted.Contains(d.ToMethodologyId)));

        var present = existing.Select(d => d.ToMethodologyId).ToHashSet();

        foreach (var target in wanted.Where(t => !present.Contains(t)))
        {
            db.MethodologyDependencies.Add(new MethodologyDependency(fromMethodologyId, target));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologySubstance>> GetSubstancesAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologySubstances
            .AsNoTracking()
            .Where(s => s.MethodologyVersionId == methodologyVersionId)
            .OrderBy(s => s.Ordinal)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyOutput>> GetOutputsAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyOutputs
            .AsNoTracking()
            .Where(o => o.MethodologyVersionId == methodologyVersionId)
            .OrderBy(o => o.Ordinal)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyRequiredInput>> GetRequiredInputsAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyRequiredInputs
            .AsNoTracking()
            .Where(r => r.MethodologyVersionId == methodologyVersionId)
            .OrderBy(r => r.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetMethodologyIdsBoundToTableAsync(
        int tableDefId, CancellationToken ct)
        => await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && b.TableDefId == tableDefId)
            .Select(b => b.MethodologyId)
            .Distinct()
            .OrderBy(methodologyId => methodologyId)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetMethodologyIdsBoundToTablesAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableDefIds);
        if (tableDefIds.Count == 0)
        {
            return [];
        }

        var ids = tableDefIds.Distinct().ToList();

        return await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && ids.Contains(b.TableDefId))
            .Select(b => b.MethodologyId)
            .Distinct()
            .OrderBy(methodologyId => methodologyId)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Два запити на весь набір таблиць, не по запиту на таблицю: зріз —
    /// найгарячіше читання системи. Версії — УСІХ статусів: виведена з обігу
    /// версія лишається тією, що порахувала свої періоди.
    /// </remarks>
    public async Task<IReadOnlyList<ColumnResultBinding>> GetColumnResultBindingsAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableDefIds);
        if (tableDefIds.Count == 0)
        {
            return [];
        }

        var ids = tableDefIds.ToList();

        var bindings = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && ids.Contains(b.TableDefId))
            .OrderBy(b => b.Id)
            .Take(MaxChildren)
            .Select(b => new { b.TableDefId, b.ColumnDefId, b.MethodologyId, b.OutputCode, b.MatchJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (bindings.Count == 0)
        {
            return [];
        }

        var methodologyIds = bindings.Select(b => b.MethodologyId).Distinct().ToList();

        var versions = await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => methodologyIds.Contains(v.MethodologyId))
            .OrderBy(v => v.Id)
            .Select(v => new { v.Id, v.MethodologyId })
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byMethodology = versions.ToLookup(v => v.MethodologyId, v => v.Id);

        return bindings.ConvertAll(b => new ColumnResultBinding(
            b.TableDefId, b.ColumnDefId, b.MethodologyId, b.OutputCode, b.MatchJson,
            [.. byMethodology[b.MethodologyId]]));
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ «Актуальний прогін документа» — останній прогін зі статусом
    /// <c>Current</c>, у якому є числа ЦЬОГО документа (прогін належить
    /// проєкту й періоду, не документу). Входи — зміни <c>aud.CellChange</c>
    /// документа за період після ПОЧАТКУ прогону, крім <c>Recalculation</c>:
    /// формули шаблону пишуть свої комірки саме всередині прогону, і рахувати
    /// їх «зміною входів» означало б позначати застарілим кожен свіжий
    /// результат.
    ///
    /// ⚠ Сирий SQL: <c>aud.*</c> немає в моделі EF (журнал пише
    /// <c>AuditWriter</c> через TVP).
    ///
    /// ⚠ Фільтр таблиць — через <c>cfg.ColumnDef.TableDefId</c> колонки зміни
    /// (<c>aud.CellChange.ColumnDefId</c> NOT NULL), перелік іде одним
    /// JSON-параметром (<c>OPENJSON</c>), як у <c>RuleCoverageReader</c>: число
    /// параметрів не залежить від кількості таблиць. Вибір прогону від фільтра
    /// НЕ залежить — прогін належить документу, не таблиці.
    /// </remarks>
    public async Task<CalculationFreshness> GetCalculationFreshnessAsync(
        long documentId, int periodKey, IReadOnlyCollection<int>? tableDefIds, CancellationToken ct)
    {
        // ⚠ Прапорець окремим параметром, а не NULL у JSON: параметр без
        // значення не має типу, і план для двох форм запиту розійшовся б.
        var filterTables = tableDefIds is not null;
        var tablesJson = JsonSerializer.Serialize(tableDefIds ?? []);

        var rows = await db.Database
            .SqlQuery<FreshnessRow>($"""
                SELECT TOP (1)
                       r.Id AS RunId, r.StartedAt AS StartedAt,
                       r.FinishedAt AS CalculatedAt,
                       (SELECT MAX(c.ChangedAt)
                          FROM aud.CellChange AS c
                         WHERE c.DocumentId = {documentId}
                           AND c.PeriodKey = {periodKey}
                           AND c.ChangedAt > r.StartedAt
                           AND c.Origin <> N'Recalculation'
                           AND ({filterTables} = CAST(0 AS bit)
                                OR EXISTS (SELECT 1
                                             FROM cfg.ColumnDef AS cd
                                            WHERE cd.Id = c.ColumnDefId
                                              AND cd.TableDefId IN (SELECT CAST(j.value AS int)
                                                                      FROM OPENJSON({tablesJson}) AS j)))) AS InputsChangedAt
                  FROM calc.CalculationRun AS r
                 WHERE r.Status = N'Current'
                   AND r.PeriodKey = {periodKey}
                   AND EXISTS (SELECT 1
                                 FROM calc.CalculationResult AS cr
                                WHERE cr.CalculationRunId = r.Id
                                  AND cr.PeriodKey = {periodKey}
                                  AND cr.DocumentId = {documentId})
                 ORDER BY r.Id DESC
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return new CalculationFreshness(null, null);
        }

        var row = rows[0];

        // RT-25 (ФВ-9.19): правка ДОВІДНИКА, який читає методологія цього документа, теж робить
        // число застарілим. Лише для вікна «увесь документ» (панель результатів): фільтр таблиць —
        // це шлях подання аркуша, і блокувати подання правкою довідника в чужому аркуші — зміна
        // поведінки, якої ніхто не просив. Автоматичного перерахунку це НЕ запускає (R-14):
        // перерахунок ставить людина через `recalculate-impacted`.
        // ⚠ Ребра — `cfg.RegistryUse` версій методології (SourceKind = 1) для версій, що дали
        // результати ЦЬОГО документа в цьому прогоні; ребра шаблону (SourceKind = 0) з'являться з RT-24.
        var changed = filterTables
            ? []
            : await db.Database
                .SqlQuery<RegistryChangeRow>($"""
                    SELECT TOP (50) rd.Code AS Code, rd.DataChangedAt AS ChangedAt
                      FROM cfg.RegistryDef AS rd
                     WHERE rd.DataChangedAt > {row.StartedAt}
                       AND EXISTS (SELECT 1
                                     FROM cfg.RegistryUse AS u
                                     JOIN calc.CalculationResult AS cr ON cr.MethodologyVersionId = u.SourceId
                                    WHERE u.RegistryDefId = rd.Id
                                      AND u.SourceKind = 1
                                      AND cr.CalculationRunId = {row.RunId}
                                      AND cr.PeriodKey = {periodKey}
                                      AND cr.DocumentId = {documentId})
                     ORDER BY rd.Code
                    """)
                .ToListAsync(ct)
                .ConfigureAwait(false);

        var inputsChangedAt = changed.Count == 0
            ? row.InputsChangedAt
            : new[] { row.InputsChangedAt, changed.Max(c => c.ChangedAt) }.Max();

        return new CalculationFreshness(row.CalculatedAt, inputsChangedAt, [.. changed.Select(c => c.Code)]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, MethodologyVersionLabel>> GetVersionLabelsAsync(
        IReadOnlyCollection<int> methodologyVersionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyVersionIds);
        if (methodologyVersionIds.Count == 0)
        {
            return new Dictionary<int, MethodologyVersionLabel>();
        }

        var ids = methodologyVersionIds.ToList();

        var labels = await (
            from version in db.MethodologyVersions.AsNoTracking()
            where ids.Contains(version.Id)
            join methodology in db.Methodologies.AsNoTracking() on version.MethodologyId equals methodology.Id
            orderby version.Id
            select new { version.Id, methodology.Code, version.Version })
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return labels.ToDictionary(l => l.Id, l => new MethodologyVersionLabel(l.Id, l.Code, l.Version));
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Журнал спільний для шаблонів і методологій (<c>EntityType</c>), і
    /// <c>EntityId</c> — версія, а не методологія: звужується через
    /// <c>calc.MethodologyVersion</c>. Ім'я — з <c>sec.User</c> (F-16: «By user»
    /// числом не читається).
    /// </remarks>
    public async Task<IReadOnlyList<MethodologyPublicationEntry>> ListPublicationsAsync(
        int methodologyId, CancellationToken ct)
        => await db.Database
            .SqlQuery<MethodologyPublicationEntry>($"""
                SELECT TOP (500)
                       e.Id, e.ChangedAt, v.Id AS MethodologyVersionId, v.Version, e.ChangeReason,
                       e.ChangedByUserId, COALESCE(NULLIF(u.DisplayName, N''), u.UserName) AS ChangedByName,
                       e.ResultDiffJson
                  FROM aud.PublicationEvent AS e
                  JOIN calc.MethodologyVersion AS v ON v.Id = e.EntityId
                  LEFT JOIN sec.[User] AS u ON u.Id = e.ChangedByUserId
                 WHERE e.EntityType = N'calc.MethodologyVersion'
                   AND v.MethodologyId = {methodologyId}
                 ORDER BY e.ChangedAt DESC, e.Id DESC
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Рядок запиту свіжості.</summary>
    public sealed record FreshnessRow(long RunId, DateTime StartedAt, DateTime? CalculatedAt, DateTime? InputsChangedAt);

    /// <summary>Довідник, змінений після прогону (RT-25).</summary>
    public sealed record RegistryChangeRow(string Code, DateTime ChangedAt);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Версії ЗАВАНТАЖУЮТЬСЯ разом із методологією і відстежуються: агрегат
    /// потрібен, щоб перевірити перетин вікон і опублікувати версію в одній
    /// транзакції. <c>AsNoTracking</c> тут зробив би публікацію
    /// беззмістовною — зміни нікуди не збереглися б.
    /// </remarks>
    public async Task<Methodology?> FindByVersionAsync(int methodologyVersionId, CancellationToken ct)
    {
        var methodologyId = await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => v.Id == methodologyVersionId)
            .Select(v => (int?)v.MethodologyId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (methodologyId is not { } id)
        {
            return null;
        }

        return await db.Methodologies
            .Include(m => m.Versions)
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Тести читаються з <c>calc.TestCase</c> (`P-08`). Порожній набір і
    /// далі означає «зеленого тесту немає», і публікація відхиляється
    /// (ФВ-9.12) — але тепер це стан **даних**, а не відсутність таблиці.
    /// <para>
    /// ⛔ Зіпсований JSON тесту не мовчить: він робить тест **червоним**, а не
    /// відсутнім. «Не змогли прочитати, отже все гаразд» — саме та підміна,
    /// через яку публікація без перевірки виглядає як публікація з перевіркою.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<MethodologyTestCase>> GetTestCasesAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        var rows = await db.MethodologyTestCases
            .AsNoTracking()
            .Where(t => t.MethodologyVersionId == methodologyVersionId)
            .OrderBy(t => t.Code)
            .Take(MaxTestCases)
            .Select(t => new { t.Code, t.InputJson, t.ExpectedJson, t.Tolerance })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<MethodologyTestCase>(rows.Count);

        foreach (var row in rows)
        {
            var input = Deserialize<CalculationInput>(row.InputJson, row.Code, "вхід");
            var expected = Deserialize<Dictionary<string, decimal>>(row.ExpectedJson, row.Code, "очікуваний вихід");

            result.Add(new MethodologyTestCase(row.Code, input, expected, row.Tolerance));
        }

        return result;
    }

    /// <summary>Стеля вибірки тестів; сотня на версію — уже нетипово.</summary>
    private const int MaxTestCases = 1_000;

    /// <summary>Налаштування розбору тестів; спільні на всі виклики.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions TestCaseOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Розбирає JSON тесту або називає, що саме зіпсовано.</summary>
    private static T Deserialize<T>(string json, string code, string part)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, TestCaseOptions)
                   ?? throw new InvalidOperationException($"Тест «{code}»: {part} порожній.");
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new InvalidOperationException($"Тест «{code}»: {part} не читається.", error);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Версія бібліотеки — з <see cref="ResolveImportsAsync"/>, а не власним запитом:
    /// правило вибору задане один раз (<c>H-24d-4</c>), і третя його копія тут дала б
    /// прогону іншу редакцію <c>Common</c>, ніж публікації.
    ///
    /// ⚠ Склад читається без відстеження: це чужа версія, прогін і публікація її лише
    /// читають. Відстежені формули бібліотеки потрапили б у <c>SaveChanges</c> публікації
    /// викликача — разом з усім, що там могло випадково змінитися.
    ///
    /// ⚠ Константи — через <see cref="GetConstantsAsync"/>: стеля там відмовляє, а не
    /// обрізає, і для бібліотеки це так само важливо, як для своєї версії.
    /// </remarks>
    public async Task<IReadOnlyList<MethodologyLibraryContent>> GetLibraryContentsAsync(
        int methodologyVersionId, DateOnly onDate, CancellationToken ct)
    {
        var libraries = await ResolveImportsAsync(methodologyVersionId, onDate, ct).ConfigureAwait(false);
        var contents = new List<MethodologyLibraryContent>(libraries.Count);

        foreach (var library in libraries)
        {
            if (library.MethodologyVersionId is not { } versionId)
            {
                contents.Add(new MethodologyLibraryContent(library, null, null, [], []));
                continue;
            }

            var modes = await db.MethodologyVersions
                .AsNoTracking()
                .Where(v => v.Id == versionId)
                .Select(v => new { v.NumericMode, v.CalendarMode })
                .FirstAsync(ct)
                .ConfigureAwait(false);

            var formulas = await db.MethodologyFormulas
                .AsNoTracking()
                .Where(f => f.MethodologyVersionId == versionId)
                .OrderBy(f => f.EvaluationOrder)
                .ThenBy(f => f.Id)
                .Take(MaxChildren)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var constants = await GetConstantsAsync(versionId, ct).ConfigureAwait(false);

            contents.Add(new MethodologyLibraryContent(
                library, modes.NumericMode, modes.CalendarMode, formulas, constants));
        }

        return contents;
    }
}

/// <summary>Реалізація <see cref="IConstantStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// Порт віддає **кандидатів**, а не готове значення: звуження за категорією і
/// речовиною та вибір темпорального інтервалу — правила предметної області
/// (ФВ-16.5), і живуть вони в <c>ConstantResolver</c>. Зокрема правило «кілька
/// кандидатів на одну дату — помилка конфігурації» неможливо перевірити, якщо
/// сховище вже вибрало один запис.
/// </remarks>
public sealed class ConstantStore(EcrDbContext db) : IConstantStore
{
    /// <summary>Стеля: варіантів однієї константи — одиниці, не тисячі.</summary>
    private const int MaxCandidates = 1_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyConstant>> GetCandidatesAsync(
        int methodologyVersionId, string code, CancellationToken ct)
        => await db.MethodologyConstants
            .AsNoTracking()
            .Where(c => c.MethodologyVersionId == methodologyVersionId && c.Code == code)
            .OrderBy(c => c.Id)
            .Take(MaxCandidates)
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
