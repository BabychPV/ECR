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
    public async Task<IReadOnlyDictionary<long, string>> GetSubstanceCodesAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        var pairs = await db.MethodologySubstances
            .AsNoTracking()
            .Where(s => s.MethodologyVersionId == methodologyVersionId)
            .Join(
                db.RegistryEntries.AsNoTracking(),
                s => s.SubstanceEntryId,
                e => e.Id,
                (s, e) => new { s.SubstanceEntryId, e.Code, e.RegistryDefId })
            .OrderBy(p => p.SubstanceEntryId)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⛔ Код запису довідника (`EcrCode`) мусить починатися з літери, тож числовий код речовини
        // (301, 1325) не може бути `RegistryEntry.Code`: у довіднику він лежить у полі SUBSTANCE_CODE,
        // а код запису — `E301` (так само, як LAND_WASTE_SUBSTANCE). Суфікс форми B `{ключ}_301`
        // береться з поля; немає поля чи значення — код запису (як було).
        var entryIds = pairs.Select(p => p.SubstanceEntryId).Distinct().ToList();
        var registryIds = pairs.Select(p => p.RegistryDefId).Distinct().ToList();
        var fieldIds = await db.RegistryFieldDefs
            .AsNoTracking()
            .Where(f => registryIds.Contains(f.RegistryDefId) && f.Code == "SUBSTANCE_CODE")
            .Select(f => f.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var fromField = new Dictionary<long, string>();
        if (fieldIds.Count > 0)
        {
            var values = await db.RegistryValues
                .AsNoTracking()
                .Where(v => entryIds.Contains(v.RegistryEntryId) && fieldIds.Contains(v.RegistryFieldDefId))
                .Select(v => new { v.RegistryEntryId, v.ValueString, v.ValueNumeric })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var value in values)
            {
                var text = !string.IsNullOrWhiteSpace(value.ValueString)
                    ? value.ValueString.Trim()
                    : value.ValueNumeric?.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(text))
                {
                    fromField[value.RegistryEntryId] = text;
                }
            }
        }

        return pairs
            .GroupBy(p => p.SubstanceEntryId)
            .ToDictionary(g => g.Key, g => fromField.TryGetValue(g.Key, out var field) ? field : g.First().Code);
    }

    /// <inheritdoc />
    public async Task<MethodologyScript?> GetScriptAsync(int methodologyVersionId, CancellationToken ct)
    {
        var row = await (
                from v in db.MethodologyVersions.AsNoTracking()
                join m in db.Methodologies.AsNoTracking() on v.MethodologyId equals m.Id
                where v.Id == methodologyVersionId
                select new
                {
                    v.ScriptText,
                    v.ScriptHash,
                    v.Engine,
                    v.NumericMode,
                    m.Kind,
                })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is { ScriptText: { Length: > 0 } text }
            ? new MethodologyScript(
                text, row.ScriptHash, row.Engine, row.Kind == MethodologyKind.Library, row.NumericMode)
            : null;
    }

    /// <inheritdoc />
    public async Task ReplaceScriptReadTablesAsync(
        int versionId, IReadOnlyList<string> tableCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableCodes);

        var existing = await db.MethodologyReadTables
            .Where(t => t.MethodologyVersionId == versionId)
            .OrderBy(t => t.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Регістр не розрізняється: SQL-порівняння за замовчуванням нечутливе, а `UQ_MethodologyReadTable`
        // відхилив би «T3_00» і «t3_00» як дубль.
        var wanted = tableCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        db.MethodologyReadTables.RemoveRange(
            existing.Where(e => !wanted.Contains(e.TableCode, StringComparer.OrdinalIgnoreCase)));

        foreach (var code in wanted.Where(
                     w => !existing.Any(e => string.Equals(e.TableCode, w, StringComparison.OrdinalIgnoreCase))))
        {
            db.MethodologyReadTables.Add(new MethodologyReadTable(versionId, code));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Службовий рядок <see cref="MethodologyReadField.RecordedMarker"/> пишеться завжди: версія, що не читає жодного поля,
    /// відрізняється від версії без запису (її прохід читача вважає читачем усього).
    /// </remarks>
    public async Task ReplaceScriptReadFieldsAsync(
        int versionId, IReadOnlyList<Ecr.Application.Calculations.Scripting.ScriptReadField> fields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var existing = await db.MethodologyReadFields
            .Where(f => f.MethodologyVersionId == versionId)
            .OrderBy(f => f.Id)
            .Take(MaxChildren + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var wanted = fields
            .Where(f => !string.IsNullOrWhiteSpace(f.FieldCode))
            .GroupBy(f => f.FieldCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Code: g.Key, OtherMonths: g.Any(f => f.OtherMonths)))
            .Prepend((Code: MethodologyReadField.RecordedMarker, OtherMonths: false))
            .ToList();

        // ✎ Огляд b9, п. 4: рядків понад стелю — частковий перелік дав би вставку поля, що вже лежить за стелею
        // (UQ_MethodologyReadField). Тоді — видалення всього переліку версії одним оператором і вставка заново.
        if (existing.Count > MaxChildren)
        {
            foreach (var row in existing)
            {
                db.Entry(row).State = EntityState.Detached;
            }

            await db.MethodologyReadFields
                .Where(f => f.MethodologyVersionId == versionId)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            foreach (var (code, otherMonths) in wanted)
            {
                db.MethodologyReadFields.Add(new MethodologyReadField(versionId, code, otherMonths));
            }

            return;
        }

        db.MethodologyReadFields.RemoveRange(existing.Where(e => !wanted.Exists(
            w => string.Equals(w.Code, e.FieldCode, StringComparison.OrdinalIgnoreCase) && w.OtherMonths == e.OtherMonths)));

        foreach (var (code, otherMonths) in wanted.Where(w => !existing.Exists(
                     e => string.Equals(e.FieldCode, w.Code, StringComparison.OrdinalIgnoreCase) && e.OtherMonths == w.OtherMonths)))
        {
            db.MethodologyReadFields.Add(new MethodologyReadField(versionId, code, otherMonths));
        }
    }

    /// <inheritdoc />
    public async Task ReplaceScriptOutputsAsync(
        int versionId, IReadOnlyList<ScriptOutputRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var existing = await db.MethodologyOutputs
            .Where(o => o.MethodologyVersionId == versionId)
            .OrderBy(o => o.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var wanted = rows
            .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Рядок, у якого змінилося хоч щось (код — теж, регістр включно), замінюється цілком: сутність
        // ззовні незмінна, а нічого не змінилося — рядок лишається як є.
        foreach (var old in existing)
        {
            var next = wanted.FirstOrDefault(w => string.Equals(w.Code, old.Code, StringComparison.OrdinalIgnoreCase));
            if (next is null
                || !string.Equals(next.Code, old.Code, StringComparison.Ordinal)
                || next.UnitId != old.UnitId
                || next.Ordinal != old.Ordinal
                || next.IsPerSubstance != old.IsPerSubstance)
            {
                db.MethodologyOutputs.Remove(old);
            }
        }

        foreach (var row in wanted)
        {
            var kept = existing.Any(o =>
                string.Equals(o.Code, row.Code, StringComparison.Ordinal)
                && o.UnitId == row.UnitId
                && o.Ordinal == row.Ordinal
                && o.IsPerSubstance == row.IsPerSubstance);
            if (kept)
            {
                continue;
            }

            var output = new MethodologyOutput(
                versionId, Ecr.Domain.ValueObjects.EcrCode.Create(row.Code), row.UnitId, row.Ordinal);
            output.SetPerSubstance(row.IsPerSubstance);
            db.MethodologyOutputs.Add(output);
        }
    }

    /// <inheritdoc />
    public async Task ReplaceScriptSubstancesAsync(
        int versionId, IReadOnlyList<long> substanceEntryIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(substanceEntryIds);

        var existing = await db.MethodologySubstances
            .Where(s => s.MethodologyVersionId == versionId)
            .OrderBy(s => s.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var wanted = substanceEntryIds.Distinct().Select((id, index) => (Id: id, Ordinal: index + 1)).ToList();

        foreach (var old in existing)
        {
            if (!wanted.Any(w => w.Id == old.SubstanceEntryId && w.Ordinal == old.Ordinal))
            {
                db.MethodologySubstances.Remove(old);
            }
        }

        foreach (var (id, ordinal) in wanted)
        {
            if (!existing.Any(s => s.SubstanceEntryId == id && s.Ordinal == ordinal))
            {
                db.MethodologySubstances.Add(new MethodologySubstance(versionId, id, ordinal));
            }
        }
    }

    /// <inheritdoc />
    public async Task ReplaceScriptImportsAsync(
        int versionId, IReadOnlyList<int> importedMethodologyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(importedMethodologyIds);

        var ownerMethodologyId = await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => (int?)v.MethodologyId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (ownerMethodologyId is not { } owner)
        {
            return;
        }

        var existing = await db.MethodologyImports
            .Where(i => i.MethodologyVersionId == versionId)
            .OrderBy(i => i.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var wanted = importedMethodologyIds.Distinct().ToList();
        db.MethodologyImports.RemoveRange(existing.Where(i => !wanted.Contains(i.ImportedMethodologyId)));

        foreach (var id in wanted.Where(w => !existing.Any(i => i.ImportedMethodologyId == w)))
        {
            db.MethodologyImports.Add(new MethodologyImport(versionId, id, owner));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, long>> FindSubstanceEntryIdsAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codes);

        var wanted = codes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return result;
        }

        // Довідники речовин — ті, що мають поле SUBSTANCE_CODE (так само, як у GetSubstanceCodesAsync).
        var fields = await db.RegistryFieldDefs
            .AsNoTracking()
            .Where(f => f.Code == "SUBSTANCE_CODE")
            .Select(f => new { f.Id, f.RegistryDefId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (fields.Count == 0)
        {
            return result;
        }

        var registryIds = fields.Select(f => f.RegistryDefId).Distinct().ToList();
        var fieldIds = fields.Select(f => f.Id).ToList();

        var byField = await (
                from v in db.RegistryValues.AsNoTracking()
                join e in db.RegistryEntries.AsNoTracking() on v.RegistryEntryId equals e.Id
                where fieldIds.Contains(v.RegistryFieldDefId) && !e.IsDeleted
                orderby e.Id, v.Id
                select new { e.Id, v.ValueString, v.ValueNumeric })
            .Take(MaxChildren * 10)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in byField.OrderBy(r => r.Id))
        {
            var text = !string.IsNullOrWhiteSpace(row.ValueString)
                ? row.ValueString.Trim()
                : row.ValueNumeric?.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture);
            if (text is not null && wanted.Contains(text, StringComparer.OrdinalIgnoreCase) && !result.ContainsKey(text))
            {
                result[text] = row.Id;
            }
        }

        var missing = wanted.Where(w => !result.ContainsKey(w)).ToList();
        if (missing.Count > 0)
        {
            var byCode = await db.RegistryEntries
                .AsNoTracking()
                .Where(e => registryIds.Contains(e.RegistryDefId) && !e.IsDeleted && missing.Contains(e.Code))
                .Select(e => new { e.Id, e.Code })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in byCode.OrderBy(r => r.Id))
            {
                var key = missing.First(m => string.Equals(m, row.Code, StringComparison.OrdinalIgnoreCase));
                if (!result.ContainsKey(key))
                {
                    result[key] = row.Id;
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LiveTable>> FindLiveTablesByCodeAsync(
        string tableCode, string? templateCode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableCode);

        var candidates = await (
                from table in db.TableDefs.AsNoTracking()
                where table.Code == tableCode && !table.IsDeleted
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                where !sheet.IsDeleted
                join version in db.TemplateVersions.AsNoTracking() on sheet.TemplateVersionId equals version.Id
                where version.Status == TemplateVersionStatus.Published
                join template in db.Templates.AsNoTracking() on version.TemplateId equals template.Id
                where templateCode == null || template.Code == templateCode
                orderby table.Id
                select new
                {
                    TableDefId = table.Id,
                    TableCode = table.Code,
                    VersionId = version.Id,
                    TemplateId = template.Id,
                    TemplateCode = template.Code,
                })
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // «Жива» версія шаблону — остання опублікована (так само, як ListLiveTableDefIdsAsync).
        var templateIds = candidates.Select(c => c.TemplateId).Distinct().ToList();
        if (templateIds.Count == 0)
        {
            return [];
        }

        var published = await db.TemplateVersions
            .AsNoTracking()
            .Where(v => templateIds.Contains(v.TemplateId) && v.Status == TemplateVersionStatus.Published)
            .Select(v => new { v.Id, v.TemplateId, v.PublishedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var latest = published
            .GroupBy(v => v.TemplateId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.PublishedAt).ThenByDescending(v => v.Id).First().Id);

        var live = candidates
            .Where(c => latest.TryGetValue(c.TemplateId, out var id) && id == c.VersionId)
            .OrderBy(c => c.TemplateCode, StringComparer.Ordinal)
            .ThenBy(c => c.TableDefId)
            .ToList();
        if (live.Count == 0)
        {
            return [];
        }

        var tableIds = live.Select(l => l.TableDefId).ToList();
        var columns = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => tableIds.Contains(c.TableDefId) && !c.IsDeleted)
            .OrderBy(c => c.Ordinal)
            .ThenBy(c => c.Id)
            .Select(c => new
            {
                c.TableDefId,
                c.Id,
                c.Code,
                c.DataType,
                c.IsMonthColumn,
                c.MonthNumber,
                c.UnitId,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var aliases = await db.MonthInputAliases
            .AsNoTracking()
            .Where(a => tableIds.Contains(a.TableDefId))
            .Select(a => new { a.TableDefId, a.Alias })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return
        [
            .. live.Select(l => new LiveTable(
                l.TemplateId,
                l.TemplateCode,
                l.VersionId,
                l.TableDefId,
                l.TableCode,
                [.. columns.Where(c => c.TableDefId == l.TableDefId)
                    .Select(c => new LiveColumn(c.Id, c.Code, c.DataType, c.IsMonthColumn, c.MonthNumber, c.UnitId))],
                [.. aliases.Where(a => a.TableDefId == l.TableDefId).Select(a => a.Alias).Distinct(StringComparer.OrdinalIgnoreCase)])),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetMethodologyDependenciesAsync(
        IReadOnlyCollection<int> fromMethodologyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fromMethodologyIds);
        if (fromMethodologyIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<int>>();
        }

        var ids = fromMethodologyIds.Distinct().ToList();
        var rows = await db.MethodologyDependencies
            .AsNoTracking()
            .Where(d => ids.Contains(d.FromMethodologyId))
            .OrderBy(d => d.Id)
            .Select(d => new { d.FromMethodologyId, d.ToMethodologyId })
            .Take(MaxChildren * 10)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.FromMethodologyId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)[.. g.Select(r => r.ToMethodologyId).Distinct().Order()]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetMethodologyIdsReadingTablesAsync(
        IReadOnlyCollection<string> tableCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableCodes);
        if (tableCodes.Count == 0)
        {
            return [];
        }

        var codes = tableCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return await (
                from r in db.MethodologyReadTables.AsNoTracking()
                join v in db.MethodologyVersions.AsNoTracking() on r.MethodologyVersionId equals v.Id
                where codes.Contains(r.TableCode)
                      && (v.Status == TemplateVersionStatus.Published
                          || v.Status == TemplateVersionStatus.Deprecated)
                select v.MethodologyId)
            .Distinct()
            .OrderBy(id => id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

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
    public async Task<string?> GetCategoryRuleAsync(int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyCategoryRules
            .AsNoTracking()
            .Where(r => r.MethodologyVersionId == methodologyVersionId)
            .Select(r => r.Expression)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetCategoryCodesAsync(int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyCategories
            .AsNoTracking()
            .Where(c => c.MethodologyVersionId == methodologyVersionId)
            .OrderBy(c => c.Ordinal)
            .ThenBy(c => c.Id)
            .Select(c => c.Code)
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
    public async Task<IReadOnlyList<MethodologyVersion>> GetPublishedVersionsOfAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyIds);
        if (methodologyIds.Count == 0)
        {
            return [];
        }

        var ids = methodologyIds.Distinct().ToList();

        return await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => ids.Contains(v.MethodologyId) && v.Status == TemplateVersionStatus.Published)
            .OrderBy(v => v.MethodologyId)
            .ThenByDescending(v => v.EffectiveFrom)
            .Take(MaxChildren * ids.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyRule>> GetRulesOfVersionsAsync(
        IReadOnlyCollection<int> methodologyVersionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyVersionIds);
        if (methodologyVersionIds.Count == 0)
        {
            return [];
        }

        var ids = methodologyVersionIds.Distinct().ToList();

        // Порядок у межах версії — той самий, що в GetRulesAsync: Priority, потім Id («перший збіг виграє», ФВ-13.4).
        return await db.MethodologyRules
            .AsNoTracking()
            .Where(r => ids.Contains(r.MethodologyVersionId) && r.IsActive)
            .OrderBy(r => r.MethodologyVersionId)
            .ThenBy(r => r.Priority)
            .ThenBy(r => r.Id)
            .Take(MaxChildren * ids.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyRequiredInput>> GetRequiredInputsOfVersionsAsync(
        IReadOnlyCollection<int> methodologyVersionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyVersionIds);
        if (methodologyVersionIds.Count == 0)
        {
            return [];
        }

        var ids = methodologyVersionIds.Distinct().ToList();

        return await db.MethodologyRequiredInputs
            .AsNoTracking()
            .Where(r => ids.Contains(r.MethodologyVersionId))
            .OrderBy(r => r.MethodologyVersionId)
            .ThenBy(r => r.Id)
            .Take(MaxChildren * ids.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

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

            // ⛔ W15z: без стелі. Річна розкладка дає прив'язку на кожен місяць кожного виходу (LAND_UI 1.0.19.0 - 10 075,
            // 1.0.20.0 - 9 300), і стеля 10 000, обрізаючи за Id, мовчки ховала б результати найновіших прив'язок.
            // Обсяг обмежений самими таблицями запиту (прив'язки однієї версії шаблону).
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
                       COALESCE(r.InputsAsOfUtc, r.StartedAt) AS InputsAsOf,
                       r.FinishedAt AS CalculatedAt,
                       (SELECT MAX(c.ChangedAt)
                          FROM aud.CellChange AS c
                         WHERE c.DocumentId = {documentId}
                           AND c.PeriodKey = {periodKey}
                           AND c.ChangedAt > r.StartedAt
                           AND c.Origin NOT IN (N'Recalculation', N'CalcError', N'CalcErrorResolved')
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

        // ⛔ L3-01: `SqlQuery<…>` віддає `datetime2` з `Kind = Unspecified`; без `SpecifyKind` момент пішов би в JSON без «Z».
        var row = rows[0] with
        {
            StartedAt = DateTime.SpecifyKind(rows[0].StartedAt, DateTimeKind.Utc),
            InputsAsOf = DateTime.SpecifyKind(rows[0].InputsAsOf, DateTimeKind.Utc),
            CalculatedAt = Utc(rows[0].CalculatedAt),
            InputsChangedAt = Utc(rows[0].InputsChangedAt),
        };

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
                     WHERE rd.DataChangedAt > {row.InputsAsOf}
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

        changed = [.. changed.Select(c => c with { ChangedAt = DateTime.SpecifyKind(c.ChangedAt, DateTimeKind.Utc) })];

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
            select new { version.Id, methodology.Code, version.Version, methodology.NameL10n })
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return labels.ToDictionary(l => l.Id, l => new MethodologyVersionLabel(l.Id, l.Code, l.Version, l.NameL10n));
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
    {
        // ⛔ L3-01: `SqlQuery<…>` віддає `datetime2` з `Kind = Unspecified`; `SpecifyKind` — до JSON, інакше без «Z».
        var entries = await db.Database
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

        return [.. entries.Select(e => e with { ChangedAt = DateTime.SpecifyKind(e.ChangedAt, DateTimeKind.Utc) })];
    }

    private static DateTime? Utc(DateTime? value)
        => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    /// <summary>Рядок запиту свіжості.</summary>
    /// <remarks>
    /// <c>InputsAsOf</c> — <c>COALESCE(InputsAsOfUtc, StartedAt)</c>: момент, відколи довідник вважається зміненим
    /// (N2-03; прогін із перенесеними результатами мірить від давнішого моменту, ніж власний старт). Правки комірок
    /// (<c>InputsChangedAt</c>) лишаються від <c>StartedAt</c>: власні аркуші прогін щойно перерахував.
    /// </remarks>
    public sealed record FreshnessRow(
        long RunId, DateTime StartedAt, DateTime InputsAsOf, DateTime? CalculatedAt, DateTime? InputsChangedAt);

    /// <summary>Довідник, змінений після прогону (RT-25).</summary>
    public sealed record RegistryChangeRow(string Code, DateTime ChangedAt);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Сирий SQL, а не LINQ: потрібен і архів <c>arc.CalculationResult</c> — запит без фільтрів за статусом
    /// прогону, той самий предикат, що й у <c>MethodologyVersionDeletionStore</c>. ✎ <c>D-560</c>: рядків витіснених і
    /// невдалих прогонів у <c>calc.CalculationResult</c> після прибирання немає — гард бачить лише актуальні (і
    /// працюючі) прогони та архів.
    /// </remarks>
    public async Task<bool> AnyCalculationResultsAsync(
        IReadOnlyCollection<int> methodologyVersionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyVersionIds);

        foreach (var id in methodologyVersionIds.Distinct())
        {
            var used = await db.Database
                .SqlQuery<int>($"""
                    SELECT CASE WHEN EXISTS (SELECT 1 FROM calc.CalculationResult WHERE MethodologyVersionId = {id})
                                  OR EXISTS (SELECT 1 FROM arc.CalculationResult WHERE MethodologyVersionId = {id})
                                THEN 1 ELSE 0 END AS Value
                    """)
                .SingleAsync(ct)
                .ConfigureAwait(false);

            if (used == 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetTraceStepMethodologyIdsAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyIds);
        if (methodologyIds.Count == 0)
        {
            return [];
        }

        var ids = methodologyIds.Distinct().ToList();
        return await db.Methodologies
            .AsNoTracking()
            .Where(m => m.TraceSteps && ids.Contains(m.Id))
            .Select(m => m.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

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

            // Аудит ent3, P2: тести, записані до перевірки форми (`"{}"`), мають Arguments = null,
            // і симуляція падала 500 на прогоні. Без аргументів прогін дасть червоний вердикт, а не збій.
            if (input.Arguments is null)
            {
                input = input with { Arguments = [] };
            }
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
