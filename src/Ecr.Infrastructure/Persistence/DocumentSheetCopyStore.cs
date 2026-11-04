using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentSheetCopyStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⛔ Жодний зовнішній ключ не має <c>ON DELETE CASCADE</c>, тож видалення копії — явне, від листя до кореня й ПО ПЕРІОДАХ
/// (рівність за <c>PeriodKey</c> дає відсічку партицій, як у <see cref="DocumentDeletionStore"/>). Зрізи подань
/// (<c>calc.SubmissionSnapshot</c>) без зовнішнього ключа на копію й лишаються (Q2: історія переживає видалення).
/// </remarks>
public sealed class DocumentSheetCopyStore(EcrDbContext db) : IDocumentSheetCopyStore
{
    /// <summary>Номер першої копії в групі (оригінал — 1).</summary>
    private const short FirstCopyOrdinal = Ecr.Domain.Entities.Documents.DocumentSheet.OriginalOrdinal + 1;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SheetCopyGroupInfo>> ListGroupsAsync(long documentId, CancellationToken ct)
    {
        var rows = await db.DocumentSheets.AsNoTracking()
            .Where(s => s.DocumentId == documentId)
            .Select(s => new { s.Id, s.SheetDefId, s.CopyOfId, s.CopyOrdinal, s.NameOverrideL10n })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var copiesOf = rows.Where(r => r.CopyOfId != null)
            .GroupBy(r => r.CopyOfId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.CopyOrdinal).Select(r => new SheetCopyOrdinal(r.Id, r.CopyOrdinal)).ToList());

        return
        [
            .. rows.Where(r => r.CopyOfId == null)
                .Select(r => new SheetCopyGroupInfo(
                    r.SheetDefId, r.NameOverrideL10n, copiesOf.GetValueOrDefault(r.Id) ?? []))
                .Where(g => g.NameOverride is not null || g.Copies.Count > 0),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, int>> ChatMessageCountsAsync(long documentId, CancellationToken ct)
        => await db.TableDiscussions.AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.SheetCopyKey != 0 && s.MessageCount > 0)
            .GroupBy(s => s.SheetCopyKey)
            .Select(g => new { g.Key, Count = g.Sum(s => s.MessageCount) })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> DeleteChatAsync(long documentId, long sheetCopyId, CancellationToken ct)
    {
        var live = await db.TableDiscussions.AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.SheetCopyKey == sheetCopyId)
            .SumAsync(s => (int?)s.MessageCount, ct)
            .ConfigureAwait(false) ?? 0;

