using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IOrphanScanner"/>: ставить і **знімає**
/// <c>doc.TableRow.IsOrphaned</c> (ФВ-8.13a, D-98).
/// </summary>
/// <remarks>
/// ⚠ Правило вирішує <see cref="OrphanScanPlan"/>, не цей клас. Тут — лише
/// вибірка кандидатів і запис результату. Розділення не косметичне: правило
/// перевіряється тестами без бази, а запис — це один <c>UPDATE</c> на десятки
/// тисяч рядків, який інакше довелося б перевіряти вручну на живому SQL.
/// <para>
/// Чинність запису рахується <see cref="RegistryResolver.IsSelectable"/> — тим
/// самим методом, що формує випадний список. Окрема, «майже така сама» умова
/// тут означала б, що рядок зі значенням, яке користувач щойно обрав зі
/// списку, оголошується осиротілим.
/// </para>
/// <para>
/// ⛔ ПОКРИТТЯ. Прохід іде ПАЧКАМИ за курсором <c>itg.ScanCursor</c> у порядку
/// складеного первинного ключа <c>(PeriodKey, Id)</c> і продовжує з тієї
/// позиції, на якій спинився попередній. Так було не завжди: раніше тут стояв
/// РІВНО ОДИН запит <c>Take(20_000)</c> без <c>OrderBy</c>, без курсора й без
/// циклу. Порядок не заданий — отже, вікно обирав оптимізатор, і воно було
/// довільним; усе, що до нього не потрапило, не перевірялося НІКОЛИ, хоч
/// таблиця розрахована на ~108 млн рядків на рік. Задача при цьому звітувала
/// про успіх в обох випадках. Це той самий клас дефекту, що й предикат без
/// <c>PeriodKey</c>, виправлений поруч: перевірка, яка не може провалитися, бо
/// майже не дивиться.
/// </para>
/// </remarks>
public sealed class OrphanScanner : IOrphanScanner
{
    /// <summary>Скільки відкритих періодів береться до розгляду за раз.</summary>
    /// <remarks>
    /// Відкритих періодів у системі — одиниці й десятки (по одному на проєкт,
    /// що звітує). Межа тут не ділить роботу на порції, як бюджет рядків, — це
    /// запобіжник проти запиту без межі взагалі.
    /// </remarks>
    private const int MaxOpenPeriods = 20_000;

    /// <summary>Скільки записів довідника читається одним запитом.</summary>
    /// <remarks>
    /// ⚠ Читання ЧАНКАМИ, а не одним <c>Take</c> зі стелею. Так було не
    /// завжди: раніше тут стояв <c>Take(20_000)</c> по переліку
    /// ідентифікаторів — і якби різних записів довідника виявилося більше,
    /// зайві мовчки не завантажилися б, <c>TryGetValue</c> повернув би
    /// <c>false</c>, а рядок із ЦІЛКОМ ЧИННИМ посиланням був би оголошений
    /// осиротілим і заблокував би <c>Submit</c>. Чанк не має стелі, за якою
    /// дані зникають: він має лише розмір.
    /// </remarks>
    private const int EntryChunkSize = 1_000;

    /// <summary>Скільки рядків добирає свої посилання одним запитом.</summary>
    /// <remarks>
    /// ⚠ Порція рядків, а не порція комірок, і саме тому окрема константа: цей
    /// запит засікається по <c>(PeriodKey, TableRowId)</c>, тобто його ціна
    /// пропорційна кількості РЯДКІВ у переліку, а не кількості їхніх комірок.
    /// </remarks>
    private const int RowChunkSize = 500;

    /// <summary>
    /// Стеля посилань на довідник в ОДНОМУ рядку — запобіжник добору.
    /// </summary>
    /// <remarks>
    /// ⛔ Стеля тут не ділить роботу на порції: вона ловить неможливе. Посилань
    /// у рядку не більше, ніж колонок у таблиці; тисяча колонок — це вже не
    /// форма звітності, а зіпсовані дані. Перевищення кидає виняток, а не
    /// мовчки відрізає хвіст: відрізаний хвіст означав би рівно той дефект,
    /// який добір і лікує, — рішення по ЧАСТИНІ посилань рядка.
    /// </remarks>
    private const int MaxRowReferences = 1_000;

