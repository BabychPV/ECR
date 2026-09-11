using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Кеш метаданих шаблону.
/// </summary>
/// <remarks>
/// Ключ <c>v{id}:r{rev}</c> робить інвалідацію **непотрібною**: презентаційна
/// правка створює новий ключ, а не псує старий. Це прибирає когерентність кешу
/// між інстансами як клас проблеми — і саме тому ≥2 інстанси тут дешеві (D-16).
/// </remarks>
public sealed class MetadataCache(IMemoryCache memory, EcrDbContext db) : IMetadataCache
{
    /// <summary>
    /// Стеля життя запису.
    /// </summary>
    /// <remarks>
    /// Так само, як у <see cref="RegistryEntryCache"/> і
    /// <see cref="AccessProfileCache"/>: не для коректності — її тримає
    /// ревізія в ключі (D-16), — а для пам'яті. Без стелі знімок, кешований
    /// на ревізії, що потім лишилася осторонь поточного вікна
    /// <c>InvalidateAsync</c> (кілька презентаційних правок поспіль без
    /// Publish/міграції — кожна лише зсуває <c>PresentationRevision</c>, не
    /// обов'язково викликаючи інвалідацію), живе в процесі НАЗАВЖДИ: ні TTL,
    /// ні <c>SizeLimit</c> його не витіснять. 30 хв — довше за 15 хв
    /// <see cref="RegistryEntryCache"/>, бо перебудова тут важча (шість
    /// запитів на знімок структури проти одного списку записів довідника) і
    /// сесія редагування версії шаблону триває порівнянно з користувацькою
    /// сесією, для якої <see cref="AccessProfileCache"/> уже бере ті самі
    /// 30 хв.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <inheritdoc />
    public async Task<TemplateVersionSnapshot> GetAsync(int templateVersionId, CancellationToken ct)
    {
        // Один легкий запит по ключу — саме він і робить схему з ключем
        // працездатною: ревізію треба знати ДО того, як шукати в кеші.
        // Дешевше за перечитування всієї структури на кожен запит рівно
        // настільки, наскільки одне число дешевше за сотні рядків.
        var revision = await db.TemplateVersions
            .AsNoTracking()
            .Where(v => v.Id == templateVersionId)
            .Select(v => (int?)v.PresentationRevision)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (revision is null)
        {
            throw new InvalidOperationException(
                $"Версії шаблону {templateVersionId} не існує.");
        }

        var key = CacheKey(templateVersionId, revision.Value);
        memory.Set(RevisionKey(templateVersionId), revision.Value, new MemoryCacheEntryOptions
        {
            Size = 1,
            Priority = CacheItemPriority.High,
        });

        if (memory.TryGetValue(key, out TemplateVersionSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        var snapshot = await LoadAsync(templateVersionId, revision.Value, ct).ConfigureAwait(false);

        // Термін не для коректності — її й так тримає ревізія в ключі, запис
        // не «застаріває» доти, доки він там лежить, — а для пам'яті (Q-252):
        // без стелі знімок ревізії, що випала з вузького вікна
        // InvalidateAsync (кілька презентаційних правок поспіль без
        // Publish/міграції), лишався б у процесі назавжди.
        memory.Set(key, snapshot, new MemoryCacheEntryOptions
        {
            Size = 1,
            Priority = CacheItemPriority.High,
            AbsoluteExpirationRelativeToNow = Lifetime,
        });

        return snapshot;
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(int templateVersionId, CancellationToken ct)
    {
        // Потрібно лише після Publish і міграції: у звичайній роботі ключ
        // змінюється сам. Прибираємо і поточну ревізію, і сусідні — після
        // Publish у пам'яті може лишатися знімок попередньої.
        var revision = await db.TemplateVersions
            .AsNoTracking()
            .Where(v => v.Id == templateVersionId)
            .Select(v => (int?)v.PresentationRevision)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var current = revision ?? 0;
        for (var r = Math.Max(0, current - 1); r <= current + 1; r++)
        {
            memory.Remove(CacheKey(templateVersionId, r));
        }
    }

    /// <summary>
    /// Ключ, під яким лежить ПОТОЧНА ревізія версії.
    /// </summary>
    /// <remarks>
    /// Потрібен синхронному <c>ITemplateStructure</c>: щоб дістати знімок із
    /// кешу, треба знати ревізію, а вона живе в базі. Запис цього числа поруч
    /// зі знімком — єдиний спосіб уникнути синхронного запиту там, де його
    /// робити не можна.
    /// </remarks>
    /// <param name="templateVersionId">Версія шаблону.</param>
    public static string RevisionKey(int templateVersionId) => $"rev:{templateVersionId}";

    /// <summary>Ключ знімка: <c>v{id}:r{rev}</c> (D-16).</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="revision">Презентаційна ревізія.</param>
    public static string CacheKey(int templateVersionId, int revision)
        => $"v{templateVersionId}:r{revision}";

    /// <summary>
    /// Завантажує повну структуру версії.
    /// </summary>
    /// <remarks>
    /// Чотири запити замість одного з <c>Include</c>: <c>Include</c> по графу
    /// «аркуші → таблиці → колонки/рядки» дає декартів добуток, у якому кожен
    /// рядок таблиці повторюється стільки разів, скільки в ній колонок. На
    /// шаблоні в 24 аркуші це десятки тисяч зайвих рядків по мережі.
    /// </remarks>
    private async Task<TemplateVersionSnapshot> LoadAsync(
        int templateVersionId, int revision, CancellationToken ct)
    {
        var sheets = await db.SheetDefs
            .AsNoTracking()
            .Where(s => s.TemplateVersionId == templateVersionId && !s.IsDeleted)
            .OrderBy(s => s.Ordinal)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var sheetIds = sheets.Select(s => s.Id).ToArray();

        var tables = await db.TableDefs
            .AsNoTracking()
            .Where(t => sheetIds.Contains(t.SheetDefId) && !t.IsDeleted)
            .OrderBy(t => t.Ordinal)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var tableIds = tables.Select(t => t.Id).ToArray();

        var columns = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => tableIds.Contains(c.TableDefId) && !c.IsDeleted)
            .OrderBy(c => c.Ordinal)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var rows = await db.RowDefs
            .AsNoTracking()
            .Where(r => tableIds.Contains(r.TableDefId) && !r.IsDeleted)
            .OrderBy(r => r.Ordinal)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⛔ П'ятий запит — ПРАВИЛА ВАЛІДАЦІЇ, і без нього валідації не було
        // взагалі (директива №09 `W8`, `S-19`/`S-28`). `ValidateDocumentHandler`
        // читає рівно `table.ValidationRules` цього знімка; знімок їх не
        // вантажив, колекція завжди була порожня — і `POST …/validate`
        // відповідав «зауважень немає» на будь-яких даних, при будь-яких
        // заведених правилах. Відповідь при цьому виглядала як робота: `200`,
        // порожній список, зелений тост. Правила існували в базі, проходили
        // перевірку публікації (`PublishChecks` бере структуру іншим шляхом —
        // `ITemplateVersionStore.GetWithStructureAsync` з `Include`) і не
        // виконувалися ЖОДНОГО разу.
        var validationRules = await db.ValidationRules
            .AsNoTracking()
            .Where(r => tableIds.Contains(r.TableDefId))
            .OrderBy(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⛔ Шостий запит — ФОРМУЛИ ШАБЛОНУ, і без нього перерахунок формул
        // не рахував нічого (директива №09 `W6`/`W8`, `S-21`; `Q-158`/`Q-160`
        // — той самий клас прогалини, що й `ValidationRule` вище).
        // `RecalculationService` бере формули рівно звідси —
        // `tables.Values.SelectMany(t => t.Formulas...)` — а знімок їх не
        // вантажив: колонки, рядки, правила були, формул не було. Формула
        // зберігалася через `PUT .../formulas/column/{columnId}` (`W5.3`),
        // проходила перевірку публікації (той самий інший шлях завантаження
        // структури, що й для правил) і не рахувалася ЖОДНОГО разу.
        var formulas = await db.FormulaDefs
            .AsNoTracking()
            .Where(f => tableIds.Contains(f.TableDefId) && !f.IsDeleted)
            .OrderBy(f => f.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Граф збирається в пам'яті: так кожна сутність приїжджає рівно один
        // раз. Складається він доменними AddColumn/AddRow/AddTable, а не
        // окремим «швидким» шляхом: перевірки на дублікати кодів мають бути
        // тими самими, що й при побудові шаблону, інакше кеш став би єдиним
        // місцем, де неконсистентна структура проходить мовчки.
        var columnsByTable = columns.ToLookup(c => c.TableDefId);
        var rowsByTable = rows.ToLookup(r => r.TableDefId);
        var rulesByTable = validationRules.ToLookup(r => r.TableDefId);
        var formulasByTable = formulas.ToLookup(f => f.TableDefId);

        foreach (var table in tables)
        {
            foreach (var column in columnsByTable[table.Id])
            {
                table.AddColumn(column);
            }

            foreach (var row in rowsByTable[table.Id])
            {
                table.AddRow(row);
            }

            foreach (var rule in rulesByTable[table.Id])
            {
                table.AddValidationRule(rule);
            }

            foreach (var formula in formulasByTable[table.Id])
            {
                table.AddFormula(formula);
            }
        }

        var tablesBySheet = tables.ToLookup(t => t.SheetDefId);
        foreach (var sheet in sheets)
        {
            foreach (var table in tablesBySheet[sheet.Id])
            {
                sheet.AddTable(table);
            }
        }

        return new TemplateVersionSnapshot(
            templateVersionId,
            revision,
            sheets,
            columns.ToDictionary(c => c.Id),
            rows.ToDictionary(r => (r.TableDefId, r.RowKeyValue)));
    }
}
