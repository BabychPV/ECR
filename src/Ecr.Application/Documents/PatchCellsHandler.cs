using Ecr.Application.Common;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Пакетна зміна комірок. Бюджет — **p95 300 мс на 100 комірок**
/// (tz/08 §8.2), тому кожна зайва дія тут коштує дорого.
/// </summary>
/// <remarks>
/// Часткове застосування заборонене: конфлікт у будь-якому рядку відхиляє
/// весь батч. «Перезаписати мовчки» не є опцією — користувач має побачити
/// розбіжність (B04 §2.3).
/// </remarks>
public sealed class PatchCellsHandler(
    ICellStore cellStore,
    IRowStore rowStore,
    IDocumentStore documents,
    IPeriodStore periods,
    IMetadataCache metadata,
    IAccessDecisionService access,
    Validation.ValidationEngine validation,
    IAuditWriter audit,
    IBackgroundJobScheduler jobs,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Застосовує зміни.</summary>
    /// <exception cref="ConcurrencyConflictException">
    /// Розбіжність <c>baseVersion</c> — <c>ECR-CELL-0409</c>.
    /// </exception>
    /// <exception cref="AccessDeniedException">
    /// Хоч одна комірка недоступна — <c>ECR-ACCS-0403</c> із причиною.
    /// </exception>
    /// <exception cref="BusinessRuleException">
    /// Комірковий <c>Error</c> валідації — <c>ECR-CELL-0422</c>.
    /// </exception>
    /// <remarks>
    /// Орієнтир — тільки послідовність кроків: увесь контекст рішень,
    /// порядок і межі транзакції описані в коментарях відповідних
    /// приватних методів нижче, а не тут.
    /// </remarks>
    public async Task<PatchCellsResponse> HandleAsync(PatchCellsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await LoadContextAsync(request, ct).ConfigureAwait(false);

        EnforceRowCreationRules(context);
        EnsureNoVersionConflicts(context);
        await EnsureAccessAsync(request, context, ct).ConfigureAwait(false);

        var changes = await BuildCellChangesAsync(request, context, ct).ConfigureAwait(false);
        var messages = EnsureValidationPasses(context, request, changes);

        var now = clock.UtcNow;
        var previous = await ReadPreviousValuesAsync(changes, ct).ConfigureAwait(false);
        var isLateEdit = await DetermineIsLateEditAsync(context.Instance.DocumentId, request.PeriodKey, ct)
            .ConfigureAwait(false);

        await PersistChangesAsync(request, context, changes, now, isLateEdit, previous, ct).ConfigureAwait(false);

        await EnqueueRecalculationAsync(request, changes, ct).ConfigureAwait(false);

        return await BuildResponseAsync(request, context.PeriodKey, changes, messages, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Контекст, зібраний з БД і знімка метаданих для одного виклику
    /// <see cref="HandleAsync"/>: структура таблиці, поточний стан рядків і
    /// розподіл запиту на створення/оновлення.
    /// </summary>
    private sealed record RequestContext(
        PeriodKey PeriodKey,
        int UserId,
        TableInstanceRef Instance,
        TemplateVersionSnapshot Snapshot,
        TableDef Table,
        Dictionary<string, ColumnDef> ColumnDefs,
        Dictionary<string, int> Columns,
        IReadOnlyDictionary<string, string> Versions,
        IReadOnlyDictionary<string, long> RowIds,
        List<PatchRow> Creations,
        List<PatchRow> Updates);

    /// <summary>Розподіл змін по upsert/delete разом із супутнім станом.</summary>
    private sealed record CellChangeLists(
        List<CellRecord> Upserts,
        List<CellAddress> Deletes,
        List<long> Touched,
        Dictionary<long, string> RowKeyById);

    /// <summary>
    /// Розв'язує особу користувача, структуру таблиці зі знімка метаданих і
    /// поточний стан рядків — усе, без чого решта кроків не може почати
    /// вирішувати.
    /// </summary>
    private async Task<RequestContext> LoadContextAsync(PatchCellsRequest request, CancellationToken ct)
    {
        var periodKey = new PeriodKey(request.PeriodKey);
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не може змінювати дані.");

        // 1. Структура зі знімка метаданих — без звернення до БД (D-16).
        //    Потрібна, щоб резолвити коди колонок у ColumnDefId; вигадувати
        //    їх не можна, це частина первинного ключа комірки.
        var instance = await rowStore.ResolveTableInstanceAsync(request.TableInstanceId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);

        var table = snapshot.Sheets
            .SelectMany(sh => sh.Tables)
            .FirstOrDefault(t => t.Id == instance.TableDefId)
            ?? throw new NotFoundException(
                "ECR-TMPL-0404", $"Таблиці {instance.TableDefId} немає в структурі версії {instance.TemplateVersionId}.");
        // ⚠ У мапі — сам ColumnDef, а не лише Id. Значення розбирається за
        // ОГОЛОШЕНИМ типом колонки: через HTTP усе приходить JsonElement-ом, і
        // здогадка за виглядом значення клала число в текст, а ідентифікатор
        // запису довідника — у ValueNumeric (`A7-01`).
        //
        // ⛔ Мапа будується ЛИШЕ з колонок ЦІЄЇ таблиці. Код колонки унікальний
        // у межах таблиці, а не версії шаблону: у реальному шаблоні дев'яносто
        // таблиць, і `C2` є майже в кожній. До `A7-27` тут стояло групування
        // по всій версії з `g.First()` — тобто код резолвився в колонку
        // ВИПАДКОВОЇ таблиці.
        //
        // ⚠ Дані від цього НЕ псувалися, і це заслуга схеми, а не коду:
        // `FK_CellValue_Column` складений (`D-84`) —
        // `(TableDefId, ColumnDefId) → cfg.ColumnDef (TableDefId, Id)`, — тому
        // комірка з колонкою чужої таблиці відхиляється базою. Дефект давав
        // відмову запису, а не тихий запис не туди.
        //
        // ⛔ Саме тому цей ключ не можна спрощувати до `ColumnDefId`: він
        // єдиний, хто ловить помилку адресації, і зробив це раніше за будь-який
        // тест.
        var columnDefs = snapshot.ColumnsById.Values
            .Where(c => c.TableDefId == instance.TableDefId)
            .ToDictionary(c => c.Code, StringComparer.Ordinal);

        var columns = columnDefs.ToDictionary(p => p.Key, p => p.Value.Id, StringComparer.Ordinal);

        // 2. Поточний стан рядків — ОДИН запит на батч, не на рядок.
        var versions = await rowStore.GetRowVersionsAsync(request.TableInstanceId, periodKey, ct).ConfigureAwait(false);
        var rowIds = await rowStore.GetRowIdsAsync(request.TableInstanceId, periodKey, ct).ConfigureAwait(false);

        // 3. Створення і оновлення розділяються за BaseVersion (R-B2):
        //    null означає намір СТВОРИТИ рядок, а не «мені байдуже до версії».
        var creations = request.Rows.Where(r => r.BaseVersion is null).ToList();
        var updates = request.Rows.Where(r => r.BaseVersion is not null).ToList();

        return new RequestContext(
            periodKey, userId, instance, snapshot, table, columnDefs, columns, versions, rowIds, creations, updates);
    }

    /// <summary>
    /// Перевіряє намір створення рядків проти режиму таблиці (Fixed/Dynamic),
    /// стелі динамічних рядків і дублікатів ключа зі станом, що вже є в БД.
    /// </summary>
    private static void EnforceRowCreationRules(RequestContext context)
    {
        var table = context.Table;
        var creations = context.Creations;

        // ⛔ Q-148 (аудит, узгодження шляхів запису). До цього PATCH /cells
        // створював рядок БУДЬ-ЯКИМ ключем у БУДЬ-ЯКОМУ режимі — той самий
        // намір, що POST /rows законно відхиляв (`CreateRowHandler`:
        // `ECR-ROW-0409`, RowMode). Два шляхи запису в ту саму таблицю давали
        // протилежні відповіді на те саме питання.
        //
        // ⚠ Рішення людини — «вужче формулювання»: не заборонити створення в
        // Fixed цілком (це зробило б таблицю незаповнюваною до `W8`), а
        // дозволити ЛИШЕ матеріалізацію шаблонного рядка (ключ є в
        // `snapshot.RowsByKey`) і заборонити вигаданий ключ. Після `W8`
        // (`MaterializeFixedRowsAsync`) фіксовані рядки й так заводяться при
        // відкритті періоду — тож на практиці цей шлях або відхиляє вигаданий
        // ключ, або впаде нижче на «рядок уже існує»; про запас лишається
        // безпечним, якщо матеріалізація колись відстане від відкриття.
        if (creations.Count > 0 && !table.AllowsDynamicRows)
        {
            var invalidKeys = creations
                .Where(r => !context.Snapshot.RowsByKey.ContainsKey((context.Instance.TableDefId, r.RowKey)))
                .Select(r => r.RowKey)
                .ToList();

            if (invalidKeys.Count > 0)
            {
                throw new BusinessRuleException(
                    "ECR-ROW-0409",
                    $"Таблиця {table.Code} має RowMode = {table.RowMode}: рядки задані шаблоном, "
                    + $"довільний ключ не приймається. Неприпустимі ключі: {string.Join(", ", invalidKeys)}.",
                    new Dictionary<string, object?> { ["rowKeys"] = invalidKeys });
            }
        }

        // ⛔ Та сама знахідка Q-148: `MaxDynamicRows` перевіряв лише
        // `CreateRowHandler`, і той самий стелю можна було обійти пакетним
        // записом через PATCH.
        if (creations.Count > 0 && table.AllowsDynamicRows && table.MaxDynamicRows is { } maxRows
            && context.RowIds.Count + creations.Count > maxRows)
        {
            throw new BusinessRuleException(
                "ECR-ROW-0409",
                $"Створення {creations.Count} рядків перевищило б межу динамічних рядків таблиці "
                + $"{table.Code}: {context.RowIds.Count} наявних + {creations.Count} нових > {maxRows}.",
                new Dictionary<string, object?> { ["existing"] = context.RowIds.Count, ["adding"] = creations.Count, ["max"] = maxRows });
        }

        var duplicates = creations.Where(r => context.Versions.ContainsKey(r.RowKey)).Select(r => r.RowKey).ToList();
        if (duplicates.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-ROW-0409",
                $"Рядки з такими ключами вже існують: {string.Join(", ", duplicates)}.",
                new Dictionary<string, object?> { ["rowKeys"] = duplicates });
        }
    }

    /// <summary>Перевіряє версії рядків, що оновлюються, проти поточного стану.</summary>
    private void EnsureNoVersionConflicts(RequestContext context)
    {
        // 4. Конфлікти версій. Збираємо ВСІ, а не падаємо на першому:
        //    користувач має побачити повну картину розбіжностей.
        var conflicts = new List<CellConflictDto>();
        foreach (var row in context.Updates)
        {
            if (!context.Versions.TryGetValue(row.RowKey, out var current))
            {
                conflicts.Add(new CellConflictDto(row.RowKey, "*", null, null, "", clock.UtcNow, ""));
                continue;
            }

            if (!string.Equals(current, row.BaseVersion, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var cell in row.Cells)
                {
                    conflicts.Add(new CellConflictDto(
                        row.RowKey, cell.ColumnCode, cell.Value, null, "", clock.UtcNow, current));
                }
            }
        }

        if (conflicts.Count > 0)
        {
            throw new ConcurrencyConflictException(
                "ECR-CELL-0409",
                $"Батч відхилено: рядків із розбіжністю версії — {conflicts.Select(c => c.RowKey).Distinct().Count()}.",
                new Dictionary<string, object?> { ["conflicts"] = conflicts });
        }
    }

    /// <summary>
    /// Права — ОДНИМ викликом на зріз і ОДНИМ на створювані рядки. Поштучна
    /// перевірка комірок не вкладається в бюджет 300 мс.
    /// </summary>
    /// <remarks>
    /// ⛔ Створення і оновлення питаються ОКРЕМО, і це не симетрія заради
    /// симетрії. Рішення зрізу ключуються <c>CellAddress</c>, у якій є
    /// <c>RowId</c>; у рядка, якого ще немає, його немає — тож
    /// <c>updates</c> і <c>creations</c> принципово не вміщаються в один
    /// запит. Саме тут і був дефект: адреси збиралися лише з <c>updates</c>,
    /// на батчі з самих створень <c>addresses.Count == 0</c>, і ВЕСЬ блок
    /// прав пропускався. Запис у ЗАКРИТИЙ період віддавав <c>200</c> і клав
    /// значення в базу.
    /// </remarks>
    private async Task EnsureAccessAsync(PatchCellsRequest request, RequestContext context, CancellationToken ct)
    {
        var profile = await access.BuildProfileAsync(context.UserId, ct).ConfigureAwait(false);
        var denied = new List<EditDecision>();

        var addresses = new List<CellAddress>();
        foreach (var row in context.Updates)
        {
            if (!context.RowIds.TryGetValue(row.RowKey, out var rowId))
            {
                continue;
            }
            foreach (var cell in row.Cells)
            {
                addresses.Add(new CellAddress(context.PeriodKey, rowId, ColumnDefIdOf(context.Columns, cell.ColumnCode)));
            }
        }

        if (addresses.Count > 0)
        {
            var decisions = await access.CanEditSliceAsync(profile, request.TableInstanceId, ct)
                                        .ConfigureAwait(false);

            // Перевіряємо лише ті адреси, які справді змінюються: рішення
            // приходять на весь зріз, але відхиляти батч через заборонену
            // комірку, якої ніхто не чіпав, було б неправильно.
            denied.AddRange(addresses
                .Where(a => decisions.TryGetValue(a, out var d) && !d.IsAllowed)
                .Select(a => decisions[a]));
        }

        if (context.Creations.Count > 0)
        {
            var newRows = await access
                .CanCreateRowsAsync(profile, request.TableInstanceId, context.Creations.Select(r => r.RowKey).ToList(), ct)
                .ConfigureAwait(false);

            foreach (var row in context.Creations)
            {
                // ⛔ Немає рішення — ВІДМОВА. Служба зобов'язана відповісти на
                // кожен запитаний ключ, і протилежне замовчування («рішення
                // немає, отже можна») — це той самий дефект, лише переписаний
                // акуратніше.
                if (!newRows.TryGetValue(row.RowKey, out var allowed))
                {
                    denied.Add(EditDecision.Deny(
                        EditDenyReason.NoGrant,
                        $"Рішення про доступ на рядок {row.RowKey} не отримано."));
                    continue;
                }

                if (!allowed.Row.IsAllowed)
                {
                    denied.Add(allowed.Row);
                    continue;
                }

                // ⚠ Колонки перевіряються ОКРЕМО від рядка: грант оголошується
                // в тому числі на колонку (`ResourceKind.Column`), тож «писати
                // в цю таблицю можна» і «писати в цю колонку можна» — різні
                // відповіді, і друга потрібна саме там, де рядок створюють.
                foreach (var cell in row.Cells)
                {
                    var columnDefId = ColumnDefIdOf(context.Columns, cell.ColumnCode);

                    if (allowed.Columns.TryGetValue(columnDefId, out var decision) && !decision.IsAllowed)
                    {
                        denied.Add(decision);
                    }
                }
            }
        }

        if (denied.Count > 0)
        {
            var first = denied[0];
            throw new AccessDeniedException(
                "ECR-ACCS-0403",
                $"Заборонених комірок у батчі: {denied.Count}. Причина першої: {first.Reason}.",
                new Dictionary<string, object?>
                {
                    ["deniedCount"] = denied.Count,
                    ["reason"] = first.Reason.ToString(),
                    ["detail"] = first.Detail
                });
        }
    }

    /// <summary>
    /// Розкладка на три операції (R-B4): значення → upsert, value = null →
    /// delete, isEmpty → upsert з IsEmpty = 1. Поле, ВІДСУТНЄ в запиті, сюди
    /// не потрапляє взагалі — саме тому «не чіпати» і «стерти» лишаються
    /// різними намірами. Тут-таки створюються нові рядки, бо їхній
    /// <c>TableRow.Id</c> потрібен, щоб побудувати адреси комірок.
    /// </summary>
    private async Task<CellChangeLists> BuildCellChangesAsync(
        PatchCellsRequest request, RequestContext context, CancellationToken ct)
    {
        var upserts = new List<CellRecord>();
        var deletes = new List<CellAddress>();
        var touched = new List<long>();

        // ⛔ Зворотна мапа `TableRow.Id` → `RowKey` збирається ТУТ, а не в
        // аудиті: рядок, який щойно створили, у `rowIds` не потрапляє ніколи
        // (та мапа прочитана до вставки), а саме його ключ і треба записати.
        // До цього аудит писав `RowKey: string.Empty` на кожному записі —
        // тобто колонка, заведена «щоб журнал читався без join», не давала
        // жодної адреси, і за журналом неможливо було сказати, ЯКИЙ рядок
        // змінили (директива №09 `W8` п.4).
        var rowKeyById = context.RowIds.ToDictionary(pair => pair.Value, pair => pair.Key);

        // ⛔ Q-164 (аудит фази 2, продуктивність): ОДИН пакетний виклик на
        // весь батч, а не `CreateRowAsync` у циклі — той коштував двох
        // походів у базу НА КОЖЕН новий рядок (`ReserveIdsAsync` +
        // `SaveChangesAsync`), той самий прийом, що вже застосований для
        // екземплярів таблиць (`MaterializeFixedRowsAsync`).
        if (context.Creations.Count > 0)
        {
            var newIds = await rowStore
                .CreateRowsAsync(
                    request.TableInstanceId, context.PeriodKey,
                    [.. context.Creations.Select(row => RowKey.Create(row.RowKey))], ordinal: 0, ct)
                .ConfigureAwait(false);

            for (var i = 0; i < context.Creations.Count; i++)
            {
                var row = context.Creations[i];
                var id = newIds[i];
                touched.Add(id);
                rowKeyById[id] = row.RowKey;
                Distribute(row, id, context.PeriodKey, context.ColumnDefs, context.Instance.TableDefId, upserts, deletes);
            }
        }

        foreach (var row in context.Updates)
        {
            if (!context.RowIds.TryGetValue(row.RowKey, out var id))
            {
                continue;
            }
            touched.Add(id);
            Distribute(row, id, context.PeriodKey, context.ColumnDefs, context.Instance.TableDefId, upserts, deletes);
        }

        return new CellChangeLists(upserts, deletes, touched, rowKeyById);
    }

    /// <summary>
    /// Валідація. ⚠ Блокує запис ЛИШЕ комірковий Error (R-B3, D-90):
    /// заборона зберегти проміжний стан зробила б роботу з великою таблицею
    /// неможливою — користувач заповнює її не за один раз.
    /// </summary>
    private List<Validation.ValidationMessage> EnsureValidationPasses(
        RequestContext context, PatchCellsRequest request, CellChangeLists changes)
    {
        var messages = Validate(context.Snapshot, context.Instance.TableDefId, request, changes.Upserts, context.RowIds);
        var blocking = messages.Where(m => m.BlocksSave).ToList();
        if (blocking.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-CELL-0422",
                $"Валідація відхилила запис: комірок із помилкою — {blocking.Count}.",
                new Dictionary<string, object?>
                {
                    ["cells"] = blocking
                        .Select(m => new { m.RowKey, m.ColumnCode, m.RuleCode, m.Message })
                        .ToList(),
                });
        }

        return messages;
    }

    /// <summary>
    /// Стан ПЕРЕД записом: старі значення комірок, які буде змінено. ⛔
    /// Читається ДО <c>ApplyAsync</c> — після нього старого значення вже
    /// немає ніде, а саме воно і є половиною запису аудиту.
    /// </summary>
    /// <remarks>
    /// ⚠ Один запит на батч, не на комірку: адреси відомі всі одразу
    /// (<c>ICellStore.ReadCellsAsync</c>), і бюджет 300 мс на 100 комірок
    /// інакше не витримати.
    /// </remarks>
    private async Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadPreviousValuesAsync(
        CellChangeLists changes, CancellationToken ct)
    {
        var addressesToWrite = changes.Upserts.Select(u => u.Address).Concat(changes.Deletes).ToList();
        return addressesToWrite.Count == 0
            ? new Dictionary<CellAddress, CellValueData>()
            : (IReadOnlyDictionary<CellAddress, CellValueData>)await cellStore
                .ReadCellsAsync(addressesToWrite, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ⛔ <c>IsLateEdit</c> ОБЧИСЛЮЄТЬСЯ (`D-70`, директива №09 `W8` п.6). Тут
    /// стояв літерал <c>false</c> — у трьох місцях одразу, — при тому що
    /// <c>Period.IsLateEditWindow</c> існував і не мав жодного читача.
    /// Журнал, у якому пізніх правок не буває ніколи, гірший за відсутність
    /// колонки: він відповідає на питання, і відповідає неправдою.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Reopen</c> теж сюди входить: він переводить період саме в
    /// <c>Grace</c> (<c>Period.Reopen</c>), тому окремої умови не потрібно.
    /// </remarks>
    private async Task<bool> DetermineIsLateEditAsync(long documentId, int periodKeyValue, CancellationToken ct)
    {
        var periodState = await periods
            .FindPeriodStateAsync(documentId, periodKeyValue, ct)
            .ConfigureAwait(false);
        return periodState == PeriodState.Grace;
    }

    /// <summary>
    /// Одна транзакція: значення, «дотик» рядків, «дотик» документа й аудит.
    /// Аудит поза транзакцією дав би журнал, у якому є зміни, яких у даних
    /// немає.
    /// </summary>
    /// <remarks>
    /// ⛔ Q-243 (критичний, аудит цілісності). До цієї правки коментар вище
    /// був НЕПРАВДОЮ: чотири кроки нижче були чотирма незалежними,
    /// самостійно закомміченими одиницями — <c>cellStore.ApplyAsync</c> сам
    /// відкривав і комітив ВЛАСНУ <c>SqlTransaction</c> (`NormalizedCellStore`),
    /// <c>rowStore.TouchRowsAsync</c> ішов окремим автокомітним
    /// <c>ExecuteUpdateAsync</c>, <c>audit.WriteCellChangesAsync</c> писав
    /// сирим <c>SqlCommand</c> без жодної відкритої транзакції (порт
    /// <c>IUnitOfWork.BeginTransactionAsync</c> існував, але його не
    /// викликав НІХТО в `Ecr.Application` — підтверджено пошуком по
    /// репозиторію), і лише «дотик» документа комітився разом із
    /// <c>uow.SaveChangesAsync</c> наприкінці. Збій/розрив з'єднання/скасування
    /// між будь-якими двома кроками лишав дані змінені БЕЗ відповідного рядка
    /// аудиту (або «дотик» без аудиту, або аудит без «дотику» документа) —
    /// реальна діра в системі, чиє призначення — звітність, придатна для
    /// перевірки регулятором.
    ///
    /// ⚠ Фікс: увесь блок — одним викликом <c>IUnitOfWork.ExecuteInTransactionAsync</c>,
    /// коміт — ОДИН, наприкінці, після успіху всього замикання.
    /// <c>NormalizedCellStore.ApplyAsync</c> тепер приєднується до цієї
    /// ambient-транзакції замість відкриття власної (Q-243);
    /// <c>RowStore.TouchRowsAsync</c> (`ExecuteUpdateAsync`) і
    /// <c>DocumentStore.TouchAsync</c> (трекнута зміна, комітиться разом із
    /// <c>uow.SaveChangesAsync</c>) автоматично приєднуються до тієї самої
    /// транзакції — обидва йдуть через ТОЙ САМИЙ <c>EcrDbContext</c>, що й
    /// <c>uow</c> (один DI-скоуп на запит). <c>AuditWriter.CreateCommand</c>
    /// уже вмів приєднатися до відкритої транзакції — йому просто нізвідки
    /// було її взяти.
    ///
    /// ⛔ Перша спроба фіксу (окремі виклики
    /// <c>IUnitOfWork.BeginTransactionAsync</c>/<c>CommitAsync</c> навколо
    /// цих самих кроків, БЕЗ обгортки в одне замикання) впала на реальному
    /// DbContext (<c>EnableRetryOnFailure</c>): <c>RowStore.TouchRowsAsync</c>
    /// (`ExecuteUpdateAsync`) кидав <c>InvalidOperationException</c> — EF
    /// Core вимагає, щоб уся транзакція (Begin + робота + Commit) йшла
    /// ОДНИМ замиканням усередині <c>CreateExecutionStrategy().ExecuteAsync</c>.
    /// Юніт-тести цього не ловили (їхній <c>EcrDbContext</c> ретраю не має);
    /// зловив реальний прогін <c>Ecr.Scenarios.Tests</c> проти піднятого
    /// <c>Ecr.Api</c>. Звідси <c>ExecuteInTransactionAsync</c> замість пари
    /// Begin/Commit — див. коментар порту в <c>IUnitOfWork.cs</c>.
    /// </remarks>
    private Task PersistChangesAsync(
        PatchCellsRequest request,
        RequestContext context,
        CellChangeLists changes,
        DateTime now,
        bool isLateEdit,
        IReadOnlyDictionary<CellAddress, CellValueData> previous,
        CancellationToken ct)
        => uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await cellStore.ApplyAsync(
                new CellChangeSet(request.TableInstanceId, changes.Upserts, changes.Deletes, changes.Touched, context.UserId, isLateEdit),
                innerCt).ConfigureAwait(false);

            await rowStore.TouchRowsAsync(changes.Touched, now, innerCt).ConfigureAwait(false);

            // ⛔ Документ теж «торкається» (`H-23d`). До цього рядка `ModifiedAt` і
            // `ModifiedByUserId` документа назавжди лишалися моментом створення:
            // рядки оновлювалися, аудит писався, а перелік документів показував
            // дату, якої зміни не мали. Колонка, що показує неправду, знецінює й
            // сусідні — правдиві.
            //
            // ⚠ Тією ж транзакцією, що й значення: дата зміни без самої зміни
            // гірша за відсутність дати.
            await documents.TouchAsync(context.Instance.DocumentId, context.UserId, now, innerCt).ConfigureAwait(false);

            await WriteAuditAsync(request, context, changes, now, isLateEdit, previous, innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct);

    /// <summary>Записує аудит батчу — усередині тієї ж транзакції, ДО коміту.</summary>
    private async Task WriteAuditAsync(
        PatchCellsRequest request,
        RequestContext context,
        CellChangeLists changes,
        DateTime now,
        bool isLateEdit,
        IReadOnlyDictionary<CellAddress, CellValueData> previous,
        CancellationToken ct)
    {
        await audit.WriteCellChangesAsync(
            BuildAuditRecords(
                request, changes.Upserts, changes.Deletes, context.UserId, now, context.Instance.DocumentId,
                changes.RowKeyById, previous, isLateEdit),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ⚠ Перерахунок ставиться в чергу ПІСЛЯ commit і поза транзакцією:
    /// воркер інакше почав би читати рядки, яких ще не видно, і отримав би
    /// або старі значення, або блокування на піку останнього дня.
    /// </summary>
    /// <remarks>
    /// ⛔ Задача — <c>IFormulaRecalculationJob</c>, а не <c>IRecalculationJob</c>.
    /// Раніше сюди ставилася задача МЕТОДОЛОГІЙ, тіла якої вона не розуміє:
    /// її запит має <c>ProjectId</c>/<c>DocumentId</c>, а тут надсилався
    /// <c>TableInstanceId</c>. Розбір давав нулі, і після кожної правки в
    /// чергу лягала задача, яка не могла зробити нічого (`A7-63`).
    ///
    /// ⛔ Змінені комірки передаються ЯВНО: саме вони — насіння каскаду. Без
    /// них перерахунок був би повним на кожну правку, і граф залежностей
    /// коштував би, не даючи нічого.
    /// </remarks>
    private async Task EnqueueRecalculationAsync(PatchCellsRequest request, CellChangeLists changes, CancellationToken ct)
    {
        var seeds = changes.Upserts
            .Select(u => u.Address)
            .Concat(changes.Deletes)
            .Select(a => new { RowId = a.TableRowId, a.ColumnDefId })
            .Distinct()
            .ToList();

        await jobs.EnqueueAsync<Ports.IFormulaRecalculationJob>(
            new { request.TableInstanceId, request.PeriodKey, Cells = seeds }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Підсумкова відповідь: застосовані комірки, нові версії рядків, повідомлення валідації.</summary>
    private async Task<PatchCellsResponse> BuildResponseAsync(
        PatchCellsRequest request,
        PeriodKey periodKey,
        CellChangeLists changes,
        List<Validation.ValidationMessage> messages,
        CancellationToken ct)
    {
        var newVersions = await rowStore.GetRowVersionsAsync(request.TableInstanceId, periodKey, ct)
                                        .ConfigureAwait(false);

        return new PatchCellsResponse(
            AppliedCells: changes.Upserts.Count + changes.Deletes.Count,
            RowVersions: newVersions,
            // Повідомлення, які запис НЕ блокують, повертаються клієнтові:
            // інакше про них ніхто б не дізнався, і сенс рівнів зник би.
            Validation: messages
                .Select(m => new ValidationMessageDto(
                    m.Severity.ToString(), m.RuleCode, m.Message, m.RowKey, m.ColumnCode))
                .ToList());
    }

    /// <summary>Валідує змінені комірки і правила рівня рядка.</summary>
    private List<Validation.ValidationMessage> Validate(
        Domain.Entities.Configuration.TemplateVersionSnapshot snapshot,
        int tableDefId,
        PatchCellsRequest request,
        List<CellRecord> upserts,
        IReadOnlyDictionary<string, long> rowIds)
    {
        var table = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .FirstOrDefault(t => t.Id == tableDefId);

        IReadOnlyList<Domain.Entities.Configuration.ValidationRule> rules =
            table?.ValidationRules ?? [];
        var byRowId = rowIds.ToDictionary(p => p.Value, p => p.Key);
        var messages = new List<Validation.ValidationMessage>();

        foreach (var record in upserts)
        {
            if (!snapshot.ColumnsById.TryGetValue(record.Address.ColumnDefId, out var column))
            {
                continue;
            }

            var rowKey = byRowId.GetValueOrDefault(record.Address.TableRowId);
            foreach (var message in validation.ValidateCell(column, record.Value, rules))
            {
                messages.Add(message with { RowKey = rowKey });
            }
        }

        // Правила рівня рядка виконуються після коміркових і запис НЕ блокують:
        // рядок може бути незавершеним посеред заповнення, і це нормальний стан.
        foreach (var row in request.Rows)
        {
            messages.AddRange(validation
                .ValidateScope(scope: 1, rules, new PatchRowValidationContext(row))
                .Select(m => m with { RowKey = row.RowKey }));
        }

        return messages;
    }

    /// <summary>Значення рядка з самого запиту — без звернення до сховища.</summary>
    /// <remarks>
    /// ⚠ Правило рівня рядка бачить те, що клієнт ЩОЙНО надіслав, а не те, що
    /// лежить у базі: перевіряти треба намір користувача, інакше повідомлення
    /// стосувалося б стану, який зараз перезаписується.
    /// </remarks>
    private sealed class PatchRowValidationContext(PatchRow row) : Validation.IValidationContext
    {
        public object? GetCell(string columnCode)
            => CellValueReader.Normalize(
                row.Cells
                   .FirstOrDefault(c => string.Equals(c.ColumnCode, columnCode, StringComparison.OrdinalIgnoreCase))
                   ?.Value);

        public object? GetCell(string rowKey, string columnCode)
            => string.Equals(rowKey, row.RowKey, StringComparison.Ordinal) ? GetCell(columnCode) : null;
    }

    private static void Distribute(
        PatchRow row,
        long rowId,
        PeriodKey periodKey,
        IReadOnlyDictionary<string, ColumnDef> columnDefs,
        int tableDefId,
        List<CellRecord> upserts,
        List<CellAddress> deletes)
    {
        foreach (var cell in row.Cells)
        {
            var column = ColumnOf(columnDefs, cell.ColumnCode);
            var address = new CellAddress(periodKey, rowId, column.Id);

            if (cell.IsEmpty)
            {
                upserts.Add(new CellRecord(address, TableDefId: tableDefId, CellValueData.Empty));
                continue;
            }

            // ⚠ Три різні операції (R-B4). `null` — стерти, і саме тому
            // читач повертає null, а не порожнє значення: «стерти» і «явна
            // порожнеча» — різні наміри, і зводити їх в один означає втратити
            // відмінність, яку користувач висловив свідомо.
            var data = CellValueReader.Read(cell.Value, column);

            if (data is null)
            {
                deletes.Add(address);
            }
            else
            {
                upserts.Add(new CellRecord(address, TableDefId: tableDefId, data));
            }
        }
    }

    /// <summary>Опис колонки за кодом; невідомий код — відмова, а не пропуск.</summary>
    private static ColumnDef ColumnOf(IReadOnlyDictionary<string, ColumnDef> columnDefs, string code)
        => columnDefs.TryGetValue(code, out var column)
            ? column
            : throw new BusinessRuleException(
                "ECR-CELL-0422",
                $"Колонки «{code}» немає в цій версії шаблону.",
                new Dictionary<string, object?> { ["columnCode"] = code });

    /// <summary>Записи аудиту для застосованого батчу.</summary>
    /// <remarks>
    /// ⛔ Три поля тут стояли константами і робили журнал непридатним рівно
    /// для того, заради чого його ведуть (директива №09 `W8`, пп. 4 і 6):
    /// <list type="bullet">
    /// <item><c>RowKey</c> — <c>string.Empty</c>: колонка, заведена «щоб
    /// журнал читався без join», не давала адреси взагалі;</item>
    /// <item><c>OldValue</c> — <c>null</c>: запис «стало 7» без «було 5» не
    /// відповідає на єдине питання, заради якого в журнал дивляться;</item>
    /// <item><c>IsLateEdit</c> — <c>false</c>: пізніх правок не бувало ніколи,
    /// хоч саме вони цікавлять того, хто звіряє звітність.</item>
    /// </list>
    /// Сховище (<c>AuditWriter</c>) при цьому писало всі три чесно — дефект
    /// був вище, у тому, що йому передавали.
    /// </remarks>
    private static List<CellChangeRecord> BuildAuditRecords(
        PatchCellsRequest request, List<CellRecord> upserts,
        List<CellAddress> deletes, int userId, DateTime now, long documentId,
        IReadOnlyDictionary<long, string> rowKeyById,
        IReadOnlyDictionary<CellAddress, CellValueData> previous,
        bool isLateEdit)
    {
        var records = new List<CellChangeRecord>(upserts.Count + deletes.Count);

        foreach (var u in upserts)
        {
            records.Add(new CellChangeRecord(
                now, u.Address, DocumentId: documentId,
                RowKey: rowKeyById.GetValueOrDefault(u.Address.TableRowId, string.Empty),
                OldValue: Was(previous, u.Address), NewValue: Describe(u.Value),
                userId, request.Origin, isLateEdit, CorrelationId: null));
        }

        foreach (var d in deletes)
        {
            records.Add(new CellChangeRecord(
                now, d, DocumentId: documentId,
                RowKey: rowKeyById.GetValueOrDefault(d.TableRowId, string.Empty),
                OldValue: Was(previous, d), NewValue: null,
                userId, request.Origin, isLateEdit, CorrelationId: null));
        }

        return records;
    }

    /// <summary>Значення комірки ДО запису; <c>null</c> — комірки не було.</summary>
    /// <remarks>
    /// ⚠ «Комірки не було» і «комірка була порожня» — різні стани (R-B4), і
    /// перший чесно лишається <c>null</c>: незаповнена комірка не
    /// матеріалізується взагалі (`ФВ-3.8`), тож приписати їй порожній рядок
    /// означало б вигадати запис, якого не існувало.
    /// </remarks>
    private static string? Was(
        IReadOnlyDictionary<CellAddress, CellValueData> previous, CellAddress address)
        => previous.TryGetValue(address, out var value) ? Describe(value) : null;

    private static string? Describe(CellValueData v)
        => v.IsEmpty ? string.Empty
         : v.ValueNumeric?.ToString(System.Globalization.CultureInfo.InvariantCulture)
           ?? v.ValueString
           ?? v.ValueBool?.ToString()
           ?? v.ValueDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
           ?? v.ValueRegistryEntryId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
           ?? v.ValueUnitId?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Код колонки → <c>ColumnDefId</c> за знімком структури.
    /// </summary>
    /// <remarks>
    /// Резолвиться зі знімка, а не вигадується: <c>ColumnDefId</c> — частина
    /// первинного ключа комірки, і будь-яке «приблизне» значення записало б
    /// дані в неіснуючу колонку.
    /// </remarks>
    private static int ColumnDefIdOf(Dictionary<string, int> map, string columnCode)
        => map.TryGetValue(columnCode, out var id)
            ? id
            : throw new BusinessRuleException(
                "ECR-CELL-0422", $"Колонки з кодом '{columnCode}' немає в структурі версії.");
}
