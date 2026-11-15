using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Periods;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.RowLocks;

/// <summary>✎ B75: блокування рядка у відповіді (дати — у часовому поясі проєкту, кінцева — включно).</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="LockFrom">Перший день дії.</param>
/// <param name="LockUntil">Останній день дії (включно).</param>
/// <param name="OwnerUserId">Хто заблокував.</param>
/// <param name="OwnerDisplayName">Ім'я того, хто заблокував; <c>null</c> — невідоме.</param>
/// <param name="Note">Примітка.</param>
/// <param name="IsActive">Діє зараз (почалося й не скінчилося).</param>
/// <param name="CanUnlock">Поточний користувач може зняти (власник блокування або право <c>Document.RowLock</c>).</param>
public sealed record RowLockDto(
    long Id,
    string TableCode,
    long? SheetCopyId,
    string RowKey,
    DateOnly LockFrom,
    DateOnly LockUntil,
    int OwnerUserId,
    string? OwnerDisplayName,
    string? Note,
    bool IsActive,
    bool CanUnlock);

/// <summary>✎ B75: запит на блокування рядків (усе або нічого).</summary>
/// <param name="PeriodKey">Період екземпляра таблиці.</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="RowKeys">Ключі рядків (1..<see cref="RowLockRules.MaxRowsPerRequest"/>).</param>
/// <param name="FromDate">Перший день (часовий пояс проєкту).</param>
/// <param name="ToDate">Останній день, включно.</param>
/// <param name="Note">Примітка; необов'язкова.</param>
public sealed record CreateRowLocksRequest(
    int PeriodKey,
    long? SheetCopyId,
    string TableCode,
    IReadOnlyList<string> RowKeys,
    DateOnly FromDate,
    DateOnly ToDate,
    string? Note);

/// <summary>✎ B75: спільні правила блокувань рядків (право, відмови, дати).</summary>
/// <remarks>
/// ⛔ Блокує (a) власник документа з рівнем <see cref="GrantLevel.Write"/> на таблицю або (b) носій права
/// <see cref="Permission"/> у проєкті з видимим документом (Q6: читання достатньо). Знімає власник блокування або носій
/// права (щоб нічого не лишалося заблокованим, якщо власника немає). Архів, архівація й закритий період відмовляють
/// обом діям.
/// </remarks>
public static class RowLockRules
{
    /// <summary>Проєктне право блокувати й знімати блокування рядків будь-кого.</summary>
    public const string Permission = "Document.RowLock";

    /// <summary>Тип сутності в <c>aud.StructureChange</c>.</summary>
    public const string AuditEntityType = "RowLock";

    /// <summary>Стеля рядків в одному запиті.</summary>
    public const int MaxRowsPerRequest = 500;

    /// <summary>Причина, з якої блокування/зняття в цьому документі за період недоступні; <c>None</c> — доступні.</summary>
    /// <param name="target">Факти документа.</param>
    public static EditDenyReason StateDenial(RowLockTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.ProjectStatus == ProjectStatus.Archived)
        {
            return EditDenyReason.ProjectArchived;
        }

        if (target.IsArchiving)
        {
            return EditDenyReason.ArchivingInProgress;
        }

