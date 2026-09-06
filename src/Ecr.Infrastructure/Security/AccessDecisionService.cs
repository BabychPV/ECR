using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Реалізація <see cref="IAccessDecisionService"/>: поєднує RBAC, стан періоду,
/// правила періодів шаблону, статус документа і структурні обмеження.
/// </summary>
/// <remarks>
/// Повертає **причину**, а не <c>bool</c>: користувач має розуміти, чому
/// комірка сіра, інакше він піде до адміністратора, а той — до розробника.
/// </remarks>
public sealed class AccessDecisionService(
    EcrDbContext db,
    IMetadataCache metadata,
    Caching.AccessProfileCache profileCache,
    IClock clock,
    Application.Common.ICurrentUser currentUser,
    Application.Ports.IWorkflowStore workflow) : IAccessDecisionService
{
    /// <inheritdoc />
    public async Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct)
    {
        var account = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp, u.IsActive })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (account is null || !account.IsActive)
        {
            // Вимкнений запис не має «профілю без прав»: порожній профіль
            // виглядав би як звичайний користувач без грантів, а це різні речі
            // і в UI, і в журналі.
            throw new AccessDeniedException(
                "ECR-AUTH-0401", "Обліковий запис не існує або вимкнений.");
        }

        // Ключ кешу — користувач + штамп: зміна ролей крутить штамп, тому
        // старий запис просто перестає адресуватися (ФВ-6.7).
        return await profileCache
            .GetOrCreateAsync(userId, account.SecurityStamp, token => LoadAsync(userId, account.SecurityStamp, token), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Сталий відбиток набору груп.
    /// </summary>
    /// <remarks>
    /// ⛔ SHA-256, а не <c>GetHashCode</c>: той рандомізований на кожен запуск
    /// процесу, і ключ кешу мінявся б після кожного перезапуску — профіль
    /// перебудовувався б щоразу, а два інстанси не бачили б кешу один одного.
    /// </remarks>
    private static string Fingerprint(IReadOnlyList<string> groupSids)
    {
        if (groupSids.Count == 0)
        {
            return string.Empty;
        }

        var joined = string.Join('|', groupSids.OrderBy(s => s, StringComparer.Ordinal));

        return System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(joined)))[..16];
    }

    /// <summary>
    /// Стеля вибірки призначень ролей на одну людину.
    /// </summary>
    /// <remarks>
    /// Двісті призначень на одного користувача — це вже не права, а наслідок
    /// помилки в адмініструванні. Межа існує, щоб така помилка не
    /// перетворилася на повільний вхід, який ніхто не пов'яже з її причиною.
    /// </remarks>
    private const int MaxRoleAssignments = 200;

    /// <summary>Збирає профіль із бази. Викликається лише при промаху кешу.</summary>
    private async Task<AccessProfile> LoadAsync(int userId, string securityStamp, CancellationToken ct)
    {
        // ⚠ «Сьогодні» тут — у UTC, і це СВІДОМО, а не забутий переклад у пояс
        // майданчика (`H-13`, `D2-78`). Строкове призначення ролі належить
        // ЛЮДИНІ, а не проєкту: той самий профіль обслуговує всі проєкти всіх
        // майданчиків і лежить у кеші під одним ключем. Пояс проєкту тут просто
        // нічого не означає — їх стільки, скільки проєктів, і жоден не має
        // права визначати, коли закінчилася підміна на час відпустки.
        //
        // ⚠ Ціна названа: на межі доби підміна може закінчитися на кілька годин
        // раніше або пізніше за місцеву північ. Це не той клас помилки, що межі
        // періодів: там дата — ЗОБОВ'ЯЗАННЯ («останній день місяця»), тут вона
        // адміністративна, і зсув у кілька годин не робить нікого спізнілим.
        var today = DateOnly.FromDateTime(clock.UtcNow);

        // ⚠ Строкові призначення враховуються тут, а не «десь у перевірці»:
        // підміна на час відпустки має закінчитися сама, інакше її доводиться
        // знімати руками — а того, хто мав би зняти, саме й немає на місці.
        // ⚠ Групи беруться З ТОКЕНА поточної сесії (ФВ-6.15a), і лише тоді,
        // коли профіль будується для НЕЇ САМОЇ. Для чужого користувача —
        // перегляд адміністратором, симуляція — токена в нас немає, і взяти
        // чужі групи зі своєї сесії означало б показати не ті права (`P-02`).
        var groupSids = userId == currentUser.UserId
            ? currentUser.GroupSids
            : [];

        // ⛔ Межі дії перевіряє ДОМЕН (`RoleAssignment.IsEffectiveOn`), а не
        // копія його умови в запиті (`H-23a`). Умова тут стояла дослівно та
        // сама, і саме тому це було небезпечно: два формулювання одного
        // правила збігаються рівно до першої правки одного з них, а
        // розійшовшись, не ламають нічого — просто хтось зберігає права після
        // закінчення підміни. Доменний метод був при цьому без викликача:
        // покритий тестом і недосяжний.
        //
        // ⚠ Ціна — вибірка призначень замість самих ролей. Призначень на
        // людину одиниці (свої плюс групові), тож у бюджет профілю це не
        // втручається; стеля нижче захищає від зіпсованих даних, а не від
        // нормального навантаження.
        var assignments = await db.RoleAssignments
            .AsNoTracking()
            .Where(a => a.UserId == userId || (a.PrincipalSid != null && groupSids.Contains(a.PrincipalSid)))
            .Take(MaxRoleAssignments)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var roleIds = assignments
            .Where(a => a.IsEffectiveOn(today))
            .Select(a => a.RoleId)
            .Distinct()
            .ToList();
        var permissions = roleIds.Count == 0
            ? []
            : await db.RolePermissions
                .AsNoTracking()
                .Where(rp => roleIds.Contains(rp.RoleId))
                .Select(rp => rp.PermissionCode)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

        var rows = roleIds.Count == 0
            ? []
            : await db.ResourceGrants
                .AsNoTracking()
                .Where(g => roleIds.Contains(g.RoleId))
                .Select(g => new { g.ResourceKind, g.ResourceId, g.Level, g.IsDeny })
                .ToListAsync(ct)
                .ConfigureAwait(false);

        var grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal);
        var denies = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var key = $"{row.ResourceKind}:{row.ResourceId}";

            if (row.IsDeny)
            {
                denies.Add(key);
                continue;
            }

            // Дві ролі на той самий ресурс — виграє ширший рівень: людина
            // отримує суму своїх ролей, а не випадкову з них.
            grants[key] = grants.TryGetValue(key, out var existing) && existing > row.Level
                ? existing
                : row.Level;
        }

        // ⛔ Успадкування Project → Sheet → Table → Column тут НЕ розгортається
        // в мапу: його робить EditRules.Effective на кожному рішенні. Причина
        // не в економії — розгорнута мапа зафіксувала б структуру шаблону на
        // момент побудови профілю, і новий аркуш успадкував би права лише
        // після перевходу користувача.
        return new AccessProfile
        {
            CacheKey = Caching.AccessProfileCache.Key(userId, securityStamp, Fingerprint(groupSids)),
            UserId = userId,
            SecurityStamp = securityStamp,
            Permissions = permissions.ToHashSet(StringComparer.Ordinal),
            Grants = grants,
            Denies = denies,
            RoleIds = roleIds.ToHashSet(),
        };
    }

    /// <inheritdoc />
    public async Task<EditDecision> CanReadDocumentAsync(
        AccessProfile profile, long documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var projectId = await ProjectIdAsync(documentId, ct).ConfigureAwait(false);

        // Читання не залежить ні від стану періоду, ні від статусу аркуша:
        // закритий період і подана форма лишаються видимими — інакше звіт
        // неможливо було б навіть переглянути після подання.
        return profile.LevelFor(ResourceKind.Project, projectId) >= GrantLevel.Read
            ? EditDecision.Allow()
            : EditDecision.Deny(EditDenyReason.NoGrant);
    }

    /// <inheritdoc />
    public async Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var context = await BuildContextAsync(
            documentId, address.PeriodKey, sheetDefId: null, address.ColumnDefId,
            evaluateAccessWindow: true, ct).ConfigureAwait(false);

        return EditRules.CanEdit(profile, context);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var instance = await db.TableInstances
            .AsNoTracking()
            .Where(t => t.Id == tableInstanceId)
            .Select(t => new { t.DocumentId, t.TableDefId, t.PeriodKeyValue })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-DOC-0404", $"Екземпляр таблиці {tableInstanceId} не знайдено.");

        var periodKey = new PeriodKey(instance.PeriodKeyValue);

        var snapshot = await SnapshotAsync(instance.DocumentId, ct).ConfigureAwait(false);

        // ⛔ Аркуш визначається ДО побудови умов, а не лишається нульовим.
        // З <c>sheetDefId: null</c> стан робочого процесу не читався взагалі й
        // підставлявся як <c>Draft</c> — тобто ПОДАНИЙ аркуш залишався
        // редаговним на єдиному шляху, яким запис і йде (<c>PatchCellsHandler</c>).
        // Перевірка в <see cref="EditRules"/> була, тести на неї були — а
        // викликати її ніхто не міг (`A7-51`).
        var sheetDefId = SheetOf(snapshot, instance.TableDefId);

        // ⚠ Спільні для зрізу умови рахуються ОДИН раз. Поштучний виклик
        // CanEditCellAsync у циклі — антипатерн: на таблиці 500×60 це 30 000
        // запитів, а на права відведено 50 мс на весь запит (ФВ-6.10).
        var shared = await BuildContextAsync(
                instance.DocumentId, periodKey, sheetDefId, columnDefId: 0,
                evaluateAccessWindow: false, ct)
            .ConfigureAwait(false);

        var rows = await db.TableRows
            .AsNoTracking()
            .Where(r => r.TableInstanceId == tableInstanceId && !r.IsDeleted)
            .Select(r => new { r.Id, r.RowKey })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var columns = snapshot.ColumnsById.Values
            .Where(c => c.TableDefId == instance.TableDefId)
            .ToList();

        // ⛔ Правила доступу до періоду (`ФВ-2.15`) і все, що їм потрібно,
        // читається ОДИН раз на зріз — див. `PeriodRuleContextAsync`.
        var ruleContext = await PeriodRuleContextAsync(
                snapshot.TemplateVersionId, shared.ProjectId, tableInstanceId, periodKey, ct)
            .ConfigureAwait(false);

        var result = new Dictionary<CellAddress, EditDecision>(rows.Count * columns.Count);

        foreach (var row in rows)
        {
            var def = snapshot.RowsByKey.TryGetValue((instance.TableDefId, row.RowKey), out var found)
                ? found
                : null;

            // Вікна чинності записів довідника, на які посилається саме цей
            // рядок: колонка → вікно. Порожньо — рядок нічого не обрав.
            var sourceValues = ruleContext.SourceWindows.TryGetValue(row.Id, out var windows)
                ? windows
                : EmptyWindows;

            foreach (var column in columns)
            {
                var context = shared with
                {
                    TableDefId = instance.TableDefId,
                    ColumnDefId = column.Id,
                    ColumnIsComputed = column.IsComputed,
                    ColumnIsReadOnly = column.IsReadOnly,
                    RowIsReadOnly = def?.IsReadOnly ?? false,
                };

                var decision = EditRules.CanEdit(profile, context);

                // ⚠ Правила періоду перевіряються ЛИШЕ там, де решта
                // дозволила. Інакше комірка в закритому періоді доповідала б про
                // вікно дозволу замість про сам період — причина має бути та,
                // яку користувач здатен усунути першою.
                if (decision.IsAllowed && ruleContext.Rules.Count > 0)
                {
                    var facts = new PeriodRuleFacts(
                        sheetDefId,
                        instance.TableDefId,
                        def?.RowKind ?? RowKind.Item,
                        (byte)periodKey.Sequence,
                        periodKey.Year,
                        ruleContext.CurrentSequence,
                        column.MonthNumber,
                        sourceValues,
                        EmptyExpressions);

                    var outcome = PeriodAccessRules.Evaluate(ruleContext.Rules, facts, profile.RoleIds);

                    if (outcome.Blocks)
                    {
                        decision = EditDecision.Deny(outcome.Reason, outcome.Detail);
                    }
                }

                result[new CellAddress(periodKey, row.Id, column.Id)] = decision;
            }
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<int, SourceValidity> EmptyWindows
        = new Dictionary<int, SourceValidity>();

    private static readonly IReadOnlyDictionary<long, IReadOnlyDictionary<int, SourceValidity>>
        EmptySourceWindows = new Dictionary<long, IReadOnlyDictionary<int, SourceValidity>>();

    /// <summary>Результати умов <c>Expression</c>.</summary>
    /// <remarks>
    /// ⛔ Порожньо, і це ВИДНО, а не сховано: обчислення виразу
    /// над рядком потребує рушія діалекту шаблонів на шляху перевірки
    /// прав, а цей шлях має бюджет 50 мс на весь зріз (<c>ФВ-6.10</c>).
    /// Правило виду <c>Expression</c> при цьому НЕ блокує нічого
    /// мовчки: <see cref="PeriodAccessRules"/> трактує необчислену умову
    /// як «не застосовується», а не як заборону.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, bool> EmptyExpressions
        = new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>Спільні для зрізу дані правил доступу до періоду.</summary>
    /// <param name="Rules">Правила версії шаблону.</param>
    /// <param name="CurrentSequence">Поточний період проєкту; <c>null</c> — не визначений.</param>
    /// <param name="SourceWindows">Рядок → колонка → вікно чинності обраного запису.</param>
    private sealed record PeriodRuleContext(
        IReadOnlyList<PeriodAccessRuleDef> Rules,
        byte? CurrentSequence,
        IReadOnlyDictionary<long, IReadOnlyDictionary<int, SourceValidity>> SourceWindows);

    /// <summary>
    /// Збирає все, що потрібно правилам доступу, ЗА КІЛЬКА ЗАПИТІВ НА ЗРІЗ
    /// — не за запитом на комірку.
    /// </summary>
    /// <remarks>
    /// ⛔ Спокуса реалізувати <c>SourceWindow</c> походом у довідник на
    /// кожну комірку велика, і вона тиха: тести на трьох рядках
    /// пройдуть, а бюджет впаде лише на реальному зрізі 500×60.
    /// Саме тому кількість запитів стереже окремий тест.
    ///
    /// ⚠ Другий і третій запити виконуються ЛИШЕ тоді, коли є правило
    /// відповідного виду. Шаблон без них — а це більшість — не платить
    /// за механізм нічим.
    /// </remarks>
    private async Task<PeriodRuleContext> PeriodRuleContextAsync(
        int templateVersionId, int projectId, long tableInstanceId, PeriodKey periodKey,
        CancellationToken ct)
    {
        var rules = await db.PeriodAccessRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rules.Count == 0)
        {
            return new PeriodRuleContext(rules, null, EmptySourceWindows);
        }

        byte? currentSequence = null;
        if (rules.Exists(r => r.RuleKind == PeriodAccessRuleKind.RelativeWindow))
        {
            // Поточний період — з календаря проєкту, а не з годинника
            // сервера: проєкт із закріпленим періодом (`D-77`) інакше
            // поводився б не так, як показує.
            currentSequence = await db.Projects
                .AsNoTracking()
                .Where(p => p.Id == projectId && p.CurrentPeriodId != null)
                .Join(db.Periods, p => p.CurrentPeriodId!.Value, x => x.Id, (_, x) => (byte?)x.Sequence)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        var sourceColumns = rules
            .Where(r => r.RuleKind == PeriodAccessRuleKind.SourceWindow)
            .Select(r => r.SourceColumnDefId)
            .OfType<int>()
            .Distinct()
            .ToList();

        if (sourceColumns.Count == 0)
        {
            return new PeriodRuleContext(rules, currentSequence, EmptySourceWindows);
        }

        // ⛔ ОДИН запит на весь зріз: посилання рядків на записи довідника
        // разом із вікнами чинності цих записів.
        var references = await (
                from cell in db.CellValues.AsNoTracking()
                join row in db.TableRows.AsNoTracking()
                    on new { cell.PeriodKeyValue, Id = cell.TableRowId }
                    equals new { row.PeriodKeyValue, row.Id }
                join entry in db.RegistryEntries.AsNoTracking()
                    on (long)cell.ValueRegistryEntryId!.Value equals entry.Id
                where row.TableInstanceId == tableInstanceId
                      && cell.PeriodKeyValue == periodKey.Value
                      && cell.ValueRegistryEntryId != null
                      && sourceColumns.Contains(cell.ColumnDefId)
                select new { cell.TableRowId, cell.ColumnDefId, entry.ValidFrom, entry.ValidTo })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var windows = references
            .GroupBy(r => r.TableRowId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<int, SourceValidity>)g.ToDictionary(
                    r => r.ColumnDefId,
                    r => new SourceValidity(r.ValidFrom, r.ValidTo)));

        return new PeriodRuleContext(rules, currentSequence, windows);
    }

    /// <inheritdoc />
    public async Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var context = await BuildContextAsync(
                documentId, periodKey, sheetDefId, columnDefId: 0, evaluateAccessWindow: true, ct)
            .ConfigureAwait(false);

        // ⚠ При поданні блокує БУДЬ-ЯКА помилка валідації будь-якого рівня
        // (ФВ-5.19), на відміну від запису, де блокує лише коміркова (D-90):
        // подана форма йде назовні цілком, і рядкова помилка в ній — це
        // неправильний звіт.
        var hasErrors = await db.ValidationResults
            .AsNoTracking()
            .Where(v => v.DocumentId == documentId && v.PeriodKey == periodKey.Value)
            .OrderByDescending(v => v.RunAt)
            .Select(v => v.ErrorCount)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) > 0;

        return EditRules.CanSubmit(profile, context, hasErrors);
    }

    /// <inheritdoc />
    public async Task<EditDecision> CanApproveAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var context = await BuildContextAsync(
                documentId, periodKey, sheetDefId, columnDefId: 0, evaluateAccessWindow: true, ct)
            .ConfigureAwait(false);

        // ⚠ Маршрут читається ЛИШЕ при затвердженні, а не в кожному рішенні
        // про доступ: затверджують рідко, а комірки читають тисячами.
        var step = await CurrentApprovalStepAsync(documentId, sheetDefId, periodKey, ct)
            .ConfigureAwait(false);

        return EditRules.CanApprove(profile, context, step?.RoleId);
    }

    /// <inheritdoc />
    public async Task<ApprovalStepView?> CurrentApprovalStepAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        var scope = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Join(db.Projects, d => d.ProjectId, p => p.Id, (_, p) => new { p.Id, p.TemplateVersionId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (scope is null)
        {
            return null;
        }

        var route = await workflow.FindRouteAsync(scope.Id, scope.TemplateVersionId, ct)
            .ConfigureAwait(false);

        // ⛔ Немає маршруту — немає кроку, і затвердження лишається
        // одноетапним. Це найчастіший стан: seed не створює жодного маршруту.
        if (route is null || route.Steps.Count == 0)
        {
            return null;
        }

        var currentStepId = await db.ApprovalStates
            .AsNoTracking()
            .Where(a => a.DocumentId == documentId
                        && a.SheetDefId == sheetDefId
                        && a.PeriodKey == periodKey.Value)
            .Select(a => a.CurrentStepId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // ⛔ Перехід рахує ДОМЕН, а не служба. Друга реалізація «яка черга»
        // розійшлася б із першою на першій же правці маршруту, і розбіжність
        // була б видима лише тоді, коли документ застряг у погодженні.
        var current = route.StepAt(currentStepId);

        if (current is null)
        {
            return null;
        }

        var next = route.StepAfter(current.Id);

        return new ApprovalStepView(
            current.Id, current.Ordinal, current.RoleId, next?.Id, route.Steps.Count);
    }

    /// <summary>Збирає умови доступу з бази в один <see cref="CellAccessContext"/>.</summary>
    /// <remarks>
    /// ⛔ <c>Project.CurrentPeriod</c> у цей ланцюг НЕ входить: інакше «пін»
    /// поточного періоду став би прихованим правом редагувати закрите (D-77).
    /// </remarks>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetDefId">Аркуш; <c>null</c> — рішення не про аркуш.</param>
    /// <param name="columnDefId">Колонка; <c>0</c> — рішення не про комірку.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="evaluateAccessWindow">
    /// Чи рахувати спрощене вікно доступу тут. <c>false</c> — виклик робить це
    /// сам через <see cref="PeriodAccessRules"/>, маючи повні факти (рядок,
    /// колонка, роль), а не лише аркуш і таблицю.
    /// </param>
    private async Task<CellAccessContext> BuildContextAsync(
        long documentId, PeriodKey periodKey, int? sheetDefId, int columnDefId,
        bool evaluateAccessWindow, CancellationToken ct)
    {
        var document = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.ProjectId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-DOC-0404", $"Документ {documentId} не знайдено.");

        var project = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == document.ProjectId)
            .Select(p => new { p.Status, p.IsArchiving, p.TemplateVersionId })
            .FirstAsync(ct)
            .ConfigureAwait(false);

        // Стан періоду — ЗБЕРЕЖЕНЕ значення, а не функція від now() (ФВ-1.12).
        var period = await db.Periods
            .AsNoTracking()
            .Where(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey.Value)
            .Select(p => new { p.State, p.Sequence })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var sheetStatus = sheetDefId is { } sheet
            ? await db.ApprovalStates
                .AsNoTracking()
                .Where(a => a.DocumentId == documentId
                            && a.SheetDefId == sheet
                            && a.PeriodKey == periodKey.Value)
                .Select(a => (DocumentStatus?)a.Status)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
            : null;

        // ⚠ Метадані читаються ЛИШЕ коли рішення справді про комірку.
        // Подання і затвердження до колонок не звертаються, і зайвий похід у
        // кеш шаблону тут не просто марний — він робить рішення про доступ
        // залежним від того, чи завантажується структура, якої це рішення не
        // стосується.
        ColumnDef? column = null;
        var tableDefId = 0;
        var effectiveSheet = sheetDefId ?? 0;

        if (columnDefId != 0)
        {
            var snapshot = await metadata.GetAsync(project.TemplateVersionId, ct).ConfigureAwait(false);
            column = snapshot.ColumnsById.TryGetValue(columnDefId, out var found) ? found : null;
            tableDefId = column?.TableDefId ?? 0;
            effectiveSheet = sheetDefId ?? SheetOf(snapshot, tableDefId);
        }

        var outOfWindow = evaluateAccessWindow
                          && period is not null
                          && await OutOfWindowAsync(
                              project.TemplateVersionId, effectiveSheet, tableDefId, period.Sequence, ct)
                              .ConfigureAwait(false);

        return new CellAccessContext(
            document.ProjectId,
            effectiveSheet,
            tableDefId,
            columnDefId,

            // Відсутній період трактується як Scheduled: «періоду ще немає» і
            // «період не відкрито» для користувача — та сама відмова.
            project.Status,
            project.IsArchiving,
            period?.State ?? PeriodState.Scheduled,
            outOfWindow,
            sheetStatus ?? DocumentStatus.Draft,
            column?.IsComputed ?? false,
            column?.IsReadOnly ?? false,
            RowIsReadOnly: false);
    }

    /// <summary>Аркуш, якому належить таблиця.</summary>
    private static int SheetOf(TemplateVersionSnapshot snapshot, int tableDefId)
    {
        foreach (var sheet in snapshot.Sheets)
        {
            foreach (var table in sheet.Tables)
            {
                if (table.Id == tableDefId)
                {
                    return sheet.Id;
                }
            }
        }

        return 0;
    }

    /// <summary>Чи виходить номер періоду за вікно доступу аркуша (ФВ-2.16).</summary>
    /// <remarks>
    /// ⚠ Береться ЛИШЕ вид <c>EditablePeriodOnly</c> і лише з блокувальною
    /// поведінкою. Решта п'яти видів говорить про рядок, колонку або довідник,
    /// а тут відомі тільки аркуш і таблиця: застосувати їх звідси означало б
    /// заборонити весь аркуш через правило про один рядок.
    ///
    /// ⛔ Поведінка звіряється теж. Без цього <c>Warn</c> і
    /// <c>AllowWithConfirmation</c> блокували б так само, як <c>ReadOnly</c>, і
    /// три поведінки <c>ФВ-2.16</c> тихо стали б однією — саме те, від чого
    /// вимога застерігає.
    /// </remarks>
    private async Task<bool> OutOfWindowAsync(
        int templateVersionId, int sheetDefId, int tableDefId, byte sequence, CancellationToken ct)
    {
        // ⚠ Застарілий <c>Hide</c> читається нарівні з <c>ReadOnly</c> (`H-1`):
        // в базі лежать рядки, записані до того, як вияснилося, що
        // ФВ-2.16 приховування не просила. Перестати їх бачити означало б
        // тихе відкриття доступу там, де він був закритий.
#pragma warning disable CS0618
        var rules = await db.PeriodAccessRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId
                        && r.RuleKind == PeriodAccessRuleKind.EditablePeriodOnly
                        && (r.OnOutOfWindow == OutOfWindowBehavior.Hide
                            || r.OnOutOfWindow == OutOfWindowBehavior.ReadOnly)
                        && (r.SheetDefId == null || r.SheetDefId == sheetDefId)
                        && (r.TableDefId == null || r.TableDefId == tableDefId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
#pragma warning restore CS0618

        // Правил немає — вікна немає: за замовчуванням доступні всі періоди.
        // Зворотне («немає правила — заборонено») зробило б кожен новий аркуш
        // недоступним, і ніхто б не зрозумів чому.
        return rules.Count > 0 && rules.TrueForAll(r => !r.AppliesTo(sequence));
    }

    /// <summary>Проєкт документа.</summary>
    private async Task<int> ProjectIdAsync(long documentId, CancellationToken ct)
        => await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => (int?)d.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
           ?? throw new NotFoundException("ECR-DOC-0404", $"Документ {documentId} не знайдено.");

    /// <summary>Знімок структури шаблону документа.</summary>
    private async Task<TemplateVersionSnapshot> SnapshotAsync(long documentId, CancellationToken ct)
    {
        var templateVersionId = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Join(db.Projects, d => d.ProjectId, p => p.Id, (_, p) => p.TemplateVersionId)
            .FirstAsync(ct)
            .ConfigureAwait(false);

        return await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
    }
}