        // Листя → корінь (усі FK без каскаду): згадки, позначки, повідомлення, зведення.
        await db.TableDiscussionMentions
            .Where(m => m.DocumentId == documentId && m.SheetCopyKey == sheetCopyId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.TableDiscussionReads
            .Where(r => r.DocumentId == documentId && r.SheetCopyKey == sheetCopyId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.TableDiscussionMessages
            .Where(m => m.DocumentId == documentId && m.SheetCopyKey == sheetCopyId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.TableDiscussions
            .Where(s => s.DocumentId == documentId && s.SheetCopyKey == sheetCopyId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return live;
    }

    /// <inheritdoc />
    public async Task<SheetCopyGroup?> FindGroupAsync(long documentId, int sheetDefId, CancellationToken ct)
    {
        var original = await db.DocumentSheets.AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.SheetDefId == sheetDefId && s.CopyOfId == null)
            .Select(s => new { s.Id, s.NameOverrideL10n })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (original is null)
        {
            return null;
        }

        var count = await db.DocumentSheets.AsNoTracking()
            .CountAsync(s => s.CopyOfId == original.Id, ct)
            .ConfigureAwait(false);

        return new SheetCopyGroup(original.Id, sheetDefId, original.NameOverrideL10n, count);
    }

    /// <inheritdoc />
    public async Task<SheetCopyRef?> FindAsync(long documentId, long sheetCopyId, CancellationToken ct)
        => await db.DocumentSheets.AsNoTracking()
            .Where(s => s.Id == sheetCopyId && s.DocumentId == documentId)
            .Select(s => new SheetCopyRef(s.Id, s.CopyOfId ?? s.Id, s.SheetDefId, s.CopyOrdinal, s.CopyOfId != null))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<(long SheetCopyId, short Ordinal)> AddCopyAsync(long documentId, int sheetDefId, CancellationToken ct)
    {
        var original = await db.DocumentSheets
            .FirstAsync(s => s.DocumentId == documentId && s.SheetDefId == sheetDefId && s.CopyOfId == null, ct)
            .ConfigureAwait(false);

        // Наступний номер — за найбільшим наявним (а не за кількістю): структура під винятковим блокуванням, номери
        // щільні (видалення перенумеровує), але max безпечний і за такої гонки, яку блокування мало б виключити.
        var last = await db.DocumentSheets.AsNoTracking()
            .Where(s => s.CopyOfId == original.Id)
            .Select(s => (short?)s.CopyOrdinal)
            .MaxAsync(ct)
            .ConfigureAwait(false);

        var copy = original.CreateCopy((short)Math.Max(FirstCopyOrdinal, (last ?? 1) + 1));
        db.DocumentSheets.Add(copy);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (copy.Id, copy.CopyOrdinal);
    }

    /// <inheritdoc />
    public async Task RenameGroupAsync(long documentId, int sheetDefId, LocalizedText name, CancellationToken ct)
    {
        var original = await db.DocumentSheets
            .FirstAsync(s => s.DocumentId == documentId && s.SheetDefId == sheetDefId && s.CopyOfId == null, ct)
            .ConfigureAwait(false);

        original.Rename(name);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SheetCopyWorkflowBlock?> FindBlockingStateAsync(
        long documentId, long sheetCopyId, CancellationToken ct)
    {
        // UPDLOCK + HOLDLOCK: паралельне подання не вставить новий стан цієї копії, доки триває видалення.
        var states = await db.ApprovalStates
            .FromSql($"""
                SELECT * FROM wf.ApprovalState WITH (UPDLOCK, HOLDLOCK)
                WHERE DocumentId = {documentId} AND SheetCopyId = {sheetCopyId}
                """)
            .AsNoTracking()
            .Where(s => s.Status == DocumentStatus.Submitted || s.Status == DocumentStatus.Approved)
            .OrderBy(s => s.PeriodKey)
            .Select(s => new SheetCopyWorkflowBlock(s.PeriodKey, s.Status))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (states is not null)
        {
            return states;
        }

        // Дані копії в заархівованому році (arc.TableInstance): видалення лишило б сиріт, і відновлення року впало б на
        // зовнішньому ключі. Заархівований рік — закрита історія, тож це та сама відмова, що й для затвердженої копії.
        var archived = await db.Database
            .SqlQuery<int>($"""
                SELECT TOP (1) PeriodKey AS Value FROM arc.TableInstance
                WHERE DocumentId = {documentId} AND SheetCopyId = {sheetCopyId}
                ORDER BY PeriodKey
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return archived.Count > 0 ? new SheetCopyWorkflowBlock(archived[0], DocumentStatus.Approved) : null;
    }

    /// <inheritdoc />
    public async Task<int> DeleteCopyAsync(long documentId, long sheetCopyId, CancellationToken ct)
    {
        var periodKeys = await db.TableInstances.AsNoTracking()
            .Where(i => i.DocumentId == documentId && i.SheetCopyId == sheetCopyId)
            .Select(i => i.PeriodKeyValue)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var cells = 0;

        // ⚠ По одному періоду на набір DELETE: рівність за PeriodKey — відсічка партицій у кожному запиті
        // (той самий прийом, що в DocumentDeletionStore.DeleteAsync).
        foreach (var key in periodKeys.Order())
        {
            var instances = db.TableInstances.Where(
                i => i.PeriodKeyValue == key && i.DocumentId == documentId && i.SheetCopyId == sheetCopyId);
            var rows = db.TableRows.Where(
                r => r.PeriodKeyValue == key && instances.Any(i => i.Id == r.TableInstanceId));

            cells += await db.CellValues
                .Where(c => c.PeriodKeyValue == key && rows.Any(r => r.Id == c.TableRowId))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.CellStyles
                .Where(s => s.PeriodKeyValue == key && rows.Any(r => r.Id == s.TableRowId))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.CellFormulaOverrides
                .Where(o => o.PeriodKeyValue == key && instances.Any(i => i.Id == o.TableInstanceId))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.RowWindowValues
                .Where(v => v.PeriodKey == key && instances.Any(i => i.Id == v.TableInstanceId))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await rows.ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await instances.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        // AN-69: поточні проблеми розрахунку цієї копії.
        await db.CalculationIssues
            .Where(i => i.DocumentId == documentId && i.SheetCopyId == sheetCopyId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // Журнал подій і стани копії (зрізи подань лишаються — без зовнішнього ключа).
        await db.ApprovalEvents
            .Where(e => e.DocumentId == documentId && e.SheetCopyId == sheetCopyId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ApprovalStates
            .Where(s => s.DocumentId == documentId && s.SheetCopyId == sheetCopyId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var removed = await db.DocumentSheets.AsNoTracking()
            .Where(s => s.Id == sheetCopyId && s.DocumentId == documentId && s.CopyOfId != null)
            .Select(s => new { s.CopyOfId })
            .FirstAsync(ct)
            .ConfigureAwait(false);
        await db.DocumentSheets
            .Where(s => s.Id == sheetCopyId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // Перенумерація «щільно»: 3→2. Уніфікований унікальний індекс (CopyOfId, CopyOrdinal) — тож по одному запису
        // за зростанням, кожен у свій SaveChanges (номер, що звільнився, вільний до наступного кроку).
        var rest = await db.DocumentSheets
            .Where(s => s.CopyOfId == removed.CopyOfId)
            .OrderBy(s => s.CopyOrdinal)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var expected = FirstCopyOrdinal;
        foreach (var copy in rest)
        {
            if (copy.CopyOrdinal != expected)
            {
                copy.Renumber(expected);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            expected++;
        }

        return cells;
    }
}
