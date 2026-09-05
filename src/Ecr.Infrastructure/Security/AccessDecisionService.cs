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
    Application.Common.ICurrentUser currentUser) : IAccessDecisionService
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

    /// <summary>Збирає профіль із бази. Викликається лише при промаху кешу.</summary>
    private async Task<AccessProfile> LoadAsync(int userId, string securityStamp, CancellationToken ct)
    {
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

        var roleIds = await db.RoleAssignments
            .AsNoTracking()
            .Where(a => (a.UserId == userId || (a.PrincipalSid != null && groupSids.Contains(a.PrincipalSid)))
                        && (a.ValidFrom == null || a.ValidFrom <= today)
                        && (a.ValidTo == null || a.ValidTo >= today))
            .Select(a => a.RoleId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
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
            documentId, address.PeriodKey, sheetDefId: null, address.ColumnDefId, ct).ConfigureAwait(false);

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

        // ⚠ Спільні для зрізу умови рахуються ОДИН раз. Поштучний виклик
        // CanEditCellAsync у циклі — антипатерн: на таблиці 500×60 це 30 000
        // запитів, а на права відведено 50 мс на весь запит (ФВ-6.10).
        var shared = await BuildContextAsync(
            instance.DocumentId, periodKey, sheetDefId: null, columnDefId: 0, ct).ConfigureAwait(false);

        var snapshot = await SnapshotAsync(instance.DocumentId, ct).ConfigureAwait(false);

        var rows = await db.TableRows
            .AsNoTracking()
            .Where(r => r.TableInstanceId == tableInstanceId && !r.IsDeleted)
            .Select(r => new { r.Id, r.RowKey })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var columns = snapshot.ColumnsById.Values
            .Where(c => c.TableDefId == instance.TableDefId)
            .ToList();

        var result = new Dictionary<CellAddress, EditDecision>(rows.Count * columns.Count);

        foreach (var row in rows)
        {
            var rowReadOnly = snapshot.RowsByKey.TryGetValue((instance.TableDefId, row.RowKey), out var def)
                              && def.IsReadOnly;

            foreach (var column in columns)
            {
                var context = shared with
                {
                    TableDefId = instance.TableDefId,
                    ColumnDefId = column.Id,
                    ColumnIsComputed = column.IsComputed,
                    ColumnIsReadOnly = column.IsReadOnly,
                    RowIsReadOnly = rowReadOnly,
                };

                result[new CellAddress(periodKey, row.Id, column.Id)] = EditRules.CanEdit(profile, context);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var context = await BuildContextAsync(documentId, periodKey, sheetDefId, columnDefId: 0, ct)
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

        var context = await BuildContextAsync(documentId, periodKey, sheetDefId, columnDefId: 0, ct)
            .ConfigureAwait(false);

        return EditRules.CanApprove(profile, context);
    }

    /// <summary>Збирає умови доступу з бази в один <see cref="CellAccessContext"/>.</summary>
    /// <remarks>
    /// ⛔ <c>Project.CurrentPeriod</c> у цей ланцюг НЕ входить: інакше «пін»
    /// поточного періоду став би прихованим правом редагувати закрите (D-77).
    /// </remarks>
    private async Task<CellAccessContext> BuildContextAsync(
        long documentId, PeriodKey periodKey, int? sheetDefId, int columnDefId, CancellationToken ct)
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

        var outOfWindow = period is not null
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
    private async Task<bool> OutOfWindowAsync(
        int templateVersionId, int sheetDefId, int tableDefId, byte sequence, CancellationToken ct)
    {
        var rules = await db.PeriodAccessRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId
                        && (r.SheetDefId == null || r.SheetDefId == sheetDefId)
                        && (r.TableDefId == null || r.TableDefId == tableDefId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

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