        return target.PeriodState == PeriodState.Closed ? EditDenyReason.PeriodClosed : EditDenyReason.None;
    }

    /// <summary>Чи може профіль знімати будь-яке блокування документа (право в проєкті).</summary>
    /// <param name="profile">Профіль.</param>
    /// <param name="projectId">Проєкт документа.</param>
    public static bool HoldsRight(AccessProfile profile, int projectId)
        => !profile.IsSimulation && PermissionCheck.IsGrantedIn(profile, Permission, projectId);

    /// <summary>Проміжок дат (часовий пояс проєкту, кінець включно) → <c>[from, until)</c> в UTC.</summary>
    /// <param name="fromDate">Перший день.</param>
    /// <param name="toDate">Останній день.</param>
    /// <param name="timeZoneId">Часовий пояс проєкту.</param>
    public static (DateTime From, DateTime Until) ToUtcRange(DateOnly fromDate, DateOnly toDate, string timeZoneId)
    {
        var zone = SiteTimeZone.Create(timeZoneId).ToTimeZoneInfo();
        return (Midnight(fromDate, zone), Midnight(toDate.AddDays(1), zone));
    }

    /// <summary>UTC-проміжок → дати проєкту (кінець включно).</summary>
    /// <param name="from">Початок (UTC).</param>
    /// <param name="until">Кінець (UTC, виключно).</param>
    /// <param name="timeZoneId">Часовий пояс проєкту.</param>
    public static (DateOnly From, DateOnly Until) ToLocalDates(DateTime from, DateTime until, string timeZoneId)
    {
        var zone = SiteTimeZone.Create(timeZoneId).ToTimeZoneInfo();
        var localFrom = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(from, DateTimeKind.Utc), zone);
        var localUntil = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(until, DateTimeKind.Utc), zone);
        return (DateOnly.FromDateTime(localFrom), DateOnly.FromDateTime(localUntil.AddTicks(-1)));
    }

    private static DateTime Midnight(DateOnly date, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);

    /// <summary>403 на блокування/зняття з причиною.</summary>
    /// <param name="reason">Причина.</param>
    /// <param name="action">Дія для тексту.</param>
    public static AccessDeniedException Denied(EditDenyReason reason, string action)
        => new(
            ErrorCodes.AccessDenied,
            $"{action} рядків відхилено: {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.rowLockDenied",
                ["reason"] = reason.ToString(),
                ["reasonKey"] = $"deny.{reason}",
            });

    /// <summary>422 «проміжок дат порожній або вже минув».</summary>
    public static BusinessRuleException RangeInvalid()
        => new(
            ErrorCodes.RequestInvalid,
            "Проміжок блокування порожній або вже минув.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.lockRangeInvalid" });

    /// <summary>Таблиця шаблону за кодом; <c>null</c> — немає.</summary>
    /// <param name="snapshot">Знімок версії.</param>
    /// <param name="tableCode">Код таблиці.</param>
    public static (SheetDef Sheet, TableDef Table)? FindTable(TemplateVersionSnapshot snapshot, string tableCode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var sheet in snapshot.Sheets)
        {
            foreach (var table in sheet.Tables)
            {
                if (!table.IsDeleted && string.Equals(table.Code, tableCode, StringComparison.Ordinal))
                {
                    return (sheet, table);
                }
            }
        }

        return null;
    }

    /// <summary>Чи бачить профіль таблицю (межа читання аркуша/таблиці, D-214).</summary>
    internal static bool CanReadTable(
        AccessProfile profile, int projectId, SheetDef sheet, TableDef table, int periodKey)
        => EditRules.CanReadIn(profile, projectId, sheet.Id, sheet.Code, table.Id, 0, new PeriodKey(periodKey));
}

/// <summary>✎ B75: <c>GET /documents/{id}/row-locks?periodKey=</c> — незняті й не прострочені блокування.</summary>
public sealed class GetRowLocksHandler(
    IRowLockStore store,
    IMetadataCache metadata,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Перелік блокувань документа за період.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<RowLockDto>> HandleAsync(long documentId, int periodKey, CancellationToken ct)
    {
        var key = PeriodKey.Parse(periodKey);
        var profile = await OpenDocumentPeriodReopenHandler.ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        var target = await store.GetTargetAsync(documentId, key.Value, now, ct).ConfigureAwait(false)
                     ?? throw DocumentVisibility.NotFound(documentId);

        var records = await store.ListAsync(documentId, key.Value, now, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return [];
        }

        var snapshot = await metadata.GetAsync(target.TemplateVersionId, ct).ConfigureAwait(false);
        var unlockAllowed = RowLockRules.StateDenial(target) == EditDenyReason.None;
        var holdsRight = RowLockRules.HoldsRight(profile, target.ProjectId);
        var readable = new Dictionary<string, bool>(StringComparer.Ordinal);
        var result = new List<RowLockDto>(records.Count);

        foreach (var record in records)
        {
            // ⛔ Ключі рядків таблиці, якої читач не бачить, не розкриваються (межа читання, S6).
            if (!readable.TryGetValue(record.TableCode, out var canRead))
            {
                canRead = RowLockRules.FindTable(snapshot, record.TableCode) is { } found
                          && RowLockRules.CanReadTable(profile, target.ProjectId, found.Sheet, found.Table, key.Value);
                readable[record.TableCode] = canRead;
            }

            if (!canRead)
            {
                continue;
            }

            var (from, until) = RowLockRules.ToLocalDates(record.LockFrom, record.LockUntil, target.TimeZoneId);
            result.Add(new RowLockDto(
                record.Id, record.TableCode, record.SheetCopyId, record.RowKey, from, until, record.OwnerUserId,
                record.OwnerDisplayName, record.Note,
                IsActive: record.LockFrom <= now && now < record.LockUntil,
                CanUnlock: unlockAllowed && !profile.IsSimulation
                           && (record.OwnerUserId == profile.UserId || holdsRight)));
        }

        return result;
    }
}