    private readonly EcrDbContext _db;
    private readonly RegistryResolver _resolver;
    private readonly IClock _clock;
    private readonly OrphanScanBudget _budget;

    /// <summary>Створює сканер.</summary>
    /// <param name="db">Контекст бази.</param>
    /// <param name="resolver">Правило чинності записів довідника.</param>
    /// <param name="clock">Годинник.</param>
    /// <param name="budget">
    /// Бюджет одного прогону; <c>null</c> — <see cref="OrphanScanBudget.Nightly"/>.
    /// </param>
    /// <remarks>
    /// ⚠ Бюджет — параметр, а не константа, саме щоб він був ПЕРЕВІРНИМ.
    /// Довести, що наступна ніч бере наступну порцію, можна лише зробивши
    /// порцію меншою за набір; із зашитими <c>500 000</c> для цього довелося б
    /// засівати півмільйона рядків, тобто такого тесту не було б узагалі.
    /// </remarks>
    public OrphanScanner(
        EcrDbContext db, RegistryResolver resolver, IClock clock, OrphanScanBudget? budget = null)
    {
        _db = db;
        _resolver = resolver;
        _clock = clock;
        _budget = budget ?? OrphanScanBudget.Nightly;
    }

    /// <inheritdoc />
    public async Task<OrphanScanSummary> ScanAllAsync(CancellationToken ct)
    {
        var periods = await OpenPeriodsAsync(ct).ConfigureAwait(false);
        if (periods.Count == 0)
        {
            return OrphanScanSummary.Nothing;
        }

        var cursor = await LoadCursorAsync(ct).ConfigureAwait(false);

        var startedAt = _clock.UtcNow;
        var position = new OrphanRowRef(cursor.PeriodKeyValue, cursor.RowId);
        var examinedRows = 0;
        var changed = 0;
        var reachedEnd = false;

        while (examinedRows < _budget.RowBudget)
        {
            var batch = await NextBatchAsync(periods, position, registryEntryId: null, ct)
                .ConfigureAwait(false);

            if (batch.Rows.Count == 0)
            {
                // Хвіст набору порожній: далі дивитися нема на що.
                reachedEnd = true;
                break;
            }

            changed += await ApplyAsync(batch.Rows, ct).ConfigureAwait(false);
            examinedRows += batch.Rows.Count;
            position = batch.Last;

            // ⛔ Курсор зсувається ПІСЛЯ запису пачки, а не до нього. Порядок
            // не косметичний: процес, убитий посеред пачки, лишає курсор на
            // початку цієї пачки, і наступна ніч просто повторить її. Зворотний
            // порядок означав би пропуск — рівно ту діру, заради якої курсор і
            // заводиться. Повтор безпечний, бо ознака РАХУЄТЬСЯ з даних, а не
            // накопичується: та сама пачка вдруге дає той самий результат.
            cursor.AdvanceTo(position.PeriodKeyValue, position.RowId, _clock.UtcNow);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (batch.IsLast)
            {
                reachedEnd = true;
                break;
            }

            // ⚠ Часовий бюджет перевіряється НА МЕЖІ ПАЧКИ, а не всередині
            // запису. Обірвати прогін між пачками означає лишити узгоджений
            // стан і чесну позицію; обірвати його посеред `UPDATE` означало б
            // покластися на те, що наступна ніч вгадає, де саме зупинилися.
            if (_clock.UtcNow - startedAt >= _budget.TimeBudget)
            {
                break;
            }
        }

        if (reachedEnd)
        {
            // ⚠ Дійшли до кінця — обертаємося на початок, а не залишаємося
            // стояти. Набір живий: рядки з'являються позаду курсора, періоди
            // відкриваються й закриваються, і курсор, що дійшов до кінця й там
            // застиг, більше не оглянув би нічого ніколи.
            cursor.CompleteCycle(_clock.UtcNow);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // ⚠ Лічильники читаються з курсора ПІСЛЯ можливого `CompleteCycle` —
        // інакше ніч, яка щойно замкнула обхід, звітувала б числом до
        // замикання, тобто найцікавіший факт губився б рівно тоді, коли він
        // з'являється.
        return new OrphanScanSummary(
            changed, examinedRows, reachedEnd, cursor.CyclesCompleted, cursor.LastCycleCompletedAt);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ БЕЗ курсора й БЕЗ бюджету ночі, і це не непослідовність. Нічний
    /// прохід — обхід усього набору, який має право розтягтися на кілька ночей;
    /// цей — точковий перерахунок рядків ОДНОГО запису довідника, і він
    /// зобов'язаний завершитися ЦІЛКОМ. Недороблений перерахунок означає
    /// користувача, який виправив довідник і лишився із заблокованим
    /// <c>Submit</c> без причини. Обсяг тут задає не таблиця, а один запис, і
    /// пачками він проходиться до кінця.
    /// </remarks>
    public async Task<int> RescanForEntryAsync(long registryEntryId, CancellationToken ct)
    {
        var periods = await OpenPeriodsAsync(ct).ConfigureAwait(false);
        if (periods.Count == 0)
        {
            return 0;
        }

        var position = new OrphanRowRef(ScanCursor.StartPeriodKeyValue, ScanCursor.StartRowId);
        var changed = 0;

        while (true)
        {
            var batch = await NextBatchAsync(periods, position, registryEntryId, ct)
                .ConfigureAwait(false);

            if (batch.Rows.Count == 0)
            {
                return changed;
            }

            changed += await ApplyAsync(batch.Rows, ct).ConfigureAwait(false);
            position = batch.Last;

            if (batch.IsLast)
            {
                return changed;
            }
        }
    }

    /// <summary>
    /// Періоди, які взагалі перевіряються.
    /// </summary>
    /// <remarks>
    /// Закриті не чіпаються: їхні дані вже подані, ознака нічого не розблокує
    /// і не заборонить (ФВ-8.13a).
    /// </remarks>
    private async Task<PeriodScope> OpenPeriodsAsync(CancellationToken ct)
    {
        var periods = await _db.Periods
            .AsNoTracking()
            .Where(p => p.State == PeriodState.Open || p.State == PeriodState.Grace)
            .Select(p => new { p.PeriodKeyValue, p.State, p.PeriodEnd })
            .Take(MaxOpenPeriods)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⚠ `PeriodKeyValue` (`Year*100+Sequence`) унікальний У МЕЖАХ ОДНОГО
        // проєкту, а не глобально: два РІЗНІ проєкти (обидва `Monthly`) цілком
        // законно мають відкритий період за той самий календарний місяць
        // одночасно — кілька проєктів звітують паралельно, це не крайовий
        // випадок. `GroupBy` тут — ВІДОМЕ спрощення, а не повне рішення:
        // сканер бере ОДНЕ подання дати/стану на ключ, бо `TableRow`/`CellValue`
        // несуть лише `PeriodKeyValue`, не `ProjectId` — без нього зіставити
        // рядок із ПРАВИЛЬНИМ періодом серед кількох однойменних неможливо.
        // Без угруповання нижче `ToDictionary` кидав `ArgumentException` на
        // дублікаті ключа, щойно в системі існувало більше одного відкритого
        // проєкту на той самий період, — сканер ПАДАВ на цілком звичайному
        // стані, а не на межовому. Повне рішення вимагає зіставляти рядок із
        // проєктом (`TableRow` → `TableInstance` → `Document` → `Project`), а
        // не лише з `PeriodKeyValue`; поза межами цього фіксу.
        var distinct = periods
            .GroupBy(p => p.PeriodKeyValue)
            .Select(g => g.First())
            .ToList();

        return new PeriodScope(
            [.. distinct.Select(p => p.PeriodKeyValue)],

            // ⚠ Дата резолвінгу — КІНЕЦЬ періоду, а не «сьогодні». Запис,
            // чинний до 30 червня, лишається чинним для всього червневого
            // звіту, навіть якщо перевірка йде в жовтні (ФВ-8.5).
            distinct.ToDictionary(p => p.PeriodKeyValue, p => p.PeriodEnd),
            distinct.ToDictionary(p => p.PeriodKeyValue, p => p.State));
    }

    /// <summary>Читає курсор сканування; заводить його, якщо ще немає.</summary>
    private async Task<ScanCursor> LoadCursorAsync(CancellationToken ct)
    {
        var existing = await _db.ScanCursors
            .FirstOrDefaultAsync(c => c.ScanCode == _budget.CursorCode, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var created = new ScanCursor(_budget.CursorCode, _clock.UtcNow);
        _db.ScanCursors.Add(created);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return created;
    }

    /// <summary>
    /// Наступна пачка кандидатів після позиції <paramref name="after"/>.
    /// </summary>
    /// <param name="scope">Періоди, що перевіряються.</param>
    /// <param name="after">Позиція, ПІСЛЯ якої брати рядки.</param>
    /// <param name="registryEntryId">
    /// Звузити ВИБІРКУ РЯДКІВ до тих, що посилаються на цей запис довідника.
    /// Ознака при цьому все одно рахується по ВСІХ посиланнях відібраних
    /// рядків — див. <see cref="CompleteRowReferencesAsync"/>.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ ПАЧКА ЗАВЖДИ ЗАКІНЧУЄТЬСЯ НА МЕЖІ РЯДКА. Читання бере комірки
    /// (їх і треба обмежувати — ціна запиту саме в них), але правило
    /// «осиротілий, якщо ХОЧ ОДНЕ посилання нечинне» вимагає БАЧИТИ ВСІ
    /// посилання рядка одразу. Рядок, розрізаний межею читання навпіл, дав би
    /// відповідь по половині своїх комірок — і рядок із нечинним посиланням у
    /// другій половині оголосили б чинним. Тому: читаємо на одне посилання
    /// більше за стелю, і якщо стеля вибрана — відрізаємо ВЕСЬ останній рядок,
    /// бо саме він міг обрізатися. Курсор при цьому стає на останній ЦІЛИЙ
    /// рядок, і відрізаний прийде наступною пачкою.
    ///
    /// ⚠ Упорядкування <c>(PeriodKey, Id, ColumnDefId)</c> — це порядок
    /// кластерного індексу, а не «якийсь стабільний». Саме тому предикат
    /// «більше за позицію» перетворюється на засічку по діапазону, а не на
    /// сканування партицій.
    /// </remarks>
    private async Task<CandidateBatch> NextBatchAsync(
        PeriodScope scope, OrphanRowRef after, long? registryEntryId, CancellationToken ct)
    {
        var periodKeys = scope.PeriodKeys;

        var candidates =
            from cell in _db.CellValues.AsNoTracking()
            where cell.ValueRegistryEntryId != null
            join row in _db.TableRows.AsNoTracking()
                on new { P = cell.PeriodKeyValue, I = cell.TableRowId }
                equals new { P = row.PeriodKeyValue, I = row.Id }
            where !row.IsDeleted
                  && periodKeys.Contains(row.PeriodKeyValue)
                  && (row.PeriodKeyValue > after.PeriodKeyValue
                      || (row.PeriodKeyValue == after.PeriodKeyValue && row.Id > after.RowId))
            select new { cell, row };

        // ⛔ Фільтр на конкретний запис звужує запит ДО проєкції в іменований
        // `CellReference`, а не після неї. `SetEntryValidityHandler` (єдиний
        // виклик з `registryEntryId != null`) до цієї правки падав
        // `InvalidOperationException: … could not be translated`: EF Core не
        // вміє скласти `Where` НАД `Select` у ІМЕНОВАНИЙ record-тип назад у SQL
        // (на відміну від анонімного типу) — і виняток не ловить ніхто, він
        // доходить до `ExceptionHandlingMiddleware` голим `500 ECR-SYS-0500`.
        // `ScanAllAsync` цієї гілки ніколи не виконує і тому лишався зеленим.
        if (registryEntryId is { } single)
        {
            candidates = candidates.Where(x => x.cell.ValueRegistryEntryId == single);
        }

        var references = await candidates
            .OrderBy(x => x.row.PeriodKeyValue)
            .ThenBy(x => x.row.Id)
            .ThenBy(x => x.cell.ColumnDefId)
            .Select(x => new CellReference(
                x.row.Id, x.row.PeriodKeyValue, x.row.IsOrphaned, x.cell.ValueRegistryEntryId!.Value))
            .Take(_budget.BatchCells + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (references.Count == 0)
        {
            return CandidateBatch.Empty;
        }

        // Стелю не вибрано — хвіст набору прочитано цілком, останній рядок
        // гарантовано повний.
        var isLast = references.Count <= _budget.BatchCells;
        if (!isLast)
        {
            references = TrimTrailingRow(references);
        }

        var last = references[^1];

        // ⛔ ФІЛЬТР ОБИРАЄ РЯДКИ, А НЕ ВИРІШУЄ ЗА НИХ. Так було не завжди: до
        // цієї правки звужений фільтром перелік ішов в угруповання ЯК Є — отже,
        // правило «осиротілий, якщо ХОЧ ОДНЕ посилання нечинне» бачило рівно
        // ОДНЕ посилання рядка, те саме, по якому йшов перерахунок. Рядок, що
        // посилається і на щойно полагоджений запис A, і на досі нечинний B,
        // діставав `allValid = true` й потрапляв у `ToClear`: ознаку знімали,
        // хоч B лишався зламаним, і `Submit` переставав блокувати форму з
        // посиланням на запис довідника, що більше не діє. Дефекту не видно на
        // рядку з ОДНИМ посиланням — а саме такі рядки й перевіряли тести, бо
        // там «усі» і «те одне» збігаються.
        //
        // ⚠ Нічний прохід (`registryEntryId == null`) добору не потребує: він
        // і так читає ВСІ посилання рядків підряд, і межа пачки вже стоїть на
        // межі рядка (див. `TrimTrailingRow`).
        if (registryEntryId is not null)
        {
            references = await CompleteRowReferencesAsync(references, ct).ConfigureAwait(false);
        }

        var entryById = await EntriesAsync(references, ct).ConfigureAwait(false);

        // ⚠ Рядок осиротілий, якщо ХОЧ ОДНЕ його посилання нечинне. Саме «хоч
        // одне», а не «всі»: рядок із чинним дозволом і нечинним водним
        // об'єктом описує викид у місце, якого вже немає.
        var rows = references
            .GroupBy(r => (r.PeriodKeyValue, r.RowId))
            .Select(group =>
            {
                var asOf = scope.AsOf[group.Key.PeriodKeyValue];

                var allValid = group.All(r =>
                    entryById.TryGetValue(r.RegistryEntryId, out var entry)
                    && _resolver.IsSelectable(entry, asOf));

                return new OrphanCandidate(
                    new OrphanRowRef(group.Key.PeriodKeyValue, group.Key.RowId),
                    scope.State[group.Key.PeriodKeyValue],
                    group.First().IsOrphaned,
                    allValid);
            })
            .ToList();

        return new CandidateBatch(rows, new OrphanRowRef(last.PeriodKeyValue, last.RowId), isLast);
    }

    /// <summary>
    /// Добирає ПОВНИЙ набір посилань для рядків, які обрав фільтр по запису.
    /// </summary>
    /// <param name="selected">
    /// Посилання, відібрані фільтром: по одному на рядок (фільтр звужує до
    /// одного запису довідника), уже обрізані по межі рядка.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Усі посилання ТИХ САМИХ рядків, ознака <c>IsOrphaned</c> — та,
    /// що вже прочитана разом із рядком.</returns>
    /// <exception cref="InvalidOperationException">
    /// Порція рядків дала більше посилань, ніж дозволяє
    /// <see cref="MaxRowReferences"/> на рядок.
    /// </exception>
    /// <remarks>
    /// ⛔ ПЕРЕЛІКОМ КЛЮЧІВ, А НЕ ДІАПАЗОНОМ «від першого рядка пачки до
    /// останнього». Діапазон виглядає природніше — пачка й так упорядкована по
    /// кластерному ключу, — але між першим і останнім рядком пачки лежать
    /// рядки, які на цей запис НЕ посилаються, і їх там може бути скільки
    /// завгодно: посилання на один запис довідника розкидані по всьому набору.
    /// Це перетворило б точковий перерахунок на прохід усією таблицею
    /// (~108 млн рядків на рік) — на СИНХРОННОМУ шляху HTTP-запиту
    /// <c>POST …/entries/{id}/validity</c>. Перелік ключів засікається по
    /// <c>PK_CellValue (PeriodKey, TableRowId, ColumnDefId)</c> рівно в потрібні
    /// рядки, а порціями він іде з тієї ж причини, що й у
    /// <see cref="EntriesAsync"/>: щоб довжина <c>IN</c> не залежала від обсягу
    /// пачки.
    ///
    /// ⚠ Рядки групуються за періодом, бо <c>doc.CellValue</c> лежить на тій
    /// самій схемі партиціонування, що й <c>doc.TableRow</c>: предикат без
    /// <c>PeriodKey</c> не має чим засікатися й проходить усі партиції — той
    /// самий дефект, що описаний у <see cref="ApplyAsync"/>.
    ///
    /// ⚠ Повторного читання <c>doc.TableRow</c> тут немає навмисно: єдине, що
    /// добирається, — це посилання, а <c>IsOrphaned</c> уже прочитано разом із
    /// рядком у вибірці кандидатів. Другий join заради вже відомого поля
    /// коштував би ще одного проходу по <c>doc.TableRow</c>.
    /// </remarks>
    private async Task<List<CellReference>> CompleteRowReferencesAsync(
        List<CellReference> selected, CancellationToken ct)
    {
        var orphanedByRow = new Dictionary<(int PeriodKeyValue, long RowId), bool>();
        foreach (var reference in selected)
        {
            orphanedByRow[(reference.PeriodKeyValue, reference.RowId)] = reference.IsOrphaned;
        }

        var complete = new List<CellReference>(selected.Count);

        foreach (var byPeriod in orphanedByRow.Keys.GroupBy(k => k.PeriodKeyValue))
        {
            var periodKey = byPeriod.Key;

            foreach (var chunk in byPeriod.Select(k => k.RowId).Chunk(RowChunkSize))
            {
                var rowIds = chunk;
                var ceiling = rowIds.Length * MaxRowReferences;

                var cells = await _db.CellValues
                    .AsNoTracking()
                    .Where(c => c.PeriodKeyValue == periodKey
                                && rowIds.Contains(c.TableRowId)
                                && c.ValueRegistryEntryId != null)
                    .OrderBy(c => c.TableRowId)
                    .ThenBy(c => c.ColumnDefId)
                    .Select(c => new RowEntryRef(c.TableRowId, c.ValueRegistryEntryId!.Value))
                    .Take(ceiling + 1)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                if (cells.Count > ceiling)
                {
                    throw new InvalidOperationException(
                        $"Період {periodKey}: {rowIds.Length} рядк(ів) дали понад {ceiling} посилань "
                        + $"на довідник, тобто більше за {MaxRowReferences} на рядок — "
                        + "добір не може гарантувати повний набір посилань рядка.");
                }

                foreach (var cell in cells)
                {
                    complete.Add(new CellReference(
                        cell.RowId,
                        periodKey,
                        orphanedByRow[(periodKey, cell.RowId)],
                        cell.RegistryEntryId));
                }
            }
        }

        return complete;
    }

    /// <summary>
    /// Відрізає останній — можливо, обрізаний межею читання — рядок.
    /// </summary>
    /// <param name="references">Посилання, впорядковані за ключем рядка.</param>
    /// <exception cref="InvalidOperationException">
    /// Усі прочитані посилання належать одному рядку: пачка не змогла б
    /// просунутися ні на крок.
    /// </exception>
    /// <remarks>
    /// ⛔ Виняток, а не тихе «обробимо, що прочитали». Якщо єдиний рядок має
    /// більше посилань, ніж уміщає стеля читання, то мовчазна обробка половини
    /// його комірок дала б НЕПРАВИЛЬНУ ознаку (див. правило «хоч одне
    /// нечинне»), а курсор застряг би на місці — сканування зупинилося б
    /// назавжди, звітуючи про успіх щоночі. Це рівно той дефект, який
    /// виправляє весь цей клас, тож ховати його тут було б безглуздо.
    /// Практично ця межа недосяжна: посилань у рядку не більше, ніж колонок у
    /// таблиці, а стеля читання — двадцять тисяч.
    /// </remarks>
    private static List<CellReference> TrimTrailingRow(List<CellReference> references)
    {
        var tail = references[^1];
        var kept = references
            .Where(r => r.PeriodKeyValue != tail.PeriodKeyValue || r.RowId != tail.RowId)
            .ToList();

        return kept.Count > 0
            ? kept
            : throw new InvalidOperationException(
                $"Рядок ({tail.PeriodKeyValue}, {tail.RowId}) має більше посилань на довідник, "
                + "ніж уміщає одне читання сканера: пачка не може просунутися.");
    }

    /// <summary>Записи довідника, на які посилається пачка.</summary>
    private async Task<Dictionary<long, RegistryEntry>> EntriesAsync(
        List<CellReference> references, CancellationToken ct)
    {
        var entryIds = references.Select(r => r.RegistryEntryId).Distinct().ToList();
        var entryById = new Dictionary<long, RegistryEntry>(entryIds.Count);

        foreach (var chunk in entryIds.Chunk(EntryChunkSize))
        {
            var ids = chunk;

            var entries = await _db.RegistryEntries
                .AsNoTracking()
                .Where(e => ids.Contains(e.Id))
                .Take(ids.Length)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var entry in entries)
            {
                entryById[entry.Id] = entry;
            }
        }

        return entryById;
    }

    /// <summary>Записує рішення по пачці. Повертає кількість змінених рядків.</summary>
    /// <remarks>
    /// Set-based, не рядок за рядком: рядків тут — тисячі.
    ///
    /// ⚠ Директива №11 §4, пункт `#49` (`Q-193`): нижче навмисно НЕ
    /// кличеться `TableRow.SetOrphaned` — завантаження кожного рядка через
    /// EF заради одного виклику домену звело б пакетну операцію до
    /// рядок-за-рядком на обсязі, для якого весь клас і писався. Обидва
    /// `SetProperty`-вирази нижче тримають ТОЙ САМИЙ інваріант, що описує
    /// `TableRow.SetOrphaned` («`OrphanedAt` нульується разом з ознакою») —
    /// звіряй їх із визначенням там, коли міняєш один із двох.
    ///
    /// ⛔ І ПО ОДНОМУ `UPDATE` НА ПЕРІОД, а не по одному на всю пачку. Так
    /// було не завжди: раніше тут стояло `Where(r => decision.ToFlag
    /// .Contains(r.Id))` — без `PeriodKey`. Первинний ключ `doc.TableRow` —
    /// складений `(PeriodKey, Id)`, таблиця лежить на `ps_ByPeriodKey`, і
    /// жодного індексу з `Id` попереду немає й бути не може:
    /// `07-partition-tables.sql` вирівнює кожен індекс цих таблиць по схемі
    /// партиціонування й падає (`THROW 50031`), якщо хоч один лишився поза
    /// нею. Тобто предикату не було чим засікатися, і кожен із двох
    /// `UPDATE` щоночі проходив УСІ партиції таблиці.
    ///
    /// Заміряно на окремій базі, 4.8 млн рядків / 24 партиції (`sqlcmd`,
    /// той самий стенд, що й для `RowStore.TouchRowsAsync`), 500 `Id`
    /// одного періоду:
    ///   було:  Index Scan (UQ_TableRow_Key), scan count 25, 28 844 читання
    ///   стало: Clustered Index Seek (PK_TableRow) з RangePartitionNew,
    ///          scan count 0, 1 500 читань
    ///
    /// Період тепер несе сам план (`OrphanRowRef`), і саме тому група нижче —
    /// законна: одна пачка може ПЕРЕТИНАТИ межу періоду (курсор іде набором
    /// підряд, а не по одному періоду за раз), тож звести все до одного
    /// `PeriodKey` у `WHERE` неможливо.
    /// </remarks>
    private async Task<int> ApplyAsync(IReadOnlyList<OrphanCandidate> rows, CancellationToken ct)
    {
        var decision = OrphanScanPlan.Plan(rows);
        if (decision.Total == 0)
        {
            return 0;
        }

        var now = _clock.UtcNow;

        foreach (var byPeriod in decision.ToFlag.GroupBy(r => r.PeriodKeyValue))
        {
            var periodKey = byPeriod.Key;
            var rowIds = byPeriod.Select(r => r.RowId).ToList();

            await _db.TableRows
                .Where(r => r.PeriodKeyValue == periodKey && rowIds.Contains(r.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.IsOrphaned, true)
                          .SetProperty(r => r.OrphanedAt, now),
                    ct)
                .ConfigureAwait(false);
        }

        foreach (var byPeriod in decision.ToClear.GroupBy(r => r.PeriodKeyValue))
        {
            var periodKey = byPeriod.Key;
            var rowIds = byPeriod.Select(r => r.RowId).ToList();

            // ⚠ OrphanedAt зануляється разом із ознакою. Лишити його означало б
            // мати рядок, який «колись був осиротілим» і виглядає як осиротілий
            // у будь-якому звіті, що дивиться на дату, а не на прапорець.
            await _db.TableRows
                .Where(r => r.PeriodKeyValue == periodKey && rowIds.Contains(r.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.IsOrphaned, false)
                          .SetProperty(r => r.OrphanedAt, (DateTime?)null),
                    ct)
                .ConfigureAwait(false);
        }

        return decision.Total;
    }

    /// <summary>Періоди, що перевіряються, з їхніми датами й станами.</summary>
    private sealed record PeriodScope(
        List<int> PeriodKeys,
        Dictionary<int, DateOnly> AsOf,
        Dictionary<int, PeriodState> State)
    {
        /// <summary>Скільки періодів у розгляді.</summary>
        public int Count => PeriodKeys.Count;
    }

    /// <summary>
    /// Одна пачка кандидатів і позиція, на яку після неї стає курсор.
    /// </summary>
    /// <param name="Rows">Рядки пачки; кожен — із ПОВНИМ набором посилань.</param>
    /// <param name="Last">Останній рядок пачки: нова позиція курсора.</param>
    /// <param name="IsLast">
    /// Пачка вичерпала набір — далі за нею кандидатів немає.
    /// </param>
    private sealed record CandidateBatch(
        IReadOnlyList<OrphanCandidate> Rows, OrphanRowRef Last, bool IsLast)
    {
        /// <summary>Порожня пачка: набір скінчився рівно на позиції курсора.</summary>
        public static CandidateBatch Empty { get; } =
            new([], new OrphanRowRef(ScanCursor.StartPeriodKeyValue, ScanCursor.StartRowId), IsLast: true);
    }

    /// <summary>Одне посилання рядка на запис довідника.</summary>
    /// <remarks>
    /// Названий тип, а не анонімний: анонімний розриває вираз фігурною дужкою,
    /// і архітектурне правило «<c>ToListAsync</c> без <c>Take</c>» бачить
    /// половину інструкції без межі (`D1-08`).
    /// </remarks>
    private sealed record CellReference(
        long RowId, int PeriodKeyValue, bool IsOrphaned, long RegistryEntryId);

    /// <summary>Посилання рядка на запис довідника в межах одного періоду.</summary>
    /// <remarks>
    /// Період тут не повторюється в кожному елементі: добір іде ПО ОДНОМУ
    /// періоду за раз (див. <see cref="CompleteRowReferencesAsync"/>), тож він
    /// відомий на місці виклику. Тип названий, а не анонімний, з тієї ж
    /// причини, що й <see cref="CellReference"/> (`D1-08`).
    /// </remarks>
    private sealed record RowEntryRef(long RowId, long RegistryEntryId);
}
