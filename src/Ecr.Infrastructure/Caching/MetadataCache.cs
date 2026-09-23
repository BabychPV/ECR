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
public sealed class MetadataCache(
    IMemoryCache memory,
    EcrDbContext db,
    CacheLifetimes? lifetimes = null,
    SingleFlight<TemplateVersionSnapshot>? flight = null) : IMetadataCache
{
    /// <summary>
    /// Спільний «один політ на ключ».
    /// </summary>
    /// <remarks>
    /// ⛔ Приходить ПАРАМЕТРОМ і мусить бути СПІЛЬНИМ на процес, бо сам
    /// <c>MetadataCache</c> — <c>Scoped</c>: на кожен HTTP-запит свій
    /// екземпляр. Власний словник у кожному екземплярі не злив би нічого —
    /// саме той напівстан, коли код прийому є, а прийому немає.
    ///
    /// ⚠ Дефолт (<c>new</c>) лишений заради прямих <c>new MetadataCache(...)</c>
    /// у тестах: контейнер значень за замовчуванням не застосовує, а
    /// <c>DependencyInjection</c> передає зареєстрований
    /// <c>SingleFlight&lt;TemplateVersionSnapshot&gt;</c> явно. Тест злиття
    /// (`RD-05`) будує екземпляри рівно так, як контейнер, — інакше він довів
    /// би роботу того, чого в продукті немає.
    /// </remarks>
    private readonly SingleFlight<TemplateVersionSnapshot> _flight =
        flight ?? new SingleFlight<TemplateVersionSnapshot>();

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
    ///
    /// ⚠ 30 хв — це ДЕФОЛТ, а не константа: значення береться з
    /// <c>Cache:MetadataSlidingMinutes</c> (`S-13`). Ключ був у
    /// <c>appsettings.json</c> без читача, тобто виставлені там 240 хв не
    /// діяли.
    /// </remarks>
    private TimeSpan Lifetime => (lifetimes ?? CacheLifetimes.Default).Metadata;

    /// <summary>Вікно, у якому ревізія не перечитується з бази (`RD-05`).</summary>
    private TimeSpan RevisionWindow => (lifetimes ?? CacheLifetimes.Default).Revision;

    /// <inheritdoc />
    public async Task<TemplateVersionSnapshot> GetAsync(int templateVersionId, CancellationToken ct)
    {
        var revision = await RevisionAsync(templateVersionId, ct).ConfigureAwait(false);
        var key = CacheKey(templateVersionId, revision);

        if (memory.TryGetValue(key, out TemplateVersionSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        // ⛔ Промах ключа — це НЕ «піти й побудувати». Між `TryGetValue` і
        // `Set` тут нічого не стояло, і на холодному ключі N одночасних
        // запитів давали N побудов по шість запитів кожна (`RD-05`, вада `D`).
        // Злиття робить із них одну; решта чекають її результату.
        return await _flight
            .RunAsync(key, token => BuildAsync(templateVersionId, revision, key, token), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Будує знімок і кладе його в кеш — усередині одного польоту.</summary>
    private async Task<TemplateVersionSnapshot> BuildAsync(
        int templateVersionId, int revision, string key, CancellationToken ct)
    {
        // Повторна перевірка вже всередині польоту: поки ми ставали в чергу,
        // попередній політ міг завершитися і покласти готове.
        if (memory.TryGetValue(key, out TemplateVersionSnapshot? ready) && ready is not null)
        {
            return ready;
        }

        var snapshot = await LoadAsync(templateVersionId, revision, ct).ConfigureAwait(false);

        // Термін не для коректності — її й так тримає ревізія в ключі, запис
        // не «застаріває» доти, доки він там лежить, — а для пам'яті (Q-252):
        // без стелі знімок ревізії, що випала з вузького вікна
        // InvalidateAsync (кілька презентаційних правок поспіль без
        // Publish/міграції), лишався б у процесі назавжди.
        //
        // ⚠ `Size` тут БІЛЬШЕ НЕМАЄ, і це не недогляд — див. пояснення біля
        // `AddMemoryCache` у `DependencyInjection.cs`: ліміту в сховища немає,
        // а розмір без ліміту не обмежує нічого і лише вдає стелю.
        memory.Set(key, snapshot, new MemoryCacheEntryOptions
        {
            Priority = CacheItemPriority.High,
            AbsoluteExpirationRelativeToNow = Lifetime,
        });

        return snapshot;
    }

    /// <summary>
    /// Поточна ревізія версії — з мемоїзацією на <see cref="RevisionWindow"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ До `RD-05` це був запит до <c>cfg.TemplateVersion</c> на КОЖЕН
    /// <c>GetAsync</c>, а на один зріз таблиці викликів ≥ 2. Прийом узятий
    /// дослівно з <c>SecurityStampValidator</c>: п'ять секунд у спільному
    /// <see cref="IMemoryCache"/>, після яких число перечитується.
    ///
    /// ⚠ Що саме стає несвіжим. Ключ <c>v{id}:r{rev}</c> лишається чесним —
    /// просто новий <c>rev</c> помічається не миттєво, а протягом вікна.
    /// Тобто ПРЕЗЕНТАЦІЙНА правка чернетки доходить до читача за ≤ 5 с. Для
    /// <c>Publish</c> і міграції цього мало, тому
    /// <see cref="InvalidateAsync"/> знімає й мемоїзоване число.
    ///
    /// ⚠ Відсутність версії НЕ мемоїзується: нуль користі (шлях однаково
    /// кидає) і зайва морока з <c>null</c> у сховищі значень.
    /// </remarks>
    private async Task<int> RevisionAsync(int templateVersionId, CancellationToken ct)
    {
        var window = RevisionWindow;
        var key = RevisionKey(templateVersionId);

        if (window > TimeSpan.Zero && memory.TryGetValue(key, out int memoized))
        {
            return memoized;
        }

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

        if (window > TimeSpan.Zero)
        {
            memory.Set(key, revision.Value, new MemoryCacheEntryOptions
            {
                Priority = CacheItemPriority.High,
                AbsoluteExpirationRelativeToNow = window,
            });
        }

        return revision.Value;
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

        // ⛔ Мемоїзоване число знімається ПЕРШИМ. Publish і міграція — рівно ті
        // випадки, де стеля несвіжості в 5 с недопустима, і лишити тут старе
        // число означало б, що явна інвалідація не діє ці п'ять секунд.
        memory.Remove(RevisionKey(templateVersionId));

        var current = revision ?? 0;
        for (var r = Math.Max(0, current - 1); r <= current + 1; r++)
        {
            memory.Remove(CacheKey(templateVersionId, r));
        }
    }

    /// <summary>
    /// Ключ, під яким лежить мемоїзована ревізія версії (`RD-05`).
    /// </summary>
    /// <remarks>
    /// ⚠ Раніше це число клалося в кеш НАЗАВЖДИ і на КОЖЕН виклик — заради
    /// синхронного <c>ITemplateStructure</c>, у якого не було жодного
    /// споживача (`Q-192`, `AR-06`). Порт і його реалізація прибрані разом із
    /// цим записом; ключ лишився, але вже зі строком і в іншій ролі — вікно, у
    /// якому ревізію не перечитують.
    /// </remarks>
    /// <param name="templateVersionId">Версія шаблону.</param>
    private static string RevisionKey(int templateVersionId) => $"rev:{templateVersionId}";

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

        // Сьомий запит — ПОЛЯ ШАПКИ ДОКУМЕНТА. Рівень версії, не таблиці,
        // тому фільтр — за TemplateVersionId, а не за tableIds (на відміну
        // від решти шести запитів вище).
        var headerFields = await db.HeaderFieldDefs
            .AsNoTracking()
            .Where(f => f.TemplateVersionId == templateVersionId && !f.IsDeleted)
            .OrderBy(f => f.Ordinal)
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
            rows.ToDictionary(r => (r.TableDefId, r.RowKeyValue)))
        {
            HeaderFields = headerFields,
        };
    }
}