/// <summary>
/// ✎ B75: <c>POST /documents/{id}/row-locks</c> — блокує рядки таблиці (усе або нічого; вже заблокований рядок — 409).
/// </summary>
public sealed class CreateRowLocksHandler(
    IRowLockStore store,
    IDocumentStore documents,
    IMetadataCache metadata,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Створює блокування.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Створені блокування.</returns>
    public async Task<IReadOnlyList<RowLockDto>> HandleAsync(
        long documentId, CreateRowLocksRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = PeriodKey.Parse(request.PeriodKey);
        var profile = await OpenDocumentPeriodReopenHandler.ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var rowKeys = (request.RowKeys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (rowKeys.Count == 0 || rowKeys.Count > RowLockRules.MaxRowsPerRequest
            || rowKeys.Any(k => k.Length > RowLock.MaxRowKeyLength)
            || string.IsNullOrWhiteSpace(request.TableCode))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Потрібно від 1 до {RowLockRules.MaxRowsPerRequest} ключів рядків і код таблиці.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.lockRowsInvalid",
                    ["max"] = RowLockRules.MaxRowsPerRequest,
                });
        }

        var now = clock.UtcNow;
        var target = await store.GetTargetAsync(documentId, key.Value, now, ct).ConfigureAwait(false)
                     ?? throw DocumentVisibility.NotFound(documentId);

        var snapshot = await metadata.GetAsync(target.TemplateVersionId, ct).ConfigureAwait(false);
        if (RowLockRules.FindTable(snapshot, request.TableCode) is not { } found
            || !RowLockRules.CanReadTable(profile, target.ProjectId, found.Sheet, found.Table, key.Value)
            || (request.SheetCopyId is { } copy
                && !await documents.HasSheetCopyAsync(documentId, found.Sheet.Id, copy, ct).ConfigureAwait(false)))
        {
            throw new NotFoundException(
                ErrorCodes.DocumentNotFound,
                $"Таблиці немає в документі {documentId} або вона невидима.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.tableNotInDocument",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });
        }

        RequireMayLock(profile, target, found, key.Value);

        var (from, until) = RowLockRules.ToUtcRange(request.FromDate, request.ToDate, target.TimeZoneId);
        if (until <= from || until <= now)
        {
            throw RowLockRules.RangeInvalid();
        }

        var existing = await store.ExistingRowKeysAsync(
                documentId, key.Value, request.SheetCopyId, found.Table.Id, rowKeys, ct)
            .ConfigureAwait(false);
        var missing = rowKeys.Where(k => !existing.Contains(k)).ToList();
        if (missing.Count > 0)
        {
            throw new NotFoundException(
                ErrorCodes.RowNotFound,
                $"Рядків {string.Join(", ", missing.Take(10))} немає в таблиці.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-ROW-0404.lockRowsMissing",
                    ["rowKeys"] = string.Join(", ", missing.Take(10)),
                });
        }

        var created = new List<RowLock>(rowKeys.Count);
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            created.Clear();

            // ⛔ Перевірка перетину й вставка — під UPDLOCK рядка документа: два паралельні блокування того самого рядка
            // не проходять обидва.
            await store.LockDocumentAsync(documentId, innerCt).ConfigureAwait(false);

            // Стан проєкту/періоду — перечитується під замком (архівація могла зафіксуватися між перевіркою й замком).
            var locked = await store.GetTargetAsync(documentId, key.Value, now, innerCt).ConfigureAwait(false)
                         ?? throw DocumentVisibility.NotFound(documentId);
            RequireMayLock(profile, locked, found, key.Value);

            var overlapping = await store.OverlappingAsync(
                    documentId, key.Value, request.SheetCopyId, request.TableCode, rowKeys, from, until, innerCt)
                .ConfigureAwait(false);
            if (overlapping.Count > 0)
            {
                throw new ConcurrencyConflictException(
                    ErrorCodes.RowDuplicate,
                    $"Рядки вже заблоковано на цей час: {string.Join(", ", overlapping.Take(10))}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-ROW-0409.rowAlreadyLocked",
                        ["rowKeys"] = string.Join(", ", overlapping.Take(10)),
                    });
            }

            created.AddRange(rowKeys.Select(k => RowLock.Create(
                documentId, key.Value, request.SheetCopyId, request.TableCode, k, from, until, profile.UserId,
                request.Note, now)));
            store.AddRange(created);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            foreach (var item in created)
            {
                await audit.WriteStructureChangeAsync(
                    RowLockAudit.Record(item, locked, "Lock", now, profile.UserId, currentUser.CorrelationId),
                    innerCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        return [.. created.Select(item =>
        {
            var (localFrom, localUntil) = RowLockRules.ToLocalDates(item.LockFrom, item.LockUntil, target.TimeZoneId);
            return new RowLockDto(
                item.Id, item.TableCode, item.SheetCopyId, item.RowKey, localFrom, localUntil, item.OwnerUserId,
                OwnerDisplayName: null, item.Note, IsActive: item.IsActiveAt(now), CanUnlock: true);
        })];
    }

    /// <summary>Право блокувати: власник документа з Write на таблицю або носій <c>Document.RowLock</c> (Q6).</summary>
    private static void RequireMayLock(
        AccessProfile profile, RowLockTarget target, (SheetDef Sheet, TableDef Table) found, int periodKey)
    {
        if (profile.IsSimulation)
        {
            throw RowLockRules.Denied(EditDenyReason.SimulationReadOnly, "Блокування");
        }

        var state = RowLockRules.StateDenial(target);
        if (state != EditDenyReason.None)
        {
            throw RowLockRules.Denied(state, "Блокування");
        }

        if (RowLockRules.HoldsRight(profile, target.ProjectId))
        {
            return;
        }

        var level = EditRules.Effective(profile, default(CellAccessContext) with
        {
            ProjectId = target.ProjectId,
            SheetDefId = found.Sheet.Id,
            TableDefId = found.Table.Id,
            SheetCode = found.Sheet.Code,
            Period = new PeriodKey(periodKey),
        });

        if (target.OwnerUserId != profile.UserId || level < GrantLevel.Write)
        {
            throw RowLockRules.Denied(EditDenyReason.NoGrant, "Блокування");
        }
    }
}

