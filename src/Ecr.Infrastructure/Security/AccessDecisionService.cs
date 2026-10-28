using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
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
    Application.Ports.IWorkflowStore workflow,
    Application.Ports.IMonthColumnRouter? monthRouter = null) : IAccessDecisionService
{
    // ⚠ Q-17: `monthRouter` необов'язковий (тести будують службу напряму); `null` або вимкнений прапор
    // `Collection:MonthColumnRouting` = річних рішень немає, поведінка до Q-17.
    /// <summary>
    /// Розрахунок стану періоду для рішень (F-08). Без стану, тому один на
    /// тип: параметр конструктора зачепив би кожне місце, що створює службу.
    /// </summary>
    private static readonly Domain.Services.PeriodStateCalculator PeriodStates = new();

    /// <inheritdoc />
    public async Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct)
    {
        // ⛔ D-443 debt (кеш на кількох вузлах): ознака «учасник документа» читається ТУТ, на кожен запит, у тому
        // самому зверненні, що й штамп (підзапит EXISTS по IX_EditAssignmentRecipient_User, 0 нових звернень), і
        // входить у ключ кешу — як ревізія групових призначень (`GroupsFingerprintAsync`). Доти вона рахувалась лише
        // при побудові профілю: надання, видане на ІНШОМУ вузлі, скидало кеш лише там (`InvalidateUser` — пам'ять
        // процесу), і тут новий учасник не бачив документа до сплину профілю (до 60 хв). Тепер перший же запит після
        // коміту надання (чи зняття останнього) адресує інший запис кешу — на кожному вузлі, без спільного лічильника.
        var account = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.SecurityStamp,
                u.IsActive,
                Participates = db.EditAssignmentRecipients.Any(r => r.UserId == u.Id),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (account is null || !account.IsActive)
        {
            // Вимкнений запис не має «профілю без прав»: порожній профіль
            // виглядав би як звичайний користувач без грантів, а це різні речі
            // і в UI, і в журналі.
            throw new AccessDeniedException(
                "ECR-AUTH-0401", "Обліковий запис не існує або вимкнений.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.accountDisabled" });
        }

        // Ключ кешу — користувач + штамп + відбиток груп: зміна ролей крутить
        // штамп, тому старий запис просто перестає адресуватися (ФВ-6.7); а
        // відбиток груп рахується ТУТ, ОДИН раз, і йде ОДНОЧАСНО в реальний
        // ключ `IMemoryCache` (`GetOrCreateAsync`) і в `LoadAsync` — щоб ключ,
        // за яким запис кладеться, і ключ, під яким `AccessProfile` сам себе
        // описує (`CacheKey`), не могли розійтися (`Q-187`).
        var groupSids = GroupSidsFor(userId);
        var groupsFingerprint = await GroupsFingerprintAsync(groupSids, ct).ConfigureAwait(false);

        // ⚠ Ознака учасника — у ТОЙ САМИЙ третій компонент ключа, що й відбиток груп: і ключ `IMemoryCache`, і
        // `AccessProfile.CacheKey` будуються з одного рядка (`Q-187`), а профіль — саме з цього значення ознаки.
        var participates = account.Participates;

        // ⛔ AN-138: відбиток вмісту рядків призначень (ScopeJson, межі дії) — у той самий третій компонент ключа.
        // Лічильники/Id вище не бачать прямої SQL-правки наявного рядка; без цього застарілі межі жили до TTL.
        var assignmentsFingerprint = await AssignmentRowsFingerprintAsync(userId, groupSids, ct).ConfigureAwait(false);
        var cacheVariant = ParticipationKeyed(groupsFingerprint + assignmentsFingerprint, participates);

        var profile = await profileCache
            .GetOrCreateAsync(
                userId, account.SecurityStamp, cacheVariant,
                token => LoadAsync(userId, account.SecurityStamp, groupSids, cacheVariant, participates, token), ct)
            .ConfigureAwait(false);

        // ⛔ Право запису інтеграції — ПОВЕРХ кешованого профілю, на кожен
        // виклик, і лише коли профіль будується для самого автора задачі
        // інтеграції. У кеш (`LoadAsync`) воно не потрапляє ніколи: ключ кешу —
        // `userId`, і прапорець усередині нього означав би, що той, хто першим
        // зігрів кеш (HTTP-запит чи задача), визначає права іншого.
        return currentUser.IsIntegrationJob && currentUser.UserId == userId
            ? IntegrationWriter(profile)
            : profile;
    }

    /// <summary>Профіль автора задачі інтеграції поверх його звичайного профілю.</summary>
    /// <param name="own">Звичайний (кешований) профіль технічного запису.</param>
    /// <remarks>
    /// ⚠ Новий об'єкт, а не зміна кешованого: кешований спільний для всіх
    /// викликів цього <c>userId</c>.
    ///
    /// ⚠ Від звичайного профілю лишаються лише ЗАБОРОНИ (<c>IsDeny</c>) — ними
    /// адміністратор вимикає запис збору в конкретний проєкт. Гранти, ролі й
    /// функціональні права — порожні: у задачі інтеграції права визначає
    /// контекст, а не те, що комусь спало на думку призначити технічному
    /// запису (саме так тест «Аналізу» тимчасово давав йому `Write`).
    ///
    /// ⚠ Окремий <c>CacheKey</c> — щоб жоден споживач, що колись закешує щось
    /// за ключем профілю, не змішав два профілі того самого запису.
    /// </remarks>
    private static AccessProfile IntegrationWriter(AccessProfile own)
        => new()
        {
            CacheKey = own.CacheKey + "|integration-job",
            UserId = own.UserId,
            SecurityStamp = own.SecurityStamp,
            Permissions = new HashSet<string>(StringComparer.Ordinal),
            Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
            Denies = own.Denies,
            RoleIds = new HashSet<int>(),
            UnscopedRoleIds = new HashSet<int>(),

            // ⚠ Заборони ролей з областю дії (ФВ-6.14) лишаються в своїх
            // проєктах; гранти й ролі — порожні з тієї ж причини, що вище.
            Scoped = own.Scoped.ToDictionary(
                s => s.Key,
                s => s.Value with
                {
                    Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
                    RoleIds = new HashSet<int>(),
                    Permissions = new HashSet<string>(StringComparer.Ordinal),

                    // ⚠ D-214: заборони звужених ролей — у своїх аркушах і періодах.
                    Narrowed = [.. s.Value.Narrowed.Select(l => l with
                    {
                        Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
                        RoleIds = new HashSet<int>(),
                        Permissions = new HashSet<string>(StringComparer.Ordinal),
                    })],
                }),
            IsIntegrationWriter = true,
        };

    /// <summary>Відбиток груп сесії РАЗОМ із ревізією призначень на них.</summary>
    /// <remarks>
    /// ⛔ Відкликання ролі в групи мусить діяти негайно, а штамп крутити нема
    /// кому: членів групи система поіменно не знає. Тому в ключ кешу входить
    /// ще й ревізія — кількість і найбільший Id призначень на SID цієї сесії.
    /// Id — IDENTITY, тож будь-яке додавання чи відкликання міняє пару, і
    /// запис зі старими правами перестає адресуватися на НАСТУПНОМУ запиті —
    /// на кожному інстансі, без спільного лічильника в пам'яті.
    ///
    /// ⛔ Те саме — для ГРАНТІВ ролей, отриманих через групу. Заміна грантів
    /// ролі крутить штамп лише прямим носіям (<c>RotateStampsForRoleAsync</c>):
    /// член групи зберігав би знятий грант до сплину профілю (30 хв). Тому в
    /// ревізію входять ще кількість і найбільший Id рядків
    /// <c>sec.ResourceGrant</c> цих ролей. <c>ReplaceGrantsAsync</c> видаляє й
    /// вставляє, а Id — IDENTITY: вставка піднімає максимум, видалення без
    /// вставки зменшує кількість — пара міняється за будь-якої зміни.
    ///
    /// ⚠ Ціна — ОДИН запит на запит сесії з групами (призначення й гранти
    /// разом, підзапитом); сесія без груп не платить нічого.
    /// </remarks>
    private async Task<string> GroupsFingerprintAsync(IReadOnlyList<string> groupSids, CancellationToken ct)
    {
        if (groupSids.Count == 0)
        {
            return string.Empty;
        }

        // Ті самі призначення й та сама стеля, що в `LoadAsync`: ревізія
        // описує рівно ту вибірку, з якої профіль будується.
        var assignments = db.RoleAssignments
            .AsNoTracking()
            .Where(a => a.PrincipalSid != null && groupSids.Contains(a.PrincipalSid))
            .OrderByDescending(a => a.Id)
            .Take(MaxRoleAssignments);

        var grants = db.ResourceGrants
            .AsNoTracking()
            .Where(g => assignments.Any(a => a.RoleId == g.RoleId));

        var revision = await assignments
            .GroupBy(_ => 1)
            .Select(set => new
            {
                Assignments = set.Count(),
                LastAssignment = set.Max(a => a.Id),
                Grants = grants.Count(),
                LastGrant = grants.Max(g => (int?)g.Id) ?? 0,
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return revision is null
            ? $"{Fingerprint(groupSids)}.0.0.0.0"
            : $"{Fingerprint(groupSids)}.{revision.Assignments}.{revision.LastAssignment}.{revision.Grants}.{revision.LastGrant}";
    }

    /// <summary>Відбиток ВМІСТУ рядків призначень, з яких будується профіль (AN-138).</summary>
    /// <remarks>
    /// ⛔ Чому не <c>CHECKSUM_AGG</c>: той слабкий (XOR 32-бітних хешів, колізії тривіальні, а колізія тут =
    /// застарілі права — небезпека, не незручність). Вибірка обмежена <see cref="MaxRoleAssignments"/> (≤200
    /// вузьких рядків, ті самі, що читає <see cref="LoadAsync"/>), тому SHA-256 рахується в застосунку: сталий між
    /// вузлами, без схеми/міграції/SQL-функцій, 128 біт у ключі. Хешуються Id, роль, субʼєкт, ScopeJson,
    /// ValidFrom/ValidTo; вставка/видалення рядка змінює набір. Один індексований запит (без груп — по UserId).
    /// Не покриває ПЕРЕХІД часу (ValidTo минає опівночі UTC) — це досі TTL профілю, як і було.
    /// </remarks>
    private async Task<string> AssignmentRowsFingerprintAsync(
        int userId, IReadOnlyList<string> groupSids, CancellationToken ct)
    {
        var rows = await db.RoleAssignments
            .AsNoTracking()
            .Where(a => a.UserId == userId || (a.PrincipalSid != null && groupSids.Contains(a.PrincipalSid)))
            .OrderByDescending(a => a.Id)
            .Take(MaxRoleAssignments)
            .Select(x => new { x.Id, x.RoleId, x.UserId, x.PrincipalSid, x.ScopeJson, x.ValidFrom, x.ValidTo })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var text = new System.Text.StringBuilder();
        foreach (var r in rows)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"{r.Id}\u001f{r.RoleId}\u001f{r.UserId}\u001f")
                .Append(r.PrincipalSid is null ? "N" : "V" + r.PrincipalSid).Append('\u001f')
                .Append(r.ScopeJson is null ? "N" : "V" + r.ScopeJson).Append('\u001f')
                .Append(r.ValidFrom?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "N").Append('\u001f')
                .Append(r.ValidTo?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "N")
                .Append('\u001e');
        }

        return "|a" + System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    /// <summary>
    /// Третій компонент ключа кешу профілю: відбиток груп плюс ознака учасника документа (D-443 debt).
    /// </summary>
    /// <param name="groupsFingerprint">Відбиток груп із ревізією (<see cref="GroupsFingerprintAsync"/>).</param>
    /// <param name="participates">Чи має користувач бодай один рядок виконавця передачі/надання.</param>
    /// <remarks>
    /// ⚠ Без учасника — рядок відбитку БЕЗ змін: ключі наявних профілів не зсуваються, а «так» і «ні» не можуть
    /// дати однаковий ключ (суфікс містить `|`, якого в шістнадцятковому відбитку груп немає).
    /// </remarks>
    private static string ParticipationKeyed(string groupsFingerprint, bool participates)
        => participates ? groupsFingerprint + "|participant" : groupsFingerprint;

    /// <inheritdoc />
    public Task InvalidateProfileAsync(int userId, CancellationToken ct)
    {
        // ⚠ Скидаються ВСІ записи користувача за будь-якого штампа й відбитку
        // груп. Раніше ціль обчислювалась як ключ із відбитком ПОТОЧНОЇ сесії
        // (`GroupSidsFor`): для чужого користувача це порожній відбиток, і
        // реальний запис (з групами) лишався б у кеші до TTL (60 хв,
        // `Cache:AccessProfileSlidingMinutes`).
        profileCache.InvalidateUser(userId);
        return Task.CompletedTask;
    }

    /// <summary>Групи, з якими будується профіль користувача <paramref name="userId"/>.</summary>
    /// <remarks>
    /// ⚠ Групи беруться З ТОКЕНА поточної сесії (ФВ-6.15a), і лише тоді, коли
    /// профіль будується для НЕЇ САМОЇ. Для чужого користувача — перегляд
    /// адміністратором, симуляція — токена в нас немає, і взяти чужі групи зі
    /// своєї сесії означало б показати не ті права (`P-02`).
    /// </remarks>
    private IReadOnlyList<string> GroupSidsFor(int userId)
        => userId == currentUser.UserId ? currentUser.GroupSids : [];

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
    /// <param name="userId">Користувач, для якого будується профіль.</param>
    /// <param name="securityStamp">Його поточний штамп безпеки.</param>
    /// <param name="groupSids">
    /// Групи, з якими рахувати профіль — вважай <see cref="GroupSidsFor"/>
    /// єдиним місцем, що вирішує, чи вони тут є (`Q-187`): рахувати їх ще раз
    /// тут означало б друге формулювання того самого правила поруч із
    /// ключем кешу, який на ньому й тримається.
    /// </param>
    /// <param name="groupsFingerprint">Той самий третій компонент ключа, під яким запис ляже в кеш.</param>
    /// <param name="participates">
    /// Ознака учасника, прочитана разом зі штампом (<see cref="BuildProfileAsync"/>) — та сама, що в ключі кешу.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    private async Task<AccessProfile> LoadAsync(
        int userId, string securityStamp, IReadOnlyList<string> groupSids, string groupsFingerprint,
        bool participates, CancellationToken ct)
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
            // Найновіші призначення першими — той самий порядок, що й у
            // відбитку груп вище: на стелі обидва бачать ОДНІ Й ТІ САМІ
            // призначення, а не дві різні довільні вибірки (EF 10102).
            .OrderByDescending(a => a.Id)
            .Take(MaxRoleAssignments)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var effective = assignments.Where(a => a.IsEffectiveOn(today)).ToList();

        // ⛔ ФВ-6.14: призначення з областю дії дає свої права ЛИШЕ в
        // перелічених проєктах. Роль → проєкти її областей (об'єднання, якщо
        // роль прийшла кількома призначеннями — особистим і груповим).
        var unscopedRoleIds = effective
            .Where(a => a.ScopeJson is null)
            .Select(a => a.RoleId)
            .ToHashSet();
        var scopedProjects = new Dictionary<int, HashSet<int>>();

        // ⛔ D-214: призначення, чия область звужена ще й аркушами чи періодами,
        // — окремо, кожне своїм шаром: злиття з іншими дало б їхнім грантам
        // увесь проєкт.
        var narrowedScopes = new List<(int RoleId, RoleAssignmentScope Scope)>();
        foreach (var assignment in effective.Where(a => a.ScopeJson is not null))
        {
            var scope = RoleAssignmentScope.TryParse(assignment.ScopeJson!);
            if (scope is { IsNarrowed: true })
            {
                narrowedScopes.Add((assignment.RoleId, scope));
                continue;
            }

            if (!scopedProjects.TryGetValue(assignment.RoleId, out var projects))
            {
                scopedProjects[assignment.RoleId] = projects = [];
            }

            // Зіпсована область (`null`) — порожньо: роль не діє ніде.
            projects.UnionWith(scope?.ProjectIds ?? []);
        }

        var roleIds = unscopedRoleIds
            .Concat(scopedProjects.Keys)
            .Concat(narrowedScopes.Select(n => n.RoleId))
            .Distinct()
            .ToList();

        var rolePermissions = roleIds.Count == 0
            ? []
            : await db.RolePermissions
                .AsNoTracking()
                .Where(rp => roleIds.Contains(rp.RoleId))
                .Select(rp => new { rp.RoleId, rp.PermissionCode })
                .ToListAsync(ct)
                .ConfigureAwait(false);

        // ⛔ Функціональні права ролей без області — глобальні (`Has(code)`).
        // Ролей з областю — лише в її проєктах (`Has(code, projectId)`) і лише
        // ПРОЄКТНІ: право без проєкту (`PermissionScopes.Global` —
        // `Security.*`, `System.*`, шаблони, довідники…) роль «лише в проєкті
        // A» не дає ніде — інакше вона дала б його всій системі.
        var permissions = rolePermissions
            .Where(rp => unscopedRoleIds.Contains(rp.RoleId))
            .Select(rp => rp.PermissionCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var scopedPermissions = rolePermissions
            .Where(rp => scopedProjects.ContainsKey(rp.RoleId) && !PermissionScopes.IsGlobal(rp.PermissionCode))
            .ToLookup(rp => rp.RoleId, rp => rp.PermissionCode);

        var registryViewRoles = rolePermissions
            .Where(rp => scopedProjects.ContainsKey(rp.RoleId) && rp.PermissionCode == "Registry.View")
            .Select(rp => rp.RoleId)
            .ToHashSet();

        var rows = roleIds.Count == 0
            ? []
            : await db.ResourceGrants
                .AsNoTracking()
                .Where(g => roleIds.Contains(g.RoleId))
                .Select(g => new { g.RoleId, g.ResourceKind, g.ResourceId, g.Level, g.IsDeny })
                .ToListAsync(ct)
                .ConfigureAwait(false);

        var grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal);
        var denies = new HashSet<string>(StringComparer.Ordinal);
        var layers = new Dictionary<int, (Dictionary<string, GrantLevel> Grants, HashSet<string> Denies)>();
        var registryLayers = new Dictionary<int, (HashSet<int> Denies, Dictionary<int, GrantLevel> Grants)>();

        foreach (var row in rows)
        {
            var key = $"{row.ResourceKind}:{row.ResourceId}";

            if (unscopedRoleIds.Contains(row.RoleId))
            {
                Merge(grants, denies, key, row.Level, row.IsDeny);
            }

            if (!scopedProjects.TryGetValue(row.RoleId, out var projects))
            {
                continue;
            }

            if (row.ResourceKind == ResourceKind.Project)
            {
                // Грант на проєкт області — у спільну мапу (ключ і так
                // називає проєкт); на проєкт поза областю — нікуди.
                if (projects.Contains(row.ResourceId))
                {
                    Merge(grants, denies, key, row.Level, row.IsDeny);
                }

                continue;
            }

            // P50: довідник проєкту не має, але Deny/Read-грант ролі з областю діє в документах ЇЇ проєктів (вибір запису Lookup):
            // інакше scoped View перебивав би власний Deny ролі (оракул існування запису). Дані вже прочитані - запитів немає.
            if (row.ResourceKind == ResourceKind.Registry)
            {
                foreach (var projectId in projects)
                {
                    if (!registryLayers.TryGetValue(projectId, out var registry))
                    {
                        registryLayers[projectId] = registry = (new HashSet<int>(), new Dictionary<int, GrantLevel>());
                    }

                    if (row.IsDeny)
                    {
                        registry.Denies.Add(row.ResourceId);
                    }
                    else if (!registry.Grants.TryGetValue(row.ResourceId, out var known) || known < row.Level)
                    {
                        registry.Grants[row.ResourceId] = row.Level;
                    }
                }

                continue;
            }

            // ⚠ Аркуш/таблиця/колонка — окремо на КОЖЕН проєкт області: їхні
            // ідентифікатори спільні для всіх проєктів шаблону. Решта видів
            // (довідник тощо) проєкту не має — роль з областю їх не дає.
            if (row.ResourceKind is not (ResourceKind.Sheet or ResourceKind.Table or ResourceKind.Column))
            {
                continue;
            }

            foreach (var projectId in projects)
            {
                if (!layers.TryGetValue(projectId, out var layer))
                {
                    layers[projectId] = layer = (
                        new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
                        new HashSet<string>(StringComparer.Ordinal));
                }

                Merge(layer.Grants, layer.Denies, key, row.Level, row.IsDeny);
            }
        }

        // P50b (Аудит P3-1): Registry-DENY ролі зі ЗВУЖЕНОЮ областю (D-214) діє в усіх проєктах її призначення - Deny завжди
        // сильніший, інакше він мовчки ігнорувався б за наявності іншої ролі з View (fail-open). View/Read-ГРАНТ такої ролі
        // лишається ІГНОРОВАНИМ (fail-closed): звужена роль доступу до довідників не дає.
        foreach (var (narrowedRoleId, narrowedScope) in narrowedScopes)
        {
            foreach (var denied in rows.Where(r => r.RoleId == narrowedRoleId && r.ResourceKind == ResourceKind.Registry && r.IsDeny))
            {
                foreach (var projectId in narrowedScope.ProjectIds)
                {
                    if (!registryLayers.TryGetValue(projectId, out var registry))
                    {
                        registryLayers[projectId] = registry = (new HashSet<int>(), new Dictionary<int, GrantLevel>());
                    }

                    registry.Denies.Add(denied.ResourceId);
                }
            }
        }

        var narrowed = await NarrowedLayersAsync(
                narrowedScopes,
                rows.Select(r => (r.RoleId, r.ResourceKind, r.ResourceId, r.Level, r.IsDeny)).ToList(),
                rolePermissions.Select(rp => (rp.RoleId, rp.PermissionCode)).ToList(),
                ct)
            .ConfigureAwait(false);

        var scoped = new Dictionary<int, ScopedProjectAccess>();
        foreach (var projectId in scopedProjects.Values.SelectMany(p => p).Concat(narrowed.Keys).Distinct())
        {
            var scopedHere = scopedProjects.Where(p => p.Value.Contains(projectId)).Select(p => p.Key).ToList();
            var rolesHere = unscopedRoleIds.ToHashSet();
            rolesHere.UnionWith(scopedHere);
            var permissionsHere = scopedHere.SelectMany(r => scopedPermissions[r]).ToHashSet(StringComparer.Ordinal);

            scoped[projectId] = (layers.TryGetValue(projectId, out var layer)
                ? new ScopedProjectAccess(layer.Grants, layer.Denies, rolesHere, permissionsHere)
                : new ScopedProjectAccess(
                    new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
                    new HashSet<string>(StringComparer.Ordinal),
                    rolesHere,
                    permissionsHere)) with
            {
                Narrowed = narrowed.TryGetValue(projectId, out var here) ? here : [],
                RegistryDenies = registryLayers.TryGetValue(projectId, out var regHere) ? regHere.Denies : new HashSet<int>(),
                RegistryGrants = registryLayers.TryGetValue(projectId, out var regGrants)
                    ? regGrants.Grants
                    : new Dictionary<int, GrantLevel>(),

                // P48: Registry.View ролі, призначеної на цей проєкт, - лише для вибору запису Lookup.
                RegistryView = scopedHere.Any(registryViewRoles.Contains),
            };
        }

        // ✎ D-443: ознака «учасник якогось документа» — з `BuildProfileAsync` (EXISTS у запиті штампа, на кожен
        // запит) і в ключі кешу: профіль ніколи не лежить у кеші з ознакою, що розійшлася з базою (D-443 debt).
        // Самі надання читаються з бази на кожне рішення.
        // ⛔ Успадкування Project → Sheet → Table → Column тут НЕ розгортається
        // в мапу: його робить EditRules.Effective на кожному рішенні. Причина
        // не в економії — розгорнута мапа зафіксувала б структуру шаблону на
        // момент побудови профілю, і новий аркуш успадкував би права лише
        // після перевходу користувача.
        return new AccessProfile
        {
            CacheKey = Caching.AccessProfileCache.Key(userId, securityStamp, groupsFingerprint),
            UserId = userId,
            SecurityStamp = securityStamp,
            Permissions = permissions.ToHashSet(StringComparer.Ordinal),
            Grants = grants,
            Denies = denies,
            RoleIds = roleIds.ToHashSet(),
            // ⛔ Не `null`, щойно є бодай одна область: інакше `RoleIdsIn`
            // упав би на `RoleIds`, а там ролі з областю — в усіх проєктах.
            UnscopedRoleIds = scopedProjects.Count == 0 && narrowedScopes.Count == 0 ? null : unscopedRoleIds,
            Scoped = scoped,
            HasDocumentParticipations = participates,
        };
    }

    /// <summary>
    /// Шари ролей, звужених аркушами чи періодами (D-214): проєкт → шари.
    /// </summary>
    /// <param name="narrowedScopes">Роль і розібрана область кожного такого призначення.</param>
    /// <param name="grantRows">Гранти всіх ролей профілю.</param>
    /// <param name="rolePermissions">Права всіх ролей профілю.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Шар — на кожну пару «призначення × проєкт області». Гранти: на сам
    /// проєкт (лише цей) і на аркуш, таблицю, колонку — решта видів проєкту не
    /// має. Права: лише рівня документа (<see cref="PermissionScopes.Narrowable"/>).
    ///
    /// ⛔ Документ «поза проміжком періодів» — це документ проєкту, жоден
    /// період якого не потрапляє в проміжок області: шару для такого проєкту
    /// немає зовсім, тож роль там не відкриває навіть документа. Проєкт без
    /// жодного періоду — так само (закрито за замовчуванням). Ціна — один
    /// запит, і лише коли є звуження за періодами.
    /// </remarks>
    private async Task<Dictionary<int, List<NarrowedAccess>>> NarrowedLayersAsync(
        List<(int RoleId, RoleAssignmentScope Scope)> narrowedScopes,
        List<(int RoleId, ResourceKind Kind, int ResourceId, GrantLevel Level, bool IsDeny)> grantRows,
        List<(int RoleId, string PermissionCode)> rolePermissions,
        CancellationToken ct)
    {
        var result = new Dictionary<int, List<NarrowedAccess>>();
        if (narrowedScopes.Count == 0)
        {
            return result;
        }

        var periodProjects = narrowedScopes
            .Where(n => n.Scope.HasPeriods)
            .SelectMany(n => n.Scope.ProjectIds)
            .Distinct()
            .ToList();

        var spans = periodProjects.Count == 0
            ? []
            : (await db.Periods
                    .AsNoTracking()
                    .Where(p => periodProjects.Contains(p.ProjectId))
                    .GroupBy(p => p.ProjectId)
                    .Select(g => new { ProjectId = g.Key, First = g.Min(p => p.PeriodKeyValue), Last = g.Max(p => p.PeriodKeyValue) })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToDictionary(s => s.ProjectId, s => (First: new PeriodKey(s.First), Last: new PeriodKey(s.Last)));

        var grantsByRole = grantRows.ToLookup(r => r.RoleId);
        var permissionsByRole = rolePermissions
            .Where(p => PermissionScopes.IsNarrowable(p.PermissionCode))
            .ToLookup(p => p.RoleId, p => p.PermissionCode);

        foreach (var (roleId, scope) in narrowedScopes)
        {
            IReadOnlySet<string>? sheets = scope.SheetCodes.Count == 0
                ? null
                : scope.SheetCodes.ToHashSet(StringComparer.Ordinal);
            var permissions = permissionsByRole[roleId].ToHashSet(StringComparer.Ordinal);

            foreach (var projectId in scope.ProjectIds)
            {
                if (scope.HasPeriods
                    && (!spans.TryGetValue(projectId, out var span) || !scope.OverlapsPeriods(span.First, span.Last)))
                {
                    continue;
                }

                var grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal);
                var denies = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in grantsByRole[roleId])
                {
                    var own = row.Kind == ResourceKind.Project
                        ? row.ResourceId == projectId
                        : row.Kind is ResourceKind.Sheet or ResourceKind.Table or ResourceKind.Column;
                    if (own)
                    {
                        Merge(grants, denies, $"{row.Kind}:{row.ResourceId}", row.Level, row.IsDeny);
                    }
                }

                if (!result.TryGetValue(projectId, out var list))
                {
                    result[projectId] = list = [];
                }

                list.Add(new NarrowedAccess(
                    sheets, scope.PeriodFrom, scope.PeriodTo, grants, denies, new HashSet<int> { roleId }, permissions));
            }
        }

        return result;
    }

    /// <summary>Додає рядок гранта до мапи: заборона — у набір, дозвіл — ширший рівень.</summary>
    /// <remarks>
    /// Дві ролі на той самий ресурс — виграє ширший рівень: людина отримує
    /// суму своїх ролей, а не випадкову з них.
    /// </remarks>
    private static void Merge(
        Dictionary<string, GrantLevel> grants, HashSet<string> denies, string key, GrantLevel level, bool isDeny)
    {
        if (isDeny)
        {
            denies.Add(key);
            return;
        }

        grants[key] = grants.TryGetValue(key, out var existing) && existing > level ? existing : level;
    }

    /// <inheritdoc />
    public async Task<EditDecision> CanReadDocumentAsync(
        AccessProfile profile, long documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var projectId = await ProjectIdAsync(documentId, ct).ConfigureAwait(false);

        var byRoles = ReadDecision(profile, projectId, participates: false);
        if (byRoles.IsAllowed || !profile.HasDocumentParticipations || profile.DeniesProject(projectId))
        {
            return byRoles;
        }

        // ✎ D-443: надання учасника дає видимість документа без ролі на проєкт. Запит — лише тоді, коли ролі
        // документ не відкривають і профіль узагалі є учасником: решта читачів не платить нічого.
        return await ParticipatesAsync(profile, documentId, ct).ConfigureAwait(false)
            ? EditDecision.Allow()
            : byRoles;
    }

    /// <inheritdoc />
    public async Task<bool> ParticipatesAsync(AccessProfile profile, long documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.HasDocumentParticipations || profile.IsIntegrationWriter)
        {
            return false;
        }

        var documents = EditAssignmentFactsReader.ParticipatedDocuments(db, profile.UserId, clock.UtcNow);
        if (!await documents.AnyAsync(id => id == documentId, ct).ConfigureAwait(false))
        {
            return false;
        }

        // ⛔ P2-2: надання, чий делегатор втратив право його видавати, видимості не дає (рядок лишається, але не діє).
        var grants = await ParticipationGrantsAsync(profile, [documentId], ct).ConfigureAwait(false);
        return grants.TryGetValue(documentId, out var active) && active.Count > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<long>> ParticipatedDocumentsAsync(
        AccessProfile profile, IReadOnlyCollection<long> documentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(documentIds);

        if (!profile.HasDocumentParticipations || profile.IsIntegrationWriter || documentIds.Count == 0)
        {
            return new HashSet<long>();
        }

        var ids = documentIds.Distinct().ToArray();
        var found = await EditAssignmentFactsReader.ParticipatedDocuments(db, profile.UserId, clock.UtcNow)
            .Where(id => ids.Contains(id))
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return found.ToHashSet();
    }

    /// <inheritdoc />
    public async Task<bool> ParticipatesInProjectAsync(AccessProfile profile, int projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.HasDocumentParticipations || profile.IsIntegrationWriter || profile.DeniesProject(projectId))
        {
            return false;
        }

        var documents = EditAssignmentFactsReader.ParticipatedDocuments(db, profile.UserId, clock.UtcNow);
        var candidates = await db.Documents
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId && documents.Contains(d.Id))
            .Select(d => d.Id)
            .OrderBy(id => id)
            .Take(50)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return false;
        }

        // ⛔ P3 (календар періодів): надання, чий делегатор утратив право його видавати, проєкту не відкриває - те саме
        // динамічне правило, що й для документа (ParticipationGrantsAsync -> WithDelegatorRightsAsync).
        var grants = await ParticipationGrantsAsync(profile, [.. candidates], ct).ConfigureAwait(false);
        return grants.Values.Any(g => g.Count > 0);
    }

    /// <summary>
    /// D-443: діючі надання учасника в документах — з тих самих фактів, що й рішення про запис
    /// (<see cref="EditAssignmentFactsReader"/>). Без ознаки профілю чи для інтеграції — порожньо без бази.
    /// </summary>
    /// <param name="profile">Профіль учасника.</param>
    /// <param name="documentIds">Документи; для <paramref name="locked"/> — рівно один.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="locked">
    /// <c>true</c> — факти під <c>REPEATABLEREAD, ROWLOCK</c> (<see cref="EditAssignmentFactsReader"/>, той самий шлях,
    /// що гейт запису <c>DelegatedWrite</c>): лише всередині транзакції дії, див. <see cref="WithParticipationAsync"/>.
    /// </param>
    private async Task<IReadOnlyDictionary<long, IReadOnlyList<ParticipationGrant>>> ParticipationGrantsAsync(
        AccessProfile profile, IReadOnlyCollection<long> documentIds, CancellationToken ct, bool locked = false)
    {
        if (!profile.HasDocumentParticipations || profile.IsIntegrationWriter || documentIds.Count == 0)
        {
            return NoGrants;
        }

        var facts = await EditAssignmentFactsReader
            .LoadAsync(db, documentIds, profile.UserId, forUpdate: locked, ct, clock.UtcNow)
            .ConfigureAwait(false);

        var now = clock.UtcNow;
        var result = new Dictionary<long, IReadOnlyList<ParticipationGrant>>(facts.Count);
        foreach (var (documentId, found) in facts)
        {
            var checkedFacts = await WithDelegatorRightsAsync(documentId, found.OwnerUserId, found.Facts, now, ct)
                .ConfigureAwait(false);
            result[documentId] = DocumentParticipation.Active(checkedFacts, now);
        }

        return result;
    }

    /// <summary>
    /// ⛔ P2-2 (аудит 25.10): надання діє, лише поки делегатор СПРАВДІ може його видавати — активний, має право
    /// керувати передачами документа (<c>Manage</c> проєкту або автор з <c>Write</c>) і власний рівень на області не
    /// нижчий за виданий. Знижений чи звільнений менеджер більше не лишає «нащадків» з Submit/Approve.
    /// </summary>
    /// <remarks>
    /// ⚠ Без каскадних видалень: рядок передачі лишається й оживає, щойно делегатор відновить права. Для рівня
    /// <c>Write</c> (передача місяця) власний рівень на області не звіряється — видача Write меряється «видимими
    /// аркушами» (<c>SheetsBeyondOwnRights</c>), а не мінімумом по області; лишається вимога керувати передачами.
    /// Запит проєкту/шаблону й профілів делегаторів — лише коли є хоча б одне надання, що ще діє за іншими умовами.
    /// </remarks>
    private async Task<IReadOnlyList<EditAssignmentFact>> WithDelegatorRightsAsync(
        long documentId, int ownerUserId, IReadOnlyList<EditAssignmentFact> facts, DateTime now, CancellationToken ct)
    {
        static bool Candidate(EditAssignmentFact f, DateTime utcNow)
            => f.MyProgress is not null && f.DelegatorActive && f.Level != GrantLevel.None
               && !(f.ExpiresAt is { } until && until <= utcNow);

        if (!facts.Any(f => Candidate(f, now)))
        {
            return facts;
        }

        var scope = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Join(db.Projects, d => d.ProjectId, p => p.Id, (d, p) => new { d.ProjectId, p.TemplateVersionId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var profiles = new Dictionary<int, AccessProfile?>();
        TemplateVersionSnapshot? snapshot = null;
        var result = new List<EditAssignmentFact>(facts.Count);

        foreach (var fact in facts)
        {
            if (!Candidate(fact, now))
            {
                result.Add(fact);
                continue;
            }

            var valid = false;
            if (scope is not null)
            {
                if (!profiles.TryGetValue(fact.DelegatedByUserId, out var delegator))
                {
                    try
                    {
                        delegator = await BuildProfileAsync(fact.DelegatedByUserId, ct).ConfigureAwait(false);
                    }
                    catch (AccessDeniedException)
                    {
                        delegator = null;
                    }

                    profiles[fact.DelegatedByUserId] = delegator;
                }

                if (delegator is not null
                    && Application.Documents.EditAssignments.DelegatorAuthority.MayManage(
                        delegator, scope.ProjectId, ownerUserId))
                {
                    valid = true;
                    if (fact.Level != GrantLevel.Write)
                    {
                        snapshot ??= await metadata.GetAsync(scope.TemplateVersionId, ct).ConfigureAwait(false);
                        var slice = EditAssignmentSlice.Create(
                            fact.SheetCode, fact.TableCode, fact.MonthNumber, fact.Period?.Value);
                        valid = Application.Documents.EditAssignments.DelegatorAuthority.OwnLevelReaches(
                            delegator, scope.ProjectId, snapshot, slice, fact.Level);
                    }
                }
            }

            result.Add(valid ? fact : fact with { DelegatorActive = false });
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<long, IReadOnlyList<ParticipationGrant>> NoGrants =
        new Dictionary<long, IReadOnlyList<ParticipationGrant>>();

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<ParticipationGrant>>> ActiveParticipationsAsync(
        AccessProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.HasDocumentParticipations || profile.IsIntegrationWriter)
        {
            return NoGrants;
        }

        var ids = await EditAssignmentFactsReader.ParticipatedDocuments(db, profile.UserId, clock.UtcNow)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var grants = await ParticipationGrantsAsync(profile, [.. ids], ct).ConfigureAwait(false);
        return grants.Where(g => g.Value.Count > 0).ToDictionary(g => g.Key, g => g.Value);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Один запит (проєкт і версія шаблону разом) плюс знімок із кешу
    /// метаданих; самі рішення — чиста функція <see cref="EditRules.CanRead"/>
    /// без жодного звернення до бази.
    /// </remarks>
    public async Task<DocumentReadScope> ReadScopeAsync(
        AccessProfile profile, long documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var scope = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Join(db.Projects, d => d.ProjectId, p => p.Id, (d, p) => new { d.ProjectId, p.TemplateVersionId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-DOC-0404",
                $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        var snapshot = await metadata.GetAsync(scope.TemplateVersionId, ct).ConfigureAwait(false);

        // ✎ D-443: межі документа з наданнями учасника саме в ньому (лише для профілю-учасника; заборона проєкту
        // перемагає — тоді межі як за ролями, тобто закриті).
        var grants = profile.DeniesProject(scope.ProjectId)
            ? NoGrants
            : await ParticipationGrantsAsync(profile, [documentId], ct).ConfigureAwait(false);

        return DocumentReadScope.ForParticipant(
            profile, scope.ProjectId, snapshot, grants.TryGetValue(documentId, out var mine) ? mine : null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Один запит на документи (проєкт і версія), один — на надання (лише для профілю-учасника), знімки — з кешу
    /// метаданих раз на версію: стала кількість звернень на будь-яку сторінку переліку.
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, DocumentReadScope>> ReadScopesAsync(
        AccessProfile profile, IReadOnlyCollection<long> documentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(documentIds);

        var result = new Dictionary<long, DocumentReadScope>();
        if (documentIds.Count == 0)
        {
            return result;
        }

        var ids = documentIds.Distinct().ToArray();
        var rows = await db.Documents
            .AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .Join(db.Projects, d => d.ProjectId, p => p.Id, (d, p) => new { d.Id, d.ProjectId, p.TemplateVersionId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var grants = await ParticipationGrantsAsync(
                profile, [.. rows.Where(r => !profile.DeniesProject(r.ProjectId)).Select(r => r.Id)], ct)
            .ConfigureAwait(false);

        var snapshots = new Dictionary<int, TemplateVersionSnapshot>();
        foreach (var row in rows)
        {
            if (!snapshots.TryGetValue(row.TemplateVersionId, out var snapshot))
            {
                snapshot = await metadata.GetAsync(row.TemplateVersionId, ct).ConfigureAwait(false);
                snapshots[row.TemplateVersionId] = snapshot;
            }

            result[row.Id] = DocumentReadScope.ForParticipant(
                profile, row.ProjectId, snapshot, grants.TryGetValue(row.Id, out var mine) ? mine : null);
        }

        return result;
    }

    /// <summary>Чи бачить профіль документи проєкту — без походу в базу.</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="projectId">Проєкт документа.</param>
    /// <remarks>
    /// ⛔ Одне формулювання на два шляхи: <see cref="CanReadDocumentAsync"/> і
    /// дії робочого процесу (<see cref="CanSubmitAsync"/>,
    /// <see cref="CanApproveAsync"/>, <see cref="CanReopenAsync"/>), які
    /// проєкт уже мають із <see cref="BuildContextAsync"/> і другого запиту не
    /// платять.
    /// </remarks>
    /// <param name="participates">
    /// D-443: у користувача є діюче надання учасника в документі (<see cref="CellAccessContext.ParticipatesInDocument"/>).
    /// Відкриває документ лише профілю-учаснику й лише без заборони проєкту.
    /// </param>
    private static EditDecision ReadDecision(AccessProfile profile, int projectId, bool participates)
    {
        // Читання не залежить ні від стану періоду, ні від статусу аркуша:
        // закритий період і подана форма лишаються видимими — інакше звіт
        // неможливо було б навіть переглянути після подання.
        //
        // ⚠ Інтеграція читає документ будь-якого проєкту, куди пише (грантів у
        // неї немає за задумом), — крім проєкту з явною забороною.
        var integrationReads = profile.IsIntegrationWriter
                               && !profile.Denies.Contains($"{ResourceKind.Project}:{projectId}");

        //
        // ⚠ D-214: роль, звужена аркушами чи періодами, документ відкриває
        // (`SeesDocumentsOf`), але рівня проєкту не піднімає.
        var byParticipation = participates
                              && profile.HasDocumentParticipations
                              && !profile.IsIntegrationWriter
                              && !profile.DeniesProject(projectId);

        return integrationReads || profile.SeesDocumentsOf(projectId) || byParticipation
            ? EditDecision.Allow()
            : EditDecision.Deny(EditDenyReason.NoGrant);
    }

    /// <summary>
    /// Дія робочого процесу над документом, якого користувач не бачить, —
    /// відмова ДО будь-якої іншої причини (S2).
    /// </summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="context">Умови аркуша, уже зібрані.</param>
    /// <returns>Відмова <see cref="EditDenyReason.NoGrant"/> або <c>null</c> — видимість є.</returns>
    /// <remarks>
    /// ⛔ S2 (enterprise-аудит безпеки, 2026-09-28). Подання, затвердження й
    /// повернення в роботу не питали права ЧИТАТИ документ: вистачало гранта
    /// на аркуш, а той не прив'язаний до проєкту (див.
    /// <see cref="EditRules.Effective"/>). Невидимий документ змінював стан
    /// (порушення B-08).
    ///
    /// ⚠ Саме ПЕРШОЮ, а не лише через <see cref="EditRules.Effective"/>: там
    /// рівень рахується ОСТАННІМ, і відмова на невидимому документі казала б
    /// «аркуш у стані Draft» чи «період закритий» — тобто розповідала б про
    /// стан документа, якого для цієї людини не існує.
    /// </remarks>
    private static EditDecision? DenyIfInvisible(AccessProfile profile, CellAccessContext context)
    {
        var read = ReadDecision(profile, context.ProjectId, context.ParticipatesInDocument);
        return read.IsAllowed ? null : read;
    }

    /// <inheritdoc />
    public async Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // ⛔ EA-B (F-A): поштучний шлях — це ПАКЕТНИЙ шлях з однією адресою, а не
        // окрема копія `BuildContextAsync + EditRules.CanEdit`. Друга копія
        // не знала б про передачі зрізу (підйом і заборони) і мовчки обходила б
        // їх: саме так колись загубилося створення рядків.
        //
        // Рядок шукається в зрізі документа за парою «період × Id»; рядок
        // іншого документа чи видалений — відмова без розрізнення (як і
        // відсутність рішення в пакетного шляху).
        var instanceId = await (
                from r in db.TableRows.AsNoTracking()
                join t in db.TableInstances.AsNoTracking() on r.TableInstanceId equals t.Id
                where r.PeriodKeyValue == address.PeriodKey.Value
                      && r.Id == address.TableRowId
                      && !r.IsDeleted
                      && t.DocumentId == documentId
                select (long?)t.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (instanceId is not { } id)
        {
            return EditDecision.Deny(EditDenyReason.NoGrant);
        }

        var all = await CanEditCellsBatchAsync(
                profile, [new CellsAccessRequest(id, address.PeriodKey, [address])], ct)
            .ConfigureAwait(false);

        return all[id].TryGetValue(address, out var decision)
            ? decision
            : EditDecision.Deny(EditDenyReason.NoGrant);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ P8 (перф-аудит): поштучний метод — це пакетний
    /// <see cref="CanEditSlicesAsync"/> з одним екземпляром. Одна логіка на
    /// обидва шляхи: друга копія розійшлася б із першою мовчки, а різниця тут
    /// означала б, що перегляд імпорту й відкриття таблиці по-різному
    /// вирішують, що можна писати.
    /// </remarks>
    public async Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var all = await CanEditSlicesAsync(profile, [tableInstanceId], ct).ConfigureAwait(false);

        return all[tableInstanceId];
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ P8 (перф-аудит). Доти перегляд імпорту кликав
    /// <see cref="CanEditSliceAsync"/> на КОЖНУ таблицю книги — у типовому
    /// шаблоні ~91 × (5–7) звернень. Тут спільне для документа й періоду
    /// (знімок шаблону, умови доступу, правила періоду) читається ОДИН раз на
    /// групу «документ × період», стан аркушів — одним запитом на всі аркуші
    /// групи, а рядки — одним запитом на всі екземпляри періоду. Рішення
    /// рахує той самий <see cref="Decide"/>.
    /// </remarks>
    public Task<IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>> CanEditSlicesAsync(
        AccessProfile profile, IReadOnlyCollection<long> tableInstanceIds, CancellationToken ct)
        => SliceDecisionsAsync(profile, tableInstanceIds, ignoreColumnFlags: false, approverEdits: true, ct);

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ CELL-STYLE P1A: ТА САМА підготовка зрізу й ТОЙ САМИЙ <see cref="Decide"/>, що в
    /// <see cref="CanClearCellsAsync"/> (рішення <c>PATCH …/cell-styles</c>), — відрізняється від
    /// <see cref="CanEditSliceAsync"/> єдиним: ознаки колонки «обчислювана»/«лише читання» не враховуються.
    /// Друга копія правил розійшлася б із записом, і клієнт увімкнув би кнопку, яку сервер відхилить.
    /// </remarks>
    public async Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanStyleSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // ⚠ D-442: правка погоджувача — лише ЗНАЧЕННЯ; колір комірок поданого аркуша лишається закритим
        // (`PatchCellStylesHandler` під блокуванням відмовляє Submitted), тож і рішення без ролі погоджувача.
        var all = await SliceDecisionsAsync(profile, [tableInstanceId], ignoreColumnFlags: true, approverEdits: false, ct)
            .ConfigureAwait(false);

        return all[tableInstanceId];
    }

    private async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>> SliceDecisionsAsync(
        AccessProfile profile, IReadOnlyCollection<long> tableInstanceIds, bool ignoreColumnFlags, bool approverEdits,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tableInstanceIds);

        var result = new Dictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>(tableInstanceIds.Count);

        if (tableInstanceIds.Count == 0)
        {
            return result;
        }

        var slices = await SliceContextsAsync(profile, tableInstanceIds, approverEdits, ct).ConfigureAwait(false);

        foreach (var byPeriod in slices.GroupBy(s => s.Value.PeriodKey))
        {
            var ids = byPeriod.Select(s => s.Key).ToList();

            var rows = (await SlicesRowsQuery(db, ids, byPeriod.Key)
                    .Select(r => new { r.TableInstanceId, r.Id, r.RowKey })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToLookup(r => r.TableInstanceId);

            foreach (var (tableInstanceId, slice) in byPeriod)
            {
                var own = rows[tableInstanceId];
                var decisions = new Dictionary<CellAddress, EditDecision>(own.Count() * slice.Columns.Count);

                foreach (var row in own)
                {
                    // Вікна чинності записів довідника, на які посилається саме
                    // цей рядок: колонка → вікно. Порожньо — рядок нічого не обрав.
                    var sourceValues = slice.Rules.SourceWindows.TryGetValue(row.Id, out var windows)
                        ? windows
                        : EmptyWindows;

                    foreach (var column in slice.Columns)
                    {
                        decisions[new CellAddress(slice.PeriodKey, row.Id, column.Id)] =
                            Decide(profile, slice, row.RowKey, column, sourceValues, ignoreColumnFlags);
                    }
                }

                result[tableInstanceId] = decisions;
            }
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ <c>DIRECTIVE-14-ARCH.md</c>, <c>WR-03</c>. На відміну від
    /// <see cref="CanEditSliceAsync"/> рядки читаються ЛИШЕ ті, що входять у
    /// <paramref name="addresses"/> (<c>SliceRowsQuery</c> з <c>rowIds</c>), і
    /// <see cref="Decide"/> викликається лише на запитані адреси — не на
    /// <c>rows × columns</c> усього екземпляра. Спільна підготовка зрізу
    /// (<see cref="SliceContextsAsync"/>) лишається тією самою: вона й так
    /// коштує кілька запитів на весь зріз, а не на рядок.
    ///
    /// ⚠ P8: поштучний метод — це <see cref="CanEditCellsBatchAsync"/> з одним
    /// екземпляром; одна логіка на обидва шляхи.
    /// </remarks>
    public async Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditCellsAsync(
        AccessProfile profile, long tableInstanceId, PeriodKey periodKey,
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(addresses);

        var all = await CanEditCellsBatchAsync(
                profile, [new CellsAccessRequest(tableInstanceId, periodKey, addresses)], ct)
            .ConfigureAwait(false);

        return all[tableInstanceId];
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ P8 (застосування імпорту книги). Спільне для документа й періоду —
    /// ОДИН раз на групу (<see cref="SliceContextsAsync"/>), рядки — ОДНИМ
    /// запитом на всі екземпляри одного запитаного періоду
    /// (<see cref="SlicesRowsQuery"/> з <c>rowIds</c>), рішення — той самий
    /// <see cref="Decide"/>.
    ///
    /// ⛔ Рядок шукається за парою «екземпляр × <c>TableRow.Id</c>», а не за
    /// самим <c>Id</c>: запит спільний для всіх екземплярів, і адреса з рядком
    /// СУСІДНЬОЇ таблиці інакше отримала б рішення під чужим зрізом — тоді як
    /// поштучний шлях її просто не знаходить.
    /// </remarks>
    public Task<IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>> CanEditCellsBatchAsync(
        AccessProfile profile, IReadOnlyCollection<CellsAccessRequest> requests, CancellationToken ct)
        => CanEditCellsCoreAsync(profile, requests, ignoreColumnFlags: false, approverEdits: true, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanClearCellsAsync(
        AccessProfile profile, long tableInstanceId, PeriodKey periodKey,
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(addresses);

        var all = await CanEditCellsCoreAsync(
                profile, [new CellsAccessRequest(tableInstanceId, periodKey, addresses)], ignoreColumnFlags: true,
                approverEdits: false, ct)
            .ConfigureAwait(false);

        return all[tableInstanceId];
    }

    private async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>> CanEditCellsCoreAsync(
        AccessProfile profile, IReadOnlyCollection<CellsAccessRequest> requests, bool ignoreColumnFlags,
        bool approverEdits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(requests);

        var result = new Dictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>(requests.Count);
        foreach (var request in requests)
        {
            if (!result.TryAdd(request.TableInstanceId, new Dictionary<CellAddress, EditDecision>()))
            {
                throw new ArgumentException(
                    $"Екземпляр {request.TableInstanceId} запитано двічі.", nameof(requests));
            }
        }

        // ⚠ Екземпляр без адрес — порожнє рішення без жодного звернення до
        // бази, як і в поштучного шляху (і без перевірки його існування).
        var asked = requests.Where(r => r.Addresses.Count > 0).ToList();
        if (asked.Count == 0)
        {
            return result;
        }

        var slices = await SliceContextsAsync(profile, [.. asked.Select(r => r.TableInstanceId)], approverEdits, ct)
            .ConfigureAwait(false);

        foreach (var byPeriod in asked.GroupBy(r => r.PeriodKey))
        {
            var rowIds = byPeriod.SelectMany(r => r.Addresses.Select(a => a.TableRowId)).Distinct().ToList();

            // ⛔ WR-03: рядки батчу, не всі рядки екземплярів — `Id IN (…)`
            // поверх предиката партиції.
            var rows = (await SlicesRowsQuery(db, [.. byPeriod.Select(r => r.TableInstanceId)], byPeriod.Key, rowIds)
                    .Select(r => new { r.TableInstanceId, r.Id, r.RowKey })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToDictionary(r => (r.TableInstanceId, r.Id));

            foreach (var request in byPeriod)
            {
                var slice = slices[request.TableInstanceId];
                var columnsById = slice.Columns.ToDictionary(c => c.Id);
                var decisions = new Dictionary<CellAddress, EditDecision>(request.Addresses.Count);

                foreach (var address in request.Addresses)
                {
                    // ⚠ Немає рядка чи колонки в зрізі (рядок видалено, належить
                    // іншому екземпляру, або ColumnDefId з іншої таблиці) —
                    // адреса просто відсутня в результаті, так само як і в
                    // CanEditSliceAsync. Викликач (`PatchCellsHandler.EnsureAccessAsync`)
                    // уже трактує відсутність рішення як відмову сам.
                    if (!rows.TryGetValue((request.TableInstanceId, address.TableRowId), out var row)
                        || !columnsById.TryGetValue(address.ColumnDefId, out var column))
                    {
                        continue;
                    }

                    var sourceValues = slice.Rules.SourceWindows.TryGetValue(row.Id, out var windows)
                        ? windows
                        : EmptyWindows;

                    decisions[address] = Decide(profile, slice, row.RowKey, column, sourceValues, ignoreColumnFlags);
                }

                result[request.TableInstanceId] = decisions;
            }
        }

        return result;
    }

    /// <summary>Рядки зрізу — запит, який іде і в бойовий шлях, і в сторожа <c>WR-05</c>.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="periodKey">Період екземпляра — він же ключ партиції.</param>
    /// <param name="rowIds">
    /// <c>null</c> — усі рядки зрізу (<see cref="CanEditSliceAsync"/>); інакше —
    /// лише перелічені (<c>WR-03</c>, <see cref="CanEditCellsAsync"/>): <c>Id IN (…)</c>
    /// поверх того самого предиката партиції, без зайвого читання решти рядків.
    /// </param>
    /// <returns>Незавершений запит; проєкцію добирає викликач.</returns>
    /// <remarks>
    /// ⛔ <paramref name="periodKey"/> тут не «на всяк випадок». Кластерний ключ
    /// <c>doc.TableRow</c> — <c>(PeriodKey, Id)</c>, унікальний індекс —
    /// <c>(PeriodKey, TableInstanceId, RowKey)</c>, і обидва вирівняні по
    /// <c>ps_ByPeriodKey</c>. Фільтр самим лише <c>TableInstanceId</c> не дає
    /// засічки партиції: СУБД читає ВСІ партиції, щоб знайти рядки одного
    /// екземпляра. Цей самий клас дефекту вже ловили в
    /// <c>RowStore.TouchRowsAsync</c> — 34 308 логічних читань проти 60.
    ///
    /// ⚠ Запит винесений у <b>public static</b> навмисно, і це не витік
    /// внутрішньої будови: сторож <c>WR-05</c>
    /// (<c>Ecr.Architecture.Tests/PartitionKeyQueryTests</c>) бере
    /// <c>ToQueryString()</c> саме з цього методу. Сторож по ТЕКСТУ джерела в
    /// цьому репозиторії вже двічі коштував червоного гейта (червонів на
    /// власному коментарі й на переносі рядка), тому перевіряється згенерований
    /// SQL — а щоб перевірявся саме бойовий запит, а не його копія в тесті,
    /// бойовий шлях мусить ходити сюди ж.
    /// </remarks>
    public static IQueryable<TableRow> SliceRowsQuery(
        EcrDbContext db, long tableInstanceId, PeriodKey periodKey,
        IReadOnlyCollection<long>? rowIds = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var query = db.TableRows
            .AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value
                        && r.TableInstanceId == tableInstanceId
                        && !r.IsDeleted);

        return rowIds is null ? query : query.Where(r => rowIds.Contains(r.Id));
    }

    /// <summary>
    /// Рядки кількох зрізів одного періоду — одним запитом
    /// (<see cref="CanEditSlicesAsync"/>, P8).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="tableInstanceIds">Екземпляри таблиць, усі — за <paramref name="periodKey"/>.</param>
    /// <param name="periodKey">Період — він же ключ партиції.</param>
    /// <param name="rowIds">
    /// <c>null</c> — усі рядки зрізів (<see cref="CanEditSlicesAsync"/>); інакше —
    /// лише перелічені (<c>WR-03</c>, <see cref="CanEditCellsBatchAsync"/>).
    /// </param>
    /// <returns>Незавершений запит; проєкцію добирає викликач.</returns>
    /// <remarks>
    /// ⛔ Предикат партиції — той самий, що й у <see cref="SliceRowsQuery"/>
    /// (<c>WR-05</c>): <c>PeriodKey</c> першим, екземпляри — <c>IN (…)</c>
    /// поверх нього. <b>public static</b> — з тієї ж причини: сторож може
    /// взяти <c>ToQueryString()</c> саме бойового запиту.
    /// </remarks>
    public static IQueryable<TableRow> SlicesRowsQuery(
        EcrDbContext db, IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey,
        IReadOnlyCollection<long>? rowIds = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tableInstanceIds);

        var query = db.TableRows
            .AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value
                        && tableInstanceIds.Contains(r.TableInstanceId)
                        && !r.IsDeleted);

        return rowIds is null ? query : query.Where(r => rowIds.Contains(r.Id));
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ P8: поштучний метод — це <see cref="CanCreateRowsBatchAsync"/> з одним екземпляром.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, NewRowAccess>> CanCreateRowsAsync(
        AccessProfile profile, long tableInstanceId, IReadOnlyCollection<string> rowKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rowKeys);

        var all = await CanCreateRowsBatchAsync(
                profile, new Dictionary<long, IReadOnlyCollection<string>> { [tableInstanceId] = rowKeys }, ct)
            .ConfigureAwait(false);

        return all[tableInstanceId];
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ P8: спільне для зрізів — ОДИН раз на групу «документ × період»
    /// (<see cref="SliceContextsAsync"/>); рядків ще немає, тож рядки й не
    /// читаються. Екземпляр без ключів — порожньо, без звернення до бази.
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, NewRowAccess>>> CanCreateRowsBatchAsync(
        AccessProfile profile,
        IReadOnlyDictionary<long, IReadOnlyCollection<string>> rowKeysByInstance,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rowKeysByInstance);

        var result = new Dictionary<long, IReadOnlyDictionary<string, NewRowAccess>>(rowKeysByInstance.Count);
        foreach (var tableInstanceId in rowKeysByInstance.Keys)
        {
            result[tableInstanceId] = new Dictionary<string, NewRowAccess>(StringComparer.Ordinal);
        }

        var asked = rowKeysByInstance.Where(p => p.Value.Count > 0).Select(p => p.Key).ToList();
        if (asked.Count == 0)
        {
            return result;
        }

        // ⚠ D-442: рядки поданого аркуша погоджувач НЕ створює й не видаляє — правка лише значень
        // (`CreateRowHandler`/`DeleteRowHandler` під блокуванням відмовляють Submitted).
        var slices = await SliceContextsAsync(profile, asked, approverEdits: false, ct).ConfigureAwait(false);

        foreach (var tableInstanceId in asked)
        {
            result[tableInstanceId] = NewRowDecisions(profile, slices[tableInstanceId],rowKeysByInstance[tableInstanceId]);
        }

        return result;
    }

    /// <summary>Рішення на рядки, яких ще немає, в одному зрізі.</summary>
    private static Dictionary<string, NewRowAccess> NewRowDecisions(
        AccessProfile profile, SliceContext slice, IReadOnlyCollection<string> rowKeys)
    {
        var result = new Dictionary<string, NewRowAccess>(rowKeys.Count, StringComparer.Ordinal);

        foreach (var rowKey in rowKeys)
        {
            // ⛔ Рішення на рядок рахується з колонкою 0 і без ознак
            // «обчислювана» / «лише для читання»: питання тут інше — чи можна
            // писати в цей зріз узагалі. Підставити сюди якусь колонку
            // означало б, що заборона на ОДНУ колонку забороняє створення
            // рядка цілком, а обчислювана перша колонка — завжди.
            // Q-17: склад рядків таблиці «місяці в колонках» — спільний на рік, тож і створення рядка рахується за річним агрегатом.
            var rowContext = (slice.Annual?.Context ?? slice.Shared) with
            {
                TableDefId = slice.TableDefId,
                ColumnDefId = 0,
                ColumnIsComputed = false,
                ColumnIsReadOnly = false,
                RowIsReadOnly = RowDefOf(slice, rowKey)?.IsReadOnly ?? false,
            };

            // Q-17 (умови вмикання): рядок належить ЯКОРЮ року - область ролі за періодами (D-214) для створення/видалення
            // рядка перевіряється за періодом якоря (найменший період календаря), а не за періодом екземпляра запиту.
            if (slice.RoutesOwnership && slice.Annual is { Periods.Count: > 0 } annualRow0)
            {
                rowContext = rowContext with { Period = new PeriodKey(annualRow0.Periods[0]) };
            }

            // ⛔ EA-B (D5): виконавець рядків НЕ створює — підйому на рівні рядка
            // немає; діють лише заборони передачі (рівень рядка, без місяця).
            if (slice.Delegation is { } delegation)
            {
                var facts = EditAssignmentGuard.Evaluate(
                    delegation.UserId,
                    delegation.OwnerUserId,
                    isIntegrationWriter: false,
                    delegation.Facts,
                    new EditSliceFacts(
                        slice.Shared.SheetCode, delegation.TableCode, slice.PeriodKey,
                        ColumnIsMonth: false, ColumnMonthNumber: null, IsRowLevel: true),
                    delegation.UtcNow);

                rowContext = rowContext with { AssignmentLock = facts.Lock, IsAssignmentExecutor = facts.IsExecutor };
            }

            var columns = new Dictionary<int, EditDecision>(slice.Columns.Count);
            foreach (var column in slice.Columns)
            {
                // ⚠ Вікон чинності в нового рядка немає за визначенням: вони
                // прив'язані до RowId, а його ще не існує. Порожній словник
                // тут — факт, а не заглушка.
                columns[column.Id] = Decide(profile, slice, rowKey, column, EmptyWindows);
            }

            var rowDecision = EditRules.CanEdit(profile, rowContext);
            if (slice.Annual is { } annualRow)
            {
                rowDecision = rowDecision with { AnnualPeriods = annualRow.Periods };
            }

            if (slice.DocumentHasAssignments)
            {
                rowDecision = rowDecision with { AssignmentsPresent = true };
            }

            result[rowKey] = new NewRowAccess(rowDecision, columns);
        }

        return result;
    }

    /// <summary>Рішення по одній комірці зрізу.</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="slice">Спільна для зрізу частина умов.</param>
    /// <param name="rowKey">Ключ рядка — наявного або того, що створюється.</param>
    /// <param name="column">Колонка.</param>
    /// <param name="sourceValues">Вікна чинності записів довідника цього рядка.</param>
    /// <param name="ignoreColumnFlags">
    /// <c>true</c> — «стерти значення» (D3-A): ознаки колонки «обчислювана»/«лише читання» не враховуються.
    /// </param>
    /// <remarks>
    /// ⚠ Спільне для наявних і для нових рядків. Різниця між ними — рівно два
    /// входи: <c>RowId</c> (у нового немає) і вікна чинності довідника (у
    /// нового порожні). Усе інше — та сама функція, і саме тому вона одна:
    /// друга копія розійшлася б із першою на першій же зміні правил.
    /// </remarks>
    private static EditDecision Decide(
        AccessProfile profile,
        SliceContext slice,
        string rowKey,
        ColumnDef column,
        IReadOnlyDictionary<int, SourceValidity> sourceValues,
        bool ignoreColumnFlags = false)
    {
        var def = RowDefOf(slice, rowKey);

        // ⛔ P2-A (слово людини 26.10: «заборона лише для поданих/погоджених/закритих»): колонка місяця k таблиці
        // MonthsInColumns підлягає стану СВОГО періоду k, а не періоду екземпляра. Підміняються лише ознаки періоду
        // (стан, стан аркуша); ґрант, роль, код аркуша, передачі й правила вікна — ті самі. Scheduled-період колонки
        // не блокує (внесення наперед), тож `MonthShared` уже несе для нього Open.
        var shared = slice.Shared;
        int? columnPeriod = null;
        if (column.IsMonthColumn
            && column.MonthNumber is { } columnMonth
            && slice.MonthShared.TryGetValue(columnMonth, out var monthContext))
        {
            shared = monthContext.Context;
            columnPeriod = monthContext.PeriodKey;
        }

        // Q-17: введений не-місячний стовпець таблиці «місяці в колонках» керується річним агрегатом, а не періодом
        // екземпляра (подання одного місяця його не блокує). Обчислювані стовпці лишаються за періодом перегляду.
        IReadOnlyList<int>? annualPeriods = null;
        if (slice.Annual is { } annual && !column.IsMonthColumn && !column.IsComputed)
        {
            shared = annual.Context;
            annualPeriods = annual.Periods;
        }

        // Q-17 (аудит P2-B): область ролі за періодами - за періодом ВЛАСНИКА колонки (місячний m -> період m; введений
        // не-місячний -> якір = найменший період календаря). Прапор вимкнено - період екземпляра, як раніше.
        if (slice.RoutesOwnership)
        {
            if (columnPeriod is { } ownerPeriod)
            {
                shared = shared with { Period = new PeriodKey(ownerPeriod) };
            }
            else if (annualPeriods is { Count: > 0 })
            {
                shared = shared with { Period = new PeriodKey(annualPeriods[0]) };
            }
        }

        var context = shared with
        {
            TableDefId = slice.TableDefId,
            ColumnDefId = column.Id,
            // S-2 (D3-A): для «стерти значення» ознаки колонки не заважають — грант перевіряється завжди.
            ColumnIsComputed = !ignoreColumnFlags && column.IsComputed,
            ColumnIsReadOnly = !ignoreColumnFlags && column.IsReadOnly,
            RowIsReadOnly = def?.IsReadOnly ?? false,
            // WW-02: комірку рахує комірочна формула — ознака рівня КОМІРКИ, не колонки й не рядка.
            CellIsComputed = !ignoreColumnFlags && slice.ComputedCells.Contains((rowKey, column.Id)),
        };

        // ⛔ EA-B: «передача місяця». Факти правила — на КОЖНУ комірку (місяць
        // колонки різний), але список передач уже звужено до зрізу, тож це
        // кілька порівнянь, не запит. Підйом Read → Write і заборони
        // (LockedByOwner/Filled/Validated/Expired) діють у `EditRules.CanEdit`
        // і ДО правил періоду шаблону нижче: ті перекривають передачу так само,
        // як звичайний дозвіл.
        if (slice.Delegation is { } delegation)
        {
            var facts = EditAssignmentGuard.Evaluate(
                delegation.UserId,
                delegation.OwnerUserId,
                isIntegrationWriter: false,
                delegation.Facts,
                new EditSliceFacts(
                    slice.Shared.SheetCode, delegation.TableCode, slice.PeriodKey,
                    column.IsMonthColumn, column.MonthNumber),
                delegation.UtcNow);

            context = context with
            {
                DelegatedWrite = facts.DelegatedWrite,
                AssignmentLock = facts.Lock,
                IsAssignmentExecutor = facts.IsExecutor,
            };
        }

        var decision = EditRules.CanEdit(profile, context);
        var viaAssignment = decision.ViaAssignment;
        var byApprover = decision.ByApprover;

        // ⚠ Правила періоду перевіряються ЛИШЕ там, де решта дозволила.
        // Інакше комірка в закритому періоді доповідала б про вікно дозволу
        // замість про сам період — причина має бути та, яку користувач здатен
        // усунути першою.
        if (decision.IsAllowed && slice.Rules.Rules.Count > 0)
        {
            var facts = new PeriodRuleFacts(
                slice.SheetDefId,
                slice.TableDefId,
                def?.RowKind ?? RowKind.Item,
                (byte)slice.PeriodKey.Sequence,
                slice.PeriodKey.Year,
                slice.Rules.CurrentSequence,
                column.MonthNumber,
                sourceValues,
                EmptyExpressions);

            // ⚠ Ролі, чинні в проєкті зрізу (ФВ-6.14): правило, обмежене роллю
            // з областю «проєкт A», не стосується цієї людини в проєкті B.
            // D-214: і звужені аркушем чи періодом — лише на своєму аркуші в своєму періоді.
            var outcome = PeriodAccessRules.Evaluate(
                slice.Rules.Rules,
                facts,
                profile.RoleIdsAt(shared.ProjectId, shared.SheetCode, shared.Period));

            if (outcome.Blocks)
            {
                decision = EditDecision.Deny(outcome.Reason, outcome.Detail);
            }
            else if (outcome.Reason != EditDenyReason.None
                     && outcome.Behavior == OutOfWindowBehavior.AllowWithConfirmation)
            {
                // ⛔ До цього гілка не існувала: `outcome`, який не блокує
                // (`Warn` і `AllowWithConfirmation` обидва такі — `ФВ-2.16`),
                // просто відкидався, і рішення лишалося звичайним `Allow()`.
                // `AllowWithConfirmation` від щойного дозволу нічим не
                // відрізнявся НІДЕ — жоден виклик далі по ланцюгу не бачив
                // навіть того, що правило спрацювало (`#43`).
                //
                // ⚠ `Warn` тут навмисно НЕ чіпається: третя, «позначкова»
                // поведінка `ФВ-2.16` — окрема, ще не підключена ланка
                // (клієнт не показує позначку взагалі), і розширювати цю
                // гілку на неї означало б закрити одну прогалину, вдаючи, що
                // закрив дві.
                decision = EditDecision.AllowWithConfirmation(outcome.Detail);
            }
            else if (outcome.Reason != EditDenyReason.None
                     && outcome.Behavior == OutOfWindowBehavior.Warn)
            {
                // ФВ-2.16 / D-239: Warn — правка проходить, але журнал
                // позначає її IsOutOfWindow. Підтвердження не потрібне.
                decision = EditDecision.AllowMarked(outcome.Detail);
            }
        }

        // Прапорець «через передачу» переживає правила періоду, що замінили
        // рішення на AllowWithConfirmation/AllowMarked.
        if (viaAssignment && decision.IsAllowed)
        {
            decision = decision with { ViaAssignment = true };
        }

        // ✎ D-442: так само «правка погоджувача» — інакше запис не повторив би перевірку погоджувача
        // під блокуванням і не поставив би позначку в журнал.
        if (byApprover && decision.IsAllowed)
        {
            decision = decision with { ByApprover = true };
        }

        if (columnPeriod is not null)
        {
            decision = decision with { ColumnPeriod = columnPeriod };
        }

        if (annualPeriods is not null)
        {
            decision = decision with { AnnualPeriods = annualPeriods };
        }

        return slice.DocumentHasAssignments ? decision with { AssignmentsPresent = true } : decision;
    }

    /// <summary>Опис рядка з шаблону за його ключем; <c>null</c> — рядок вільний.</summary>
    private static RowDef? RowDefOf(SliceContext slice, string rowKey)
        => slice.Snapshot.RowsByKey.TryGetValue((slice.TableDefId, rowKey), out var found) ? found : null;

    /// <summary>Спільна для зрізу частина умов доступу.</summary>
    /// <param name="PeriodKey">Період екземпляра таблиці.</param>
    /// <param name="TableDefId">Таблиця версії шаблону.</param>
    /// <param name="SheetDefId">Аркуш, якому належить таблиця.</param>
    /// <param name="Shared">Умови, однакові для всіх комірок зрізу.</param>
    /// <param name="Columns">Колонки саме цієї таблиці.</param>
    /// <param name="Snapshot">Знімок версії шаблону.</param>
    /// <param name="Rules">Правила доступу до періоду і те, що їм потрібно.</param>
    private sealed record SliceContext(
        PeriodKey PeriodKey,
        int TableDefId,
        int SheetDefId,
        CellAccessContext Shared,
        IReadOnlyList<ColumnDef> Columns,
        TemplateVersionSnapshot Snapshot,
        PeriodRuleContext Rules)
    {
        /// <summary>
        /// Передачі зрізу документа для ЦЬОГО користувача (EA-B); <c>null</c> — передач
        /// немає чи вони його не стосуються (документ без передач, симуляція, системний шлях).
        /// </summary>
        public DelegationFacts? Delegation { get; init; }

        /// <summary>У документа зрізу є передачі (ознака для повторної перевірки під гейтом).</summary>
        public bool DocumentHasAssignments { get; init; }

        /// <summary>
        /// Комірки таблиці, які рахує комірочна формула (<c>FormulaScope.Cell</c>): <c>(RowKey, ColumnDefId)</c>.
        /// Порожньо — таких формул у таблиці немає (більшість таблиць).
        /// </summary>
        public IReadOnlySet<(string RowKey, int ColumnDefId)> ComputedCells { get; init; } = NoComputedCells;

        /// <summary>
        /// P2-A (слово людини 26.10): умови місячних колонок ІНШИХ місяців, ніж період екземпляра, у таблиці
        /// <c>MonthsInColumns</c> місячного проєкту — номер місяця → (умови з ПЕРІОДОМ колонки, ключ періоду колонки).
        /// Порожньо — таблиця не місячна, місяць той самий, шлях інтеграції/симуляції.
        /// </summary>
        public IReadOnlyDictionary<byte, (CellAccessContext Context, int PeriodKey)> MonthShared { get; init; } = NoMonthShared;

        /// <summary>
        /// Q-17: «річний контекст» (агрегат стану періоду й аркуша за місяцями календаря, <see cref="AnnualPeriodRule"/>)
        /// для введених не-місячних стовпців і складу рядків таблиці «місяці в колонках» та ключі періодів
        /// календаря; <c>null</c> — прапор вимкнено, таблиця інша, перегляд поза календарем.
        /// </summary>
        public (CellAccessContext Context, IReadOnlyList<int> Periods)? Annual { get; init; }

        /// <summary>
        /// Q-17: маршрутизація власників діє (прапор + MonthsInColumns) - область ролі за ПЕРІОДАМИ для комірки колонки з
        /// іншим власником перевіряється за періодом ВЛАСНИКА, а не періодом екземпляра запиту.
        /// </summary>
        public bool RoutesOwnership { get; init; }
    }

    private static readonly IReadOnlySet<(string RowKey, int ColumnDefId)> NoComputedCells =
        new HashSet<(string, int)>();

    private static readonly IReadOnlyDictionary<byte, (CellAccessContext Context, int PeriodKey)> NoMonthShared =
        new Dictionary<byte, (CellAccessContext, int)>();

    /// <summary>
    /// <c>(RowKey, ColumnDefId)</c> комірок таблиці, які рахує комірочна формула шаблону (WW-02): користувач
    /// такі комірки не редагує (<see cref="EditDenyReason.CalculatedCell"/>), решта комірок їхнього рядка й
    /// колонки лишається вводом.
    /// </summary>
    /// <remarks>
    /// Формули беруться з уже прогрітого знімка версії (<c>MetadataCache</c> їх вантажить), тож нового звернення
    /// до бази це не коштує. Рядок формули → ключ рядка через <c>TableDef.Rows</c>.
    /// </remarks>
    private static IReadOnlySet<(string RowKey, int ColumnDefId)> ComputedCellsOf(
        TemplateVersionSnapshot snapshot, int tableDefId)
    {
        var table = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .FirstOrDefault(t => t.Id == tableDefId);

        if (table is null || !table.Formulas.Any(f => !f.IsDeleted && f.Scope == FormulaScope.Cell))
        {
            return NoComputedCells;
        }

        var keysById = table.Rows.Where(r => !r.IsDeleted).ToDictionary(r => r.Id, r => r.RowKeyValue);
        var result = new HashSet<(string, int)>();

        foreach (var formula in table.Formulas)
        {
            if (!formula.IsDeleted
                && formula.Scope == FormulaScope.Cell
                && formula.RowDefId is { } rowId
                && formula.ColumnDefId is { } columnId
                && keysById.TryGetValue(rowId, out var rowKey))
            {
                result.Add((rowKey, columnId));
            }
        }

        return result;
    }

    /// <summary>Передачі документа, звужені до зрізу, і те, що потрібно правилу <see cref="EditAssignmentGuard"/>.</summary>
    /// <param name="UserId">Хто пише.</param>
    /// <param name="OwnerUserId">Автор документа.</param>
    /// <param name="TableCode">Код таблиці зрізу; <c>null</c> — невідомий.</param>
    /// <param name="Facts">Передачі документа (вже звужені за аркушем/таблицею/періодом).</param>
    /// <param name="UtcNow">Годинник служби — для строку дії.</param>
    private sealed record DelegationFacts(
        int UserId,
        int OwnerUserId,
        string? TableCode,
        IReadOnlyList<EditAssignmentFact> Facts,
        DateTime UtcNow);

    /// <summary>
    /// Збирає все, що спільне для зрізів, ОДИН раз на групу «документ × період».
    /// </summary>
    /// <param name="profile">Профіль прав: чиї передачі зрізу (EA-B) читати.</param>
    /// <param name="tableInstanceIds">Екземпляри таблиць.</param>
    /// <param name="approverEdits">
    /// D-442: рахувати для поданих аркушів, чи користувач — погоджувач поточного кроку
    /// (<see cref="CellAccessContext.IsCurrentApprover"/>). Лише для рішень про ЗНАЧЕННЯ комірок; колір,
    /// очищення й рядки — без цього (<c>false</c>).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Екземпляр → умови його зрізу; кожен запитаний екземпляр присутній.</returns>
    /// <remarks>
    /// ⚠ Спільне для <see cref="CanEditSlicesAsync"/>, <see cref="CanEditCellsBatchAsync"/>
    /// і <see cref="CanCreateRowsBatchAsync"/> (а через них — для поштучних):
    /// усі три питання — про той самий зріз, і друга копія цієї підготовки
    /// розійшлася б із першою мовчки. Саме так колись і стався дефект, який
    /// закрив <see cref="CanCreateRowsAsync"/>: створення пішло іншим шляхом, на
    /// якому перевірки просто не було.
    ///
    /// ⛔ P8: єдина реалізація і для одного зрізу, і для книги імпорту. Кількість звернень — на ГРУПУ, а не на таблицю:
    /// екземпляри (1), знімок (1), умови доступу (1), стан решти аркушів
    /// групи (1, лише коли аркушів більше одного), правила періоду (1–3).
    /// </remarks>
    private async Task<IReadOnlyDictionary<long, SliceContext>> SliceContextsAsync(
        AccessProfile profile, IReadOnlyCollection<long> tableInstanceIds, bool approverEdits, CancellationToken ct)
    {
        var ids = tableInstanceIds.Distinct().ToList();

        // `WR-05`/O3: саме цей запит і ВИЗНАЧАЄ період, тож ключ партиції
        // береться з множини періодів — seek на партицію замість скану всіх
        // (`RowStore.TableInstancesByIdQuery`). Набір рядків той самий, що й
        // без ключа: незнайдене шукається ще раз колишнім запитом, тож
        // відмова нижче спрацьовує рівно тоді, коли й раніше.
        var instances = await RowStore.FindTableInstancesAsync(
                db,
                ids,
                q => q.Select(t => new { t.Id, t.DocumentId, t.TableDefId, t.PeriodKeyValue }),
                i => i.Id,
                ct)
            .ConfigureAwait(false);

        // ⛔ Відсутній екземпляр — та сама відмова, що й у поштучного шляху,
        // а не мовчазний пропуск: словник без запису викликач міг би прочитати
        // як «заборон немає».
        var found = instances.Select(i => i.Id).ToHashSet();
        foreach (var id in ids.Where(id => !found.Contains(id)))
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Екземпляр таблиці {id} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.tableInstance",
                    ["tableInstanceId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var result = new Dictionary<long, SliceContext>(instances.Count);


        foreach (var group in instances.GroupBy(i => (i.DocumentId, i.PeriodKeyValue)))
        {
            var documentId = group.Key.DocumentId;
            var periodKey = new PeriodKey(group.Key.PeriodKeyValue);

            var snapshot = await SnapshotAsync(documentId, ct).ConfigureAwait(false);

            // ⛔ Аркуш визначається ДО побудови умов, а не лишається нульовим.
            // З <c>sheetDefId: null</c> стан робочого процесу не читався взагалі й
            // підставлявся як <c>Draft</c> — тобто ПОДАНИЙ аркуш залишався
            // редаговним на єдиному шляху, яким запис і йде (<c>PatchCellsHandler</c>).
            // Перевірка в <see cref="EditRules"/> була, тести на неї були — а
            // викликати її ніхто не міг (`A7-51`).
            var sheetOf = group.ToDictionary(i => i.Id, i => SheetOf(snapshot, i.TableDefId));

            // ⚠ Спільні для зрізу умови рахуються ОДИН раз. Поштучний виклик
            // CanEditCellAsync у циклі — антипатерн: на таблиці 500×60 це 30 000
            // запитів, а на права відведено 50 мс на весь запит (ФВ-6.10).
            var sharedBySheet = await SheetContextsAsync(
                    documentId, periodKey, [.. sheetOf.Values.Distinct()], ct)
                .ConfigureAwait(false);

            var projectId = sharedBySheet.Values.First().ProjectId;

            // ⛔ EA-B: передачі читаються З БАЗИ на кожне рішення (не з кешу профілю — профіль не
            // знає документа) і ЛИШЕ для документа, у якого вони є (ознака — з запиту умов доступу).
            var documentHasAssignments = sharedBySheet.Values.First().HasEditAssignments;
            var assignments = documentHasAssignments
                ? await EditAssignmentsAsync(profile, [documentId], ct).ConfigureAwait(false)
                : EmptyAssignments;

            // ✎ D-443: надання учасника — з тих самих фактів, 0 нових звернень; лише для профілю-учасника і без
            // заборони проєкту (заборона перемагає й тут, і в `EditRules.Effective`).
            var grants = profile.HasDocumentParticipations
                         && !profile.DeniesProject(projectId)
                         && assignments.TryGetValue(documentId, out var forGrants)
                ? DocumentParticipation.Active(forGrants.Facts, clock.UtcNow)
                : [];

            // ⛔ Правила доступу до періоду (`ФВ-2.15`) і все, що їм потрібно,
            // читається ОДИН раз на групу — див. `PeriodRuleContextAsync`.
            // Вікна чинності ключовані `TableRow.Id`, а він унікальний, тож
            // один словник на всі екземпляри групи дає кожному рівно його рядки.
            var ruleContext = await PeriodRuleContextAsync(
                    snapshot.TemplateVersionId, projectId, [.. group.Select(i => i.Id)], periodKey, ct)
                .ConfigureAwait(false);

            // ✎ D-442: погоджувач поточного кроку — ОДИН раз на поданий аркуш групи, не на комірку.
            var approverOf = new Dictionary<int, bool>();

            // ⛔ P2-A: стани періодів місячних колонок — ОДИН раз на групу (проєкт + періоди року) і на аркуш (стани
            // аркуша), а не на таблицю чи комірку; лише для таблиць `MonthsInColumns` (решта не платить нічого).
            var monthEnvironmentRead = false;
            MonthEnvironment? monthEnvironment = null;
            var monthStatesBySheet = new Dictionary<int, IReadOnlyDictionary<byte, MonthState>>();

            foreach (var instance in group)
            {
                var sheetDefId = sheetOf[instance.Id];

                var columns = snapshot.ColumnsById.Values
                    .Where(c => c.TableDefId == instance.TableDefId)
                    .ToList();

                // ⚠ D-214: код аркуша — зі знімка, що вже в руках; без нього
                // роль, звужена аркушами, у рішеннях зрізу не діяла б.
                var sheetCode = SheetCodeOf(snapshot, sheetDefId);
                var instanceTableCode = snapshot.Sheets
                    .SelectMany(s => s.Tables)
                    .FirstOrDefault(t => t.Id == instance.TableDefId)?.Code;

                // ✎ D-443: рівень надання — на аркуш (погоджувач поточного кроку, як `CanApproveAsync`) і на таблицю
                // зрізу (рішення комірок).
                var shared = sharedBySheet[sheetDefId] with
                {
                    SheetCode = sheetCode,
                    ParticipatesInDocument = grants.Count > 0,
                    ParticipationLevel = DocumentParticipation.LevelAt(grants, sheetCode, null, periodKey),
                };

                if (approverEdits && shared.SheetStatus == DocumentStatus.Submitted)
                {
                    if (!approverOf.TryGetValue(sheetDefId, out var isApprover))
                    {
                        isApprover = await IsCurrentApproverAsync(profile, documentId, periodKey, shared, ct)
                            .ConfigureAwait(false);
                        approverOf[sheetDefId] = isApprover;
                    }

                    shared = shared with { IsCurrentApprover = isApprover };
                }

                shared = shared with
                {
                    ParticipationLevel = DocumentParticipation.LevelAt(grants, sheetCode, instanceTableCode, periodKey),
                };

                DelegationFacts? delegation = null;
                if (assignments.TryGetValue(documentId, out var owned))
                {
                    var tableCode = instanceTableCode;

                    var narrowed = EditAssignmentGuard.Narrow(
                        owned.Facts,
                        new EditSliceFacts(shared.SheetCode, tableCode, periodKey, false, null));

                    if (narrowed.Count > 0)
                    {
                        delegation = new DelegationFacts(
                            profile.UserId, owned.OwnerUserId, tableCode, narrowed, clock.UtcNow);
                    }
                }

                var monthShared = NoMonthShared;
                (CellAccessContext Context, IReadOnlyList<int> Periods)? annual = null;
                var isMonthsTable = !profile.IsIntegrationWriter
                    && !profile.IsSimulation
                    && snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == instance.TableDefId)
                        is { LayoutKind: TableLayoutKind.MonthsInColumns };
                var hasOtherMonthColumn = isMonthsTable
                    && columns.Any(c => c.IsMonthColumn && c.MonthNumber is { } m && m != periodKey.Sequence);
                var annualRouting = isMonthsTable && monthRouter is { Enabled: true };
                if (hasOtherMonthColumn || annualRouting)
                {
                    if (!monthStatesBySheet.TryGetValue(sheetDefId, out var monthStates))
                    {
                        if (!monthEnvironmentRead)
                        {
                            monthEnvironment = await MonthEnvironmentAsync(documentId, periodKey, ct).ConfigureAwait(false);
                            monthEnvironmentRead = true;
                        }

                        monthStates = monthEnvironment is null
                            ? new Dictionary<byte, MonthState>()
                            : await MonthStatesAsync(monthEnvironment, documentId, sheetDefId, periodKey, ct)
                                .ConfigureAwait(false);
                        monthStatesBySheet[sheetDefId] = monthStates;
                    }

                    // Q-17: річний агрегат — за місяцями, що мають рядок періоду в календарі (разом із місяцем екземпляра).
                    if (annualRouting
                        && monthEnvironment is { } calendar
                        && calendar.States.ContainsKey((byte)periodKey.Sequence))
                    {
                        var periodStates = new List<PeriodState>(12);
                        var sheetStatuses = new List<DocumentStatus>(12);
                        var keys = new List<int>(12);
                        for (byte month = 1; month <= 12; month++)
                        {
                            if (!calendar.States.ContainsKey(month))
                            {
                                continue;
                            }

                            if (month == periodKey.Sequence)
                            {
                                periodStates.Add(shared.PeriodState);
                                sheetStatuses.Add(shared.SheetStatus);
                                keys.Add(periodKey.Value);
                            }
                            else if (monthStates.TryGetValue(month, out var other))
                            {
                                periodStates.Add(other.State);
                                sheetStatuses.Add(other.Status);
                                keys.Add(other.PeriodKey);
                            }
                        }

                        annual = (shared with
                        {
                            PeriodState = AnnualPeriodRule.AggregateState(periodStates),
                            SheetStatus = AnnualPeriodRule.AggregateStatus(sheetStatuses),
                            IsCurrentApprover = false,
                        }, keys);
                    }

                    if (hasOtherMonthColumn && monthStates.Count > 0)
                    {
                        monthShared = monthStates.ToDictionary(
                            s => s.Key,
                            s => (shared with
                            {
                                PeriodState = s.Value.State,
                                SheetStatus = s.Value.Status,
                                IsCurrentApprover = false,
                            }, s.Value.PeriodKey));
                    }
                }

                result[instance.Id] = new SliceContext(
                    periodKey, instance.TableDefId, sheetDefId, shared, columns, snapshot, ruleContext)
                {
                    Delegation = delegation,
                    DocumentHasAssignments = documentHasAssignments,
                    ComputedCells = ComputedCellsOf(snapshot, instance.TableDefId),
                    MonthShared = monthShared,
                    Annual = annual,
                    RoutesOwnership = annualRouting,
                };
            }
        }

        return result;
    }

    /// <summary>Стани періодів року документа (ефективні, на <c>clock.UtcNow</c>) за номером місяця.</summary>
    /// <param name="Year">Рік періоду екземпляра.</param>
    /// <param name="States">Місяць (<c>Period.Sequence</c>) → ефективний стан; місяця без рядка періоду немає.</param>
    private sealed record MonthEnvironment(int Year, IReadOnlyDictionary<byte, PeriodState> States);

    /// <summary>Умови місячної колонки: стан її періоду, стан аркуша за цей період і ключ періоду.</summary>
    private sealed record MonthState(PeriodState State, DocumentStatus Status, int PeriodKey);

    /// <summary>
    /// P2-A: стани періодів року — ОДНИМ читанням проєкту й ОДНИМ — періодів; <c>null</c> — проєкт не місячний
    /// (номер періоду не дорівнює місяцю, відповідності «місяць k → період k» немає: <c>SubmissionPeriodSelector</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Стан для РІШЕННЯ — той самий, що в <see cref="BuildContextAsync"/> (F-08, ФВ-1.8): для активного проєкту
    /// <c>Effective</c> на <c>clock.UtcNow</c> з тим самим вікном року, для чернетки — збережений.
    /// </remarks>
    private async Task<MonthEnvironment?> MonthEnvironmentAsync(long documentId, PeriodKey instancePeriod, CancellationToken ct)
    {
        var project = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new { p.Id, p.Status, p.PeriodKind, p.PeriodEnd, p.YearGraceOffsetDays, p.TimeZoneId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (project is null || project.PeriodKind != PeriodKind.Monthly)
        {
            return null;
        }

        // Q-17: той самий предикат календаря року, що й у маршрутизації місячних стовпців.
        var rows = await Persistence.PeriodCalendar.YearPeriods(db, project.Id, instancePeriod.Year)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var window = Domain.Services.YearGraceWindow.For(
            project.PeriodEnd,
            project.YearGraceOffsetDays,
            SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo());
        var now = clock.UtcNow;

        var states = new Dictionary<byte, PeriodState>(rows.Count);
        foreach (var row in rows)
        {
            states[row.Sequence] = project.Status == ProjectStatus.Active ? PeriodStates.Effective(row, now, window) : row.State;
        }

        return new MonthEnvironment(instancePeriod.Year, states);
    }

    /// <summary>
    /// P2-A: для кожного місяця року, КРІМ місяця періоду екземпляра, — стан його періоду й стан аркуша за цей період
    /// (один запит до <c>wf.ApprovalState</c> на аркуш усього року).
    /// </summary>
    /// <remarks>
    /// ⛔ <c>Scheduled</c> (і періоду без рядка) → <c>Open</c>: внесення наперед (Land T9a) не ламається. Правку
    /// <c>Submitted</c>/<c>Approved</c>/<c>Closed</c> місяця це не відкриває — вони лишаються самі собою.
    /// </remarks>
    private async Task<IReadOnlyDictionary<byte, MonthState>> MonthStatesAsync(
        MonthEnvironment environment, long documentId, int sheetDefId, PeriodKey instancePeriod, CancellationToken ct)
    {
        var first = environment.Year * 100;
        var statuses = await db.ApprovalStates
            .AsNoTracking()
            .Where(a => a.DocumentId == documentId
                        && a.SheetDefId == sheetDefId
                        && a.PeriodKey > first
                        && a.PeriodKey <= first + 12)
            .Select(a => new { a.PeriodKey, a.Status })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var statusByKey = new Dictionary<int, DocumentStatus>();
        foreach (var status in statuses)
        {
            statusByKey.TryAdd(status.PeriodKey, status.Status);
        }

        var result = new Dictionary<byte, MonthState>(12);
        for (byte month = 1; month <= 12; month++)
        {
            if (month == instancePeriod.Sequence)
            {
                continue;
            }

            var key = first + month;
            var state = environment.States.TryGetValue(month, out var found) ? found : PeriodState.Scheduled;
            result[month] = new MonthState(
                state == PeriodState.Scheduled ? PeriodState.Open : state,
                statusByKey.TryGetValue(key, out var sheetStatus) ? sheetStatus : DocumentStatus.Draft,
                key);
        }

        return result;
    }

    /// <summary>Передачі документів для користувача профілю: документ → автор і факти.</summary>
    /// <remarks>
    /// ⚠ Порожньо без звернення до бази для симуляції (запис відхилено раніше) й
    /// системних шляхів (D3). Проєкція — `EditAssignmentFactsReader`, спільна з
    /// повторною перевіркою під блокуванням аркуша.
    /// </remarks>
    private static readonly IReadOnlyDictionary<long, (int OwnerUserId, IReadOnlyList<EditAssignmentFact> Facts)>
        EmptyAssignments = new Dictionary<long, (int, IReadOnlyList<EditAssignmentFact>)>();

    private async Task<IReadOnlyDictionary<long, (int OwnerUserId, IReadOnlyList<EditAssignmentFact> Facts)>>
        EditAssignmentsAsync(AccessProfile profile, IReadOnlyCollection<long> documentIds, CancellationToken ct)
    {
        if (profile.IsSimulation || profile.IsIntegrationWriter || documentIds.Count == 0)
        {
            return new Dictionary<long, (int, IReadOnlyList<EditAssignmentFact>)>();
        }

        var loaded = await Persistence.EditAssignmentFactsReader
            .LoadAsync(db, documentIds, profile.UserId, forUpdate: false, ct, clock.UtcNow)
            .ConfigureAwait(false);

        // ⛔ P2-2: передача діє лише поки делегатор може її видавати (див. WithDelegatorRightsAsync).
        var now = clock.UtcNow;
        var result = new Dictionary<long, (int, IReadOnlyList<EditAssignmentFact>)>(loaded.Count);
        foreach (var (documentId, found) in loaded)
        {
            result[documentId] = (
                found.OwnerUserId,
                await WithDelegatorRightsAsync(documentId, found.OwnerUserId, found.Facts, now, ct).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>
    /// Спільні умови доступу для кількох аркушів одного документа за період.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetDefIds">Аркуші; щонайменше один.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Аркуш → умови, рівні тим, що дав би <see cref="BuildContextAsync"/> з цим аркушем.</returns>
    /// <remarks>
    /// ⛔ Перший аркуш — рівно <see cref="BuildContextAsync"/>, як і доти. Для
    /// решти від нього відрізняються ЛИШЕ два поля — <c>SheetDefId</c> і
    /// <c>SheetStatus</c> (<c>columnDefId: 0</c>, вікно доступу не рахується:
    /// жодне інше поле від аркуша не залежить), тож вони беруться одним
    /// запитом до <c>wf.ApprovalState</c> з тим самим фільтром і тим самим
    /// правилом «немає рядка — <c>Draft</c>».
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, CellAccessContext>> SheetContextsAsync(
        long documentId, PeriodKey periodKey, IReadOnlyList<int> sheetDefIds, CancellationToken ct)
    {
        var first = await BuildContextAsync(
                documentId, periodKey, sheetDefIds[0], columnDefId: 0,
                evaluateAccessWindow: false, ct)
            .ConfigureAwait(false);

        var result = new Dictionary<int, CellAccessContext>(sheetDefIds.Count) { [sheetDefIds[0]] = first };

        if (sheetDefIds.Count == 1)
        {
            return result;
        }

        var others = sheetDefIds.Skip(1).ToList();
        var periodValue = periodKey.Value;

        var states = await db.ApprovalStates
            .AsNoTracking()
            .Where(a => a.DocumentId == documentId
                        && a.PeriodKey == periodValue
                        && others.Contains(a.SheetDefId))
            .Select(a => new { a.SheetDefId, a.Status })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var statuses = new Dictionary<int, DocumentStatus>();
        foreach (var state in states)
        {
            statuses.TryAdd(state.SheetDefId, state.Status);
        }

        foreach (var sheetDefId in others)
        {
            result[sheetDefId] = first with
            {
                SheetDefId = sheetDefId,
                SheetStatus = statuses.TryGetValue(sheetDefId, out var status) ? status : DocumentStatus.Draft,
            };
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
        int templateVersionId, int projectId, IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey,
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
        var references = await SourceWindowQuery(db, tableInstanceIds, periodKey, sourceColumns)
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

    /// <summary>
    /// Посилання рядків зрізу на записи довідника разом із вікнами чинності.
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="tableInstanceIds">
    /// Екземпляри таблиць одного періоду (P8: один запит на всі зрізи групи,
    /// а не на кожен).
    /// </param>
    /// <param name="periodKey">Період екземплярів — він же ключ партиції.</param>
    /// <param name="sourceColumns">Колонки, на які дивляться правила <c>SourceWindow</c>.</param>
    /// <returns>Незавершений запит.</returns>
    /// <remarks>
    /// ⛔ <c>PeriodKey</c> стоїть на ОБОХ партиціонованих таблицях, а не лише на
    /// <c>doc.CellValue</c>. Рівність по ключу партиції в умові з'єднання
    /// (<c>cell.PeriodKey = row.PeriodKey</c>) оптимізатор здебільшого
    /// розкриває транзитивно, але «здебільшого» — не критерій для гарячого
    /// шляху: досить одного плану, де він цього не зробив, і
    /// <c>doc.TableRow</c> читається всіма партиціями. Явний предикат коштує
    /// нуль і знімає питання.
    ///
    /// ⚠ <b>public static</b> — з тієї ж причини, що й
    /// <see cref="SliceRowsQuery"/>: сторож <c>WR-05</c> перевіряє
    /// <c>ToQueryString()</c> саме бойового запиту.
    /// </remarks>
    public static IQueryable<SourceWindowRow> SourceWindowQuery(
        EcrDbContext db, IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey,
        IReadOnlyCollection<int> sourceColumns)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tableInstanceIds);
        ArgumentNullException.ThrowIfNull(sourceColumns);

        return from cell in db.CellValues.AsNoTracking()
               join row in db.TableRows.AsNoTracking()
                   on new { cell.PeriodKeyValue, Id = cell.TableRowId }
                   equals new { row.PeriodKeyValue, row.Id }
               join entry in db.RegistryEntries.AsNoTracking()
                   on cell.ValueRegistryEntryId!.Value equals entry.Id
               where tableInstanceIds.Contains(row.TableInstanceId)
                     && cell.PeriodKeyValue == periodKey.Value
                     && row.PeriodKeyValue == periodKey.Value
                     && !row.IsDeleted
                     && cell.ValueRegistryEntryId != null
                     && sourceColumns.Contains(cell.ColumnDefId)
               select new SourceWindowRow(
                   cell.TableRowId, cell.ColumnDefId, entry.ValidFrom, entry.ValidTo);
    }

    /// <summary>
    /// D-442: чи користувач — погоджувач ПОТОЧНОГО кроку маршруту поданого аркуша (те саме рішення,
    /// що <see cref="CanApproveAsync"/>: видимість, роль кроку, грант <see cref="GrantLevel.Approve"/>).
    /// </summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="shared">Спільні умови аркуша (код аркуша заповнено).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Одне правило «чия черга» на затвердження, чергу «До погодження» і правку поданого:
    /// <see cref="EditRules.CanApprove"/> з роллю кроку з <see cref="CurrentApprovalStepAsync"/>.
    /// Друга копія розійшлася б із першою, і правив би не той, хто погоджує.
    ///
    /// ⚠ Маршрут читається ЛИШЕ коли грант на аркуш сягає <see cref="GrantLevel.Approve"/> (чиста перевірка
    /// без бази): автор із <c>Write</c>/<c>Submit</c> на поданому аркуші не платить жодним запитом.
    /// </remarks>
    private async Task<bool> IsCurrentApproverAsync(
        AccessProfile profile, long documentId, PeriodKey periodKey, CellAccessContext shared, CancellationToken ct)
    {
        if (profile.IsSimulation || profile.IsIntegrationWriter)
        {
            return false;
        }

        var sheetContext = shared with { TableDefId = 0, ColumnDefId = 0 };
        if (DenyIfInvisible(profile, sheetContext) is not null
            || EditRules.Effective(profile, sheetContext) < GrantLevel.Approve)
        {
            return false;
        }

        var step = await CurrentApprovalStepAsync(documentId, sheetContext.SheetDefId, periodKey, ct)
            .ConfigureAwait(false);

        return EditRules.CanApprove(profile, sheetContext, step?.RoleId).IsAllowed;
    }

    /// <inheritdoc />
    public async Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var context = await BuildContextAsync(
                documentId, periodKey, sheetDefId, columnDefId: 0, evaluateAccessWindow: true, ct)
            .ConfigureAwait(false);
        context = await WithSheetCodeAsync(profile, documentId, context, ct).ConfigureAwait(false);
        context = await WithParticipationAsync(profile, documentId, context, ct).ConfigureAwait(false);

        if (DenyIfInvisible(profile, context) is { } invisible)
        {
            return invisible;
        }

        // ⚠ При поданні блокує БУДЬ-ЯКА помилка валідації будь-якого рівня
        // (ФВ-5.19), на відміну від запису, де блокує лише коміркова (D-90):
        // подана форма йде назовні цілком, і рядкова помилка в ній — це
        // неправильний звіт.
        //
        // ⛔ T2-01: тут НЕ читається збережений підсумок (`ErrorCount` останнього
        // «Перевірити»). Він застарілий одразу після правки даних: відмова `403`
        // «domain rule» за ним лишалась, хоч помилку вже виправлено (допомагав
        // лише ручний Validate). Блокування по помилках валідації робить сам
        // `SubmitSheetHandler` — СВІЖИМ прогоном під блокуванням аркуша
        // (422 ECR-SUB-4221 зі списком), тож рішення про права його не дублює.
        return EditRules.CanSubmit(profile, context, hasBlockingErrors: false);
    }

    /// <inheritdoc />
    public async Task<EditDecision> CanApproveAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var context = await BuildContextAsync(
                documentId, periodKey, sheetDefId, columnDefId: 0, evaluateAccessWindow: true, ct)
            .ConfigureAwait(false);
        context = await WithSheetCodeAsync(profile, documentId, context, ct).ConfigureAwait(false);
        context = await WithParticipationAsync(profile, documentId, context, ct).ConfigureAwait(false);

        if (DenyIfInvisible(profile, context) is { } invisible)
        {
            return invisible;
        }

        // ⚠ Маршрут читається ЛИШЕ при затвердженні, а не в кожному рішенні
        // про доступ: затверджують рідко, а комірки читають тисячами.
        var step = await CurrentApprovalStepAsync(documentId, sheetDefId, periodKey, ct)
            .ConfigureAwait(false);

        return EditRules.CanApprove(profile, context, step?.RoleId);
    }

    /// <inheritdoc />
    public async Task<EditDecision> CanReopenAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var context = await BuildContextAsync(
                documentId, periodKey, sheetDefId, columnDefId: 0, evaluateAccessWindow: true, ct)
            .ConfigureAwait(false);
        context = await WithSheetCodeAsync(profile, documentId, context, ct).ConfigureAwait(false);
        context = await WithParticipationAsync(profile, documentId, context, ct).ConfigureAwait(false);

        if (DenyIfInvisible(profile, context) is { } invisible)
        {
            return invisible;
        }

        // ⛔ P2-4: роль кроку маршруту, як при затвердженні. Submitted — поточний крок; Approved — останній крок
        // (CurrentStepId після затвердження порожній). Маршруту немає — перевірки немає. Маршрут читається лише коли
        // стан дозволяє повернення й грант сягає Approve (чиста перевірка без бази) — решта не платить запитом.
        int? requiredRoleId = null;
        if (context.SheetStatus is DocumentStatus.Submitted or DocumentStatus.Approved
            && EditRules.Effective(profile, context with { TableDefId = 0, ColumnDefId = 0 }) >= GrantLevel.Approve)
        {
            requiredRoleId = context.SheetStatus == DocumentStatus.Submitted
                ? (await CurrentApprovalStepAsync(documentId, sheetDefId, periodKey, ct).ConfigureAwait(false))?.RoleId
                : await LastApprovalStepRoleAsync(documentId, ct).ConfigureAwait(false);
        }

        return EditRules.CanReopen(profile, context, requiredRoleId);
    }

    /// <summary>Роль ОСТАННЬОГО кроку маршруту документа; <c>null</c> — маршруту немає.</summary>
    private async Task<int?> LastApprovalStepRoleAsync(long documentId, CancellationToken ct)
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

        var route = await workflow.FindRouteAsync(scope.Id, scope.TemplateVersionId, ct).ConfigureAwait(false);
        return route is null || route.Steps.Count == 0
            ? null
            : route.Steps.OrderBy(s => s.Ordinal).Last().RoleId;
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
        // ⚠ `WR-04` п. 5: документ, проєкт, період і стан аркуша — ОДНИМ
        // запитом. Доти це були чотири окремі звернення з тим самим ключем
        // (`Documents` → `Projects` → `Periods` → `ApprovalStates`), і кожне
        // рішення про доступ на шляху запису платило за всі чотири.
        //
        // ⚠ Стан аркуша читається завжди, але з `sheet = 0`, коли аркуша не
        // названо: рядка з `SheetDefId = 0` не буває, тож підзапит дає `NULL`,
        // а нижче значення однаково відкидається — поведінка та сама, що й
        // доти, коли запит не виконувався зовсім.
        var periodValue = periodKey.Value;
        var sheetForStatus = sheetDefId ?? 0;

        var state = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new
                {
                    d.ProjectId,
                    p.Status,
                    p.IsArchiving,
                    p.TemplateVersionId,

                    // ⚠ ФВ-1.8: річне вікно — з того самого рядка проєкту, без
                    // окремого звернення (храповик `PatchCellsQueryCountTests`).
                    p.PeriodEnd,
                    p.YearGraceOffsetDays,
                    p.TimeZoneId,

                    // ⛔ EA-B: ознака «у документа є передачі» — EXISTS у тому самому запиті,
                    // 0 нових звернень; документ без передач не читає їх ні в рішенні, ні під гейтом.
                    HasAssignments = db.EditAssignments.Any(x => x.DocumentId == documentId),
                    Period = db.Periods
                        .AsNoTracking()
                        .Where(x => x.ProjectId == d.ProjectId && x.PeriodKeyValue == periodValue)
                        .FirstOrDefault(),
                    SheetStatus = db.ApprovalStates
                        .AsNoTracking()
                        .Where(a => a.DocumentId == documentId
                                    && a.SheetDefId == sheetForStatus
                                    && a.PeriodKey == periodValue)
                        .Select(a => (DocumentStatus?)a.Status)
                        .FirstOrDefault(),
                })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-DOC-0404",
                $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        var period = state.Period;

        // ⛔ F-08: стан для РІШЕННЯ — на `clock.UtcNow`, а не збережений.
        // Збережений змінює лише годинна задача, і перевідкритий період після
        // `ReopenedUntil` ще до години приймав запис. `Closed` не
        // повертається назад ніколи (`PeriodStateCalculator.Effective`).
        //
        // ⚠ Лише для АКТИВНОГО проєкту — як і в самій задачі: періоди чернетки
        // не відкриваються за датами, доки проєкт не активовано (`A7-25`), і
        // розрахунок тут відкрив би їх повз активацію.
        //
        // ⚠ ФВ-1.8: вікно — те саме, що передає `PeriodStateJob`; інакше задача
        // тримала б грудень у `Grace`, а рішення про запис сказало б `Closed`.
        var periodState = period is null
            ? PeriodState.Scheduled
            : state.Status == ProjectStatus.Active
                ? PeriodStates.Effective(
                    period,
                    clock.UtcNow,
                    Domain.Services.YearGraceWindow.For(
                        state.PeriodEnd,
                        state.YearGraceOffsetDays,
                        SiteTimeZone.Create(state.TimeZoneId).ToTimeZoneInfo()))
                : period.State;

        var sheetStatus = sheetDefId is null ? null : state.SheetStatus;

        // ⚠ Метадані читаються ЛИШЕ коли рішення справді про комірку.
        // Подання і затвердження до колонок не звертаються, і зайвий похід у
        // кеш шаблону тут не просто марний — він робить рішення про доступ
        // залежним від того, чи завантажується структура, якої це рішення не
        // стосується.
        ColumnDef? column = null;
        var tableDefId = 0;
        var effectiveSheet = sheetDefId ?? 0;
        string? sheetCode = null;

        if (columnDefId != 0)
        {
            var snapshot = await metadata.GetAsync(state.TemplateVersionId, ct).ConfigureAwait(false);
            column = snapshot.ColumnsById.TryGetValue(columnDefId, out var found) ? found : null;
            tableDefId = column?.TableDefId ?? 0;
            effectiveSheet = sheetDefId ?? SheetOf(snapshot, tableDefId);
            sheetCode = SheetCodeOf(snapshot, effectiveSheet);
        }

        var outOfWindow = evaluateAccessWindow
                          && period is not null
                          && await OutOfWindowAsync(
                              state.TemplateVersionId, effectiveSheet, tableDefId, period.Sequence, ct)
                              .ConfigureAwait(false);

        return new CellAccessContext(
            state.ProjectId,
            effectiveSheet,
            tableDefId,
            columnDefId,

            // Відсутній період трактується як Scheduled: «періоду ще немає» і
            // «період не відкрито» для користувача — та сама відмова.
            state.Status,
            state.IsArchiving,
            periodState,
            outOfWindow,
            sheetStatus ?? DocumentStatus.Draft,
            column?.IsComputed ?? false,
            column?.IsReadOnly ?? false,
            RowIsReadOnly: false)
        {
            // ⚠ D-214: період рішення відомий завжди; код аркуша — там, де знімок
            // уже прочитано (рішення про комірку). Подання й затвердження
            // добирають його самі (`WithSheetCodeAsync`) і лише тоді, коли він
            // комусь потрібен.
            SheetCode = sheetCode,
            Period = periodKey,
            HasEditAssignments = state.HasAssignments,
        };
    }

    /// <summary>Код аркуша зі знімка; <c>null</c> — аркуша в знімку немає.</summary>
    private static string? SheetCodeOf(TemplateVersionSnapshot snapshot, int sheetDefId)
        => snapshot.Sheets.FirstOrDefault(s => s.Id == sheetDefId)?.Code;

    /// <summary>
    /// Додає до умов код аркуша — лише коли в проєкті є роль, звужена аркушами (D-214).
    /// </summary>
    /// <remarks>
    /// ⚠ Знімок шаблону на шляху подання й затвердження не читається навмисно
    /// (див. <see cref="BuildContextAsync"/>); тут — лише для профілю, якому
    /// код справді змінює рішення. Решта профілів не платить нічого.
    /// </remarks>
    private async Task<CellAccessContext> WithSheetCodeAsync(
        AccessProfile profile, long documentId, CellAccessContext context, CancellationToken ct)
    {
        if (context.SheetCode is not null
            || !profile.Scoped.TryGetValue(context.ProjectId, out var scoped)
            || !scoped.Narrowed.Any(l => l.SheetCodes is not null))
        {
            return context;
        }

        var snapshot = await SnapshotAsync(documentId, ct).ConfigureAwait(false);
        return context with { SheetCode = SheetCodeOf(snapshot, context.SheetDefId) };
    }

    /// <summary>
    /// D-443: додає до умов аркуша надання учасника документа — рівень на аркуш (надання на документ чи на цей аркуш;
    /// надання на таблицю подавати/погоджувати аркуш не дає) і ознаку видимості.
    /// </summary>
    /// <remarks>
    /// ⚠ Лише для профілю-учасника, документа з передачами (ознака з того самого запиту умов) і без заборони
    /// проєкту; решта не платить нічого. Знімок шаблону — лише заради коду аркуша, якщо його ще немає.
    ///
    /// ⛔ D-443 debt: усередині транзакції дії (подання, затвердження, повернення в роботу, правка погоджувача —
    /// усі приймають рішення ще раз ПІСЛЯ блокування аркуша) надання читаються під спільними
    /// <c>REPEATABLEREAD, ROWLOCK</c>, як факти гейта запису (<c>IEditAssignmentStore.ReadFactsForGateAsync</c>).
    /// Доти читання йшло знімком RCSI: відкликання чи зниження рівня, зафіксоване ПІСЛЯ рішення під блокуванням і
    /// ДО коміту дії, лишалось непоміченим — подання/підпис фіксувались за наданням, якого вже не було. Тепер
    /// зафіксоване раніше видно (блокуюче читання не бере знімок), а зняття, що приходить пізніше, чекає коміту дії.
    /// Поза транзакцією (швидка відмова до блокування, читання) — знімок, як і доти: там нічого не пишеться.
    /// </remarks>
    private async Task<CellAccessContext> WithParticipationAsync(
        AccessProfile profile, long documentId, CellAccessContext context, CancellationToken ct)
    {
        if (!profile.HasDocumentParticipations
            || profile.IsIntegrationWriter
            || !context.HasEditAssignments
            || profile.DeniesProject(context.ProjectId))
        {
            return context;
        }

        var locked = db.Database.CurrentTransaction is not null;
        var grants = (await ParticipationGrantsAsync(profile, [documentId], ct, locked).ConfigureAwait(false))
            .TryGetValue(documentId, out var mine) ? mine : [];
        if (grants.Count == 0)
        {
            return context;
        }

        var sheetCode = context.SheetCode
                        ?? SheetCodeOf(await SnapshotAsync(documentId, ct).ConfigureAwait(false), context.SheetDefId);

        return context with
        {
            SheetCode = sheetCode,
            ParticipatesInDocument = true,
            ParticipationLevel = DocumentParticipation.LevelAt(grants, sheetCode, null, context.Period),
        };
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
    /// <inheritdoc />
    public Task<int?> DocumentProjectIdAsync(long documentId, CancellationToken ct)
        => db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => (int?)d.ProjectId)
            .FirstOrDefaultAsync(ct);

    private async Task<int> ProjectIdAsync(long documentId, CancellationToken ct)
        => await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => (int?)d.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
           ?? throw new NotFoundException(
               "ECR-DOC-0404",
               $"Документ {documentId} не знайдено.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-DOC-0404.document",
                   ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
               });

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

/// <summary>Посилання однієї комірки на запис довідника разом із вікном чинності.</summary>
/// <param name="TableRowId">Рядок, який обрав запис.</param>
/// <param name="ColumnDefId">Колонка, у якій стоїть посилання.</param>
/// <param name="ValidFrom">Початок чинності запису; <c>null</c> — необмежений.</param>
/// <param name="ValidTo">Кінець чинності запису; <c>null</c> — необмежений.</param>
/// <remarks>
/// ⚠ Іменований тип замість анонімного потрібен рівно тому, що запит став
/// <c>public static</c> (див. <see cref="AccessDecisionService.SourceWindowQuery"/>):
/// анонімний тип не можна повернути з <c>IQueryable&lt;T&gt;</c>.
/// </remarks>
public sealed record SourceWindowRow(
    long TableRowId, int ColumnDefId, DateOnly? ValidFrom, DateOnly? ValidTo);
