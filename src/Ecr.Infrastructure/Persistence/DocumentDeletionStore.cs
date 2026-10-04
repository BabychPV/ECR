using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentDeletionStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⛔ Жоден зовнішній ключ на <c>doc.Document</c> не має <c>ON DELETE CASCADE</c>
/// (усі <c>NO_ACTION</c>), тож видалення — явне, від листя до кореня. <c>aud.CellChange</c>
/// НЕ чіпається: це журнал, і сам факт видалення лягає поруч у <c>aud.SecurityEvent</c>.
/// </remarks>
public sealed class DocumentDeletionStore(EcrDbContext db) : IDocumentDeletionStore
{
    /// <summary>Стеля станів на документ: 500 аркушів × 12 періодів із запасом.</summary>
    private const int MaxStates = 10_000;

    /// <inheritdoc />
    public async Task<DocumentWorkflowFacts> LockWorkflowFactsAsync(long documentId, CancellationToken ct)
    {
        // HOLDLOCK — блокування діапазону: паралельний Submit не вставить новий стан
        // цього документа, доки транзакція видалення не завершиться.
        var states = await db.ApprovalStates
            .FromSql($"SELECT * FROM wf.ApprovalState WITH (UPDLOCK, HOLDLOCK) WHERE DocumentId = {documentId}")
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .Take(MaxStates)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasHistory =
            await db.ApprovalEvents.AnyAsync(e => e.DocumentId == documentId, ct).ConfigureAwait(false)
            || await db.SubmissionSnapshots.AnyAsync(s => s.DocumentId == documentId, ct).ConfigureAwait(false);

        return new DocumentWorkflowFacts(states, hasHistory);
    }

    /// <inheritdoc />
    public async Task<int> DeleteAsync(long documentId, CancellationToken ct)
    {
        // ⛔ L10-06: мапа подій джерела (`ext.SourceEventMap`) — конфігурація
        // інтеграції, що посилається на документ зовнішнім ключем. Доти
        // видалення падало на FK 547 уже посеред транзакції — і людина бачила
        // 500. Мапу мовчки не видаляємо (це чужа налаштована робота, не дані
        // документа): відмова 409 з причиною, документ лишається цілим.
        if (await db.SourceEventMaps.AnyAsync(m => m.DocumentId == documentId, ct).ConfigureAwait(false))
        {
            throw new DomainException(
                ErrorCodes.DocumentSubmitted,
                $"Документ {documentId} використовує мапа подій джерела; спершу приберіть мапу.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0409.deleteHasEventMap",
                    ["reason"] = "SourceEventMap",
                });
        }

        var instances = db.TableInstances.Where(i => i.DocumentId == documentId);
        var rows = db.TableRows.Where(r => instances.Any(
            i => i.Id == r.TableInstanceId && i.PeriodKeyValue == r.PeriodKeyValue));

        var cells = await db.CellValues
            .Where(c => rows.Any(r => r.Id == c.TableRowId && r.PeriodKeyValue == c.PeriodKeyValue))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // ⛔ L10-06: значення PI за вікном рядка ключуються екземпляром таблиці
        // без зовнішнього ключа — після видалення екземплярів їх уже не знайти.
        await db.RowWindowValues
            .Where(v => instances.Any(i => i.Id == v.TableInstanceId && i.PeriodKeyValue == v.PeriodKey))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        await rows.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await instances.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // Похідні дані без зовнішнього ключа: лишити їх — означало б сиріт, що
        // вказують на неіснуючий документ.
        await db.CalculationResults.Where(r => r.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.CalculationInputs.Where(r => r.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // ⛔ L10-06: прогони перерахунку САМЕ цього документа (`DocumentId`).
        // Прогони проєкту (`DocumentId = NULL`) лишаються — їх рядки цього
        // документа вже прибрано вище. Кроки, входи й результати прогону
        // тримають на ньому зовнішній ключ — спершу вони.
        var runs = db.CalculationRuns.Where(r => r.DocumentId == documentId);
        await db.CalculationSteps.Where(s => runs.Any(r => r.Id == s.CalculationRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.CalculationInputs.Where(i => runs.Any(r => r.Id == i.CalculationRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.CalculationResults.Where(r => runs.Any(x => x.Id == r.CalculationRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await runs.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        await db.DocumentIndexValues.Where(v => v.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ValidationResults.Where(v => v.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.DocumentHeaderValues.Where(v => v.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ApprovalStates.Where(s => s.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.DocumentSheets.Where(s => s.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Documents.Where(d => d.Id == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return cells;
    }
}