/// <summary>✎ B75: <c>DELETE /documents/{id}/row-locks/{lockId}</c> — знімає блокування (історія лишається).</summary>
public sealed class ReleaseRowLockHandler(
    IRowLockStore store,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Знімає блокування.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="lockId">Блокування.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(long documentId, long lockId, CancellationToken ct)
    {
        var profile = await OpenDocumentPeriodReopenHandler.ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await store.LockDocumentAsync(documentId, innerCt).ConfigureAwait(false);

            var item = await store.FindAsync(documentId, lockId, innerCt).ConfigureAwait(false)
                       ?? throw new NotFoundException(
                           ErrorCodes.RowNotFound,
                           $"Блокування {lockId} у документі {documentId} немає.",
                           new Dictionary<string, object?>
                           {
                               ["messageKey"] = "err.ECR-ROW-0404.rowLock",
                               ["lockId"] = lockId.ToString(CultureInfo.InvariantCulture),
                           });

            var target = await store.GetTargetAsync(documentId, item.PeriodKeyValue, now, innerCt).ConfigureAwait(false)
                         ?? throw DocumentVisibility.NotFound(documentId);

            if (profile.IsSimulation)
            {
                throw RowLockRules.Denied(EditDenyReason.SimulationReadOnly, "Зняття блокування");
            }

            var state = RowLockRules.StateDenial(target);
            if (state != EditDenyReason.None)
            {
                throw RowLockRules.Denied(state, "Зняття блокування");
            }

            if (item.OwnerUserId != profile.UserId && !RowLockRules.HoldsRight(profile, target.ProjectId))
            {
                throw RowLockRules.Denied(EditDenyReason.NoGrant, "Зняття блокування");
            }

            if (!item.Release(profile.UserId, now))
            {
                return;
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            await audit.WriteStructureChangeAsync(
                RowLockAudit.Record(item, target, "Unlock", now, profile.UserId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>Запис журналу про блокування/зняття.</summary>
internal static class RowLockAudit
{
    public static StructureChangeRecord Record(
        RowLock item, RowLockTarget target, string operation, DateTime utcNow, int userId, string? correlationId)
        => new(
            utcNow,
            TemplateVersionId: target.TemplateVersionId,
            EntityType: RowLockRules.AuditEntityType,

            // ⚠ Ідентифікатор журналу — int; повний bigint блокування — у NewJson.
            EntityId: item.Id <= int.MaxValue ? (int)item.Id : 0,
            ChangeClass: ChangeClass.Guarded,
            Operation: operation,
            OldJson: null,
            NewJson: JsonSerializer.Serialize(new
            {
                lockId = item.Id,
                documentId = item.DocumentId,
                document = target.BusinessKey,
                periodKey = item.PeriodKeyValue,
                sheetCopyId = item.SheetCopyId,
                tableCode = item.TableCode,
                rowKey = item.RowKey,
                lockFrom = item.LockFrom,
                lockUntil = item.LockUntil,
                ownerUserId = item.OwnerUserId,
                note = item.Note,
            }),
            ChangeReason: item.Note,
            ChangedByUserId: userId,
            CorrelationId: correlationId);
}
