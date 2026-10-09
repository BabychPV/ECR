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
        //
        // ⛔ L10-06 (аудит 2026-10-09): перевірка — З БЛОКУВАННЯМ (UPDLOCK, HOLDLOCK), а не
        // голий `AnyAsync`. Під RCSI (06-rcsi.sql) звичайне читання бачить лише ЗАКОМІЧЕНІ рядки:
        // мапа, яку паралельна транзакція вже вставила, але ще не завершила, лишалась невидимою,
        // і видалення падало на FK_SEM_Document (547 → 500) посеред транзакції. Блокуюче читання
        // чекає завершення тієї вставки і бачить її (→ 409), а діапазонне блокування не пускає
        // нову мапу, доки транзакція видалення не завершиться (той самий прийом, що в
        // LockWorkflowFactsAsync). ⚠ Індексу з провідним `DocumentId` у ext.SourceEventMap немає
        // (лише UQ_SourceEventMap), тож діапазон — увесь скан таблиці конфігурації мапінгів; вона
        // мала, а видалення чернетки — рідка дія, тож ціна — коротка пауза створення мап.
        var hasEventMap = await db.SourceEventMaps
            .FromSql($"SELECT * FROM ext.SourceEventMap WITH (UPDLOCK, HOLDLOCK) WHERE DocumentId = {documentId}")
            .AsNoTracking()
            .AnyAsync(ct)
            .ConfigureAwait(false);

        if (hasEventMap)
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
        //
        // ⛔ R5-Q1-03: предикат ще й за `PeriodKey`. Голий `DocumentId` — скан УСІХ партицій
        // `calc.CalculationResult` (`IX_CalculationResult_Lookup` веде `PeriodKey`) і
        // `calc.CalculationInput` (лише PK `(PeriodKey, Id)`) з U-блокуваннями всередині цієї
        // транзакції, що вже тримає діапазон `ext.SourceEventMap`. Набір ключів повний за побудовою:
        // результат і вхід пишуться в `run.PeriodKey ?? 0` (`CalculationResultStore`), а кожен
        // рядок тримає FK на свій прогін — тож ключі всіх прогонів проєкту документа покривають
        // усі його рядки; періоди проєкту й 0 — запас на випадок, якщо ця побудова зміниться.
        var projectId = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => (int?)d.ProjectId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var documentKeys = new HashSet<int> { 0 };
        if (projectId is { } project)
        {
            documentKeys.UnionWith(await db.CalculationRuns.AsNoTracking()
                .Where(r => r.ProjectId == project)
                .Select(r => r.PeriodKey ?? 0)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false));
            documentKeys.UnionWith(await db.Periods.AsNoTracking()
                .Where(p => p.ProjectId == project)
                .Select(p => p.PeriodKeyValue)
                .ToListAsync(ct).ConfigureAwait(false));
        }

        // ⛔ По одному ключу на DELETE, а не `PeriodKey IN (@k1, @k2, …)`. Список параметрів
        // оптимізатор згортає в залишковий OR-предикат і вільний обирати скан вузького
        // `IX_CalculationResult_Version` (провідний `MethodologyVersionId`) — тоді відсічки партицій
        // немає зовсім (план на малій/порожній таблиці: Index Scan, 25 із 25). Рівність за
        // `PeriodKey` дає пошук із відсічкою до однієї партиції за будь-якої статистики.
        // Ключів — стільки, скільки періодів/прогонів у проєкту (одиниці-десятки), тож це дешево.
        foreach (var key in documentKeys.Order())
        {
            await db.CalculationResults
                .Where(r => r.PeriodKey == key && r.DocumentId == documentId)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.CalculationInputs
                .Where(r => r.PeriodKey == key && r.DocumentId == documentId)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        // ⛔ L10-06: прогони перерахунку САМЕ цього документа (`DocumentId`).
        // Прогони проєкту (`DocumentId = NULL`) лишаються — їх рядки цього
        // документа вже прибрано вище. Кроки, входи й результати прогону
        // тримають на ньому зовнішній ключ — спершу вони.
        //
        // ⛔ Рев'ю AN-37 P2-1: індексу з провідним `CalculationRunId` у
        // `calc.CalculationStep/Input/Result` немає, тож голий предикат за
        // прогоном — скан УСІХ партицій трьох найбільших таблиць усередині
        // транзакції видалення (блокує перерахунок). Тому спершу прогони в
        // пам'ять, далі предикат ще й за `PeriodKey` — відсічка партицій.
        // Прогін за період пише лише в свій період; річний (`PeriodKey` NULL) —
        // у періоди свого проєкту, і саме їх беремо.
        var runs = await db.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == documentId)
            .Select(r => new { r.Id, r.ProjectId, r.PeriodKey })
            .ToListAsync(ct).ConfigureAwait(false);

        if (runs.Count > 0)
        {
            var runIds = runs.Select(r => r.Id).ToList();
            var periodKeys = runs.Where(r => r.PeriodKey is not null).Select(r => r.PeriodKey!.Value).ToHashSet();

            if (runs.Any(r => r.PeriodKey is null))
            {
                var projectIds = runs.Select(r => r.ProjectId).Distinct().ToList();
                periodKeys.UnionWith(await db.Periods.AsNoTracking()
                    .Where(p => projectIds.Contains(p.ProjectId))
                    .Select(p => p.PeriodKeyValue)
                    .ToListAsync(ct).ConfigureAwait(false));
            }

            var keys = periodKeys.ToList();
            await db.CalculationSteps.Where(s => keys.Contains(s.PeriodKey) && runIds.Contains(s.CalculationRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.CalculationInputs.Where(i => keys.Contains(i.PeriodKey) && runIds.Contains(i.CalculationRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.CalculationResults.Where(r => keys.Contains(r.PeriodKey) && runIds.Contains(r.CalculationRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.CalculationRuns.Where(r => runIds.Contains(r.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        await db.DocumentIndexValues.Where(v => v.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ValidationResults.Where(v => v.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.DocumentHeaderValues.Where(v => v.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ApprovalStates.Where(s => s.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.DocumentSheets.Where(s => s.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Documents.Where(d => d.Id == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return cells;
    }
}
