using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IMethodologyStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class MethodologyStore(EcrDbContext db) : IMethodologyStore
{
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
    public async Task<IReadOnlyList<MethodologyConstant>> GetConstantsAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyConstants
            .AsNoTracking()
            .Where(c => c.MethodologyVersionId == methodologyVersionId)
            .OrderBy(c => c.Code)
            .ThenBy(c => c.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

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
            .Select(v => new { v.Id, v.MethodologyId, v.EffectiveFrom })
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var effective = versions
            .GroupBy(v => v.MethodologyId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.EffectiveFrom).First().Id);

        var versionIds = effective.Values.ToList();

        var formulas = await db.MethodologyFormulas
            .AsNoTracking()
            .Where(f => versionIds.Contains(f.MethodologyVersionId))
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
