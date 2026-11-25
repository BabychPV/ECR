using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ✎ 25.11: видалення проєкту з усіма даними (<see cref="IProjectPurgeStore"/>, <c>PurgeProjectHandler</c>).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Видалення йде НАЯВНИМИ сховищами, а не другою копією переліку таблиць: робочі дані документа —
/// <see cref="IDocumentDeletionStore.DeleteAsync"/> (той самий метод, що видаляє чернетку: комірки, рядки, екземпляри,
/// результати, прогони документа, чат, передачі, винятки місяців, блокування, дозволи, аркуші), рядок проєкту з
/// періодами, маршрутом, прив'язками шаблонів і грантами — <see cref="IProjectStore.DeleteAsync"/> (єдине дозволене
/// сторожем ФВ-1.15 місце). Тут — лише те, чого в чернетки бути не може: історія погодження (<c>wf.ApprovalEvent</c>,
/// <c>calc.SubmissionSnapshot</c>) і прогони рівня проєкту з їхніми кроками, входами, результатами, проблемами.
/// </para>
/// <para>
/// ⚠ Журнали аудиту (<c>aud.*</c>) і архів (<c>arc.*</c>) НЕ видаляються: аудит незмінний, а проєкт з архівом сюди не
/// доходить (B69, відмова в обробнику).
/// </para>
/// </remarks>
public sealed class ProjectPurgeStore(
    EcrDbContext db,
    IDocumentDeletionStore documents,
    IProjectStore projects,
    IDependencyGraphProvider graph) : IProjectPurgeStore
{
    private sealed class ProjectRow
    {
        public string Code { get; set; } = null!;
        public int Status { get; set; }
        public bool IsArchiving { get; set; }
    }

    /// <inheritdoc />
    public async Task<ProjectPurgeFacts?> ReadFactsAsync(int projectId, bool lockForDelete, CancellationToken ct)
    {
        // ⚠ UPDLOCK у транзакції видалення: паралельна активація/архівація чи нова прив'язка чекають кінця видалення.
        var row = lockForDelete
            ? await db.Database.SqlQuery<ProjectRow>($"""
                SELECT p.Code AS Code, CAST(p.Status AS int) AS Status, p.IsArchiving AS IsArchiving
                  FROM doc.Project AS p WITH (UPDLOCK, ROWLOCK)
                 WHERE p.Id = {projectId}
                """).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : await db.Database.SqlQuery<ProjectRow>($"""
                SELECT p.Code AS Code, CAST(p.Status AS int) AS Status, p.IsArchiving AS IsArchiving
                  FROM doc.Project AS p
                 WHERE p.Id = {projectId}
                """).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var docs = await (
                from d in db.Documents.AsNoTracking()
                where d.ProjectId == projectId
                join t in db.Templates.AsNoTracking() on d.TemplateId equals (int?)t.Id into tj
                from t in tj.DefaultIfEmpty()
                orderby d.Id
                select new { d.Id, d.BusinessKey, TemplateCode = t == null ? null : t.Code })
            .ToListAsync(ct).ConfigureAwait(false);
        var docIds = docs.Select(d => d.Id).ToList();

        var documentRows = new List<ProjectPurgeDocument>(docs.Count);
        foreach (var doc in docs)
        {
            documentRows.Add(new ProjectPurgeDocument(doc.Id, doc.BusinessKey, doc.TemplateCode, await CellCountAsync(doc.Id, ct).ConfigureAwait(false)));
        }

        var runIds = await db.CalculationRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId || (r.DocumentId != null && docIds.Contains(r.DocumentId.Value)))
            .Select(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var counts = new ProjectPurgeCounts(
            Cells: documentRows.Sum(d => d.Cells),
            CalculationResults: await db.CalculationResults.AsNoTracking()
                .LongCountAsync(r => docIds.Contains(r.DocumentId) || runIds.Contains(r.CalculationRunId), ct).ConfigureAwait(false),
            CalculationRuns: runIds.Count,
            CalculationIssues: await db.CalculationIssues.AsNoTracking()
                .LongCountAsync(i => docIds.Contains(i.DocumentId) || runIds.Contains(i.CalculationRunId), ct).ConfigureAwait(false),
            ApprovalEvents: await db.ApprovalEvents.AsNoTracking()
                .LongCountAsync(e => docIds.Contains(e.DocumentId), ct).ConfigureAwait(false),
            ApprovalStates: await db.ApprovalStates.AsNoTracking()
                .LongCountAsync(s => docIds.Contains(s.DocumentId), ct).ConfigureAwait(false),
            SubmissionSnapshots: await db.SubmissionSnapshots.AsNoTracking()
                .LongCountAsync(s => docIds.Contains(s.DocumentId), ct).ConfigureAwait(false),
            Periods: await db.Periods.AsNoTracking().LongCountAsync(p => p.ProjectId == projectId, ct).ConfigureAwait(false));

        var months = await db.TableInstances.AsNoTracking()
            .Where(i => docIds.Contains(i.DocumentId))
            .Select(i => i.PeriodKeyValue)
            .Distinct()
            .OrderBy(k => k)
            .ToListAsync(ct).ConfigureAwait(false);

        var archived = docIds.Count == 0
            ? []
            : await db.Database.SqlQuery<int>($"""
                SELECT DISTINCT a.PeriodKey AS Value
                  FROM arc.TableInstance AS a
                 WHERE a.DocumentId IN (SELECT CAST([value] AS bigint) FROM OPENJSON({JsonSerializer.Serialize(docIds)}))
                """).OrderBy(k => k).Take(1_000).ToListAsync(ct).ConfigureAwait(false);

        // ✎ R3 C2-b: мапи подій на ВЛАСНІ документи — окремий перелік (інша причина й інший вихід: прибрати в Sources),
        // а не «залежні інших проєктів».
        var eventMaps = await EventMapDependantsAsync(row.Code, docIds, ct).ConfigureAwait(false);
        var dependants = await SourceBindingDependantsAsync(projectId, row.Code, docIds, ct).ConfigureAwait(false);

        // ✎ R3 C2-f: місяці із затвердженими аркушами — видаляються без повернення; у перегляд і журнал.
        var approvedMonths = await db.ApprovalStates.AsNoTracking()
            .Where(s => docIds.Contains(s.DocumentId) && s.Status == Domain.Enums.DocumentStatus.Approved)
            .Select(s => s.PeriodKey)
            .Distinct()
            .OrderBy(k => k)
            .Take(1_000)
            .ToListAsync(ct).ConfigureAwait(false);

        // ✎ R3 C2-g: проєкт золотих тестів HSE400 — попередження (інструменти tools/hse400), не блок: вирішує людина.
        var warnings = ProjectPurgeWarnings.For(row.Code);

        return new ProjectPurgeFacts(
            row.Code,
            (Domain.Enums.ProjectStatus)row.Status,
            row.IsArchiving,
            // ⚠ Основна версія — лише для журналу змін; читає її ProjectStore (дозволене сторожем B74 місце).
            (await projects.GetDeletionFactsAsync(projectId, ct).ConfigureAwait(false))?.TemplateVersionId ?? 0,
            documentRows,
            counts,
            months,
            archived,
            dependants,
            eventMaps,
            approvedMonths,
            warnings);
    }

    /// <inheritdoc />
    public async Task<ProjectPurgeResult> PurgeAsync(int projectId, IReadOnlyList<long> documentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documentIds);

        try
        {
            return await PurgeCoreAsync(projectId, documentIds, ct).ConfigureAwait(false);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 547)
        {
            // ✎ R3 C2-e: під час видалення в проєкті з'явився рядок, якого немає у фактах (новий документ, період, прогін):
            // DELETE падає на FK. Транзакція викликача відкочується — нічого не видалено; людині 409 з причиною, не 500.
            throw new Application.Errors.ConcurrencyConflictException(
                "ECR-PRD-0409",
                $"Проєкт {projectId} змінився під час видалення (FK: {ex.Message}). Нічого не видалено — повторіть.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0409.purgeConcurrentChange",
                    ["projectId"] = projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
    }

    private async Task<ProjectPurgeResult> PurgeCoreAsync(int projectId, IReadOnlyList<long> documentIds, CancellationToken ct)
    {
        // ✎ R3 C2-d: задачі документів проєкту — та сама семантика, що «Скасувати» в черзі (DbJobQueue.RequestCancelAsync):
        // у черзі — Cancelled одразу, виконувана — позначка CancelRequestedAt (робітник зупиниться на найближчій перевірці).
        // Рядки задач лишаються історією (itg.JobProgress без FK на документ).
        var cancelledJobs = documentIds.Count == 0
            ? 0
            : await db.Database.ExecuteSqlAsync($"""
                UPDATE itg.JobProgress
                   SET [State] = CASE WHEN [State] = 'Queued' THEN 'Cancelled' ELSE [State] END,
                       CancelRequestedAt = CASE WHEN [State] = 'Running'
                           THEN ISNULL(CancelRequestedAt, SYSUTCDATETIME()) ELSE CancelRequestedAt END,
                       UpdatedAt = SYSUTCDATETIME()
                 WHERE [State] IN ('Queued', 'Running')
                   AND DocumentId IN (SELECT CAST([value] AS bigint) FROM OPENJSON({JsonSerializer.Serialize(documentIds)}))
                """, ct).ConfigureAwait(false);

        long cells = 0;
        foreach (var documentId in documentIds)
        {
            // Історія погодження — FK на документ без каскаду; у чернетки її немає, тому DocumentDeletionStore її не чіпає.
            // ⚠ Рівність за PeriodKey (відсічка партицій): ключі — з самих рядків документа.
            foreach (var key in await db.ApprovalEvents.AsNoTracking().Where(e => e.DocumentId == documentId)
                         .Select(e => e.PeriodKey).Distinct().ToListAsync(ct).ConfigureAwait(false))
            {
                await db.ApprovalEvents.Where(e => e.PeriodKey == key && e.DocumentId == documentId)
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            foreach (var key in await db.SubmissionSnapshots.AsNoTracking().Where(s => s.DocumentId == documentId)
                         .Select(s => s.PeriodKey).Distinct().ToListAsync(ct).ConfigureAwait(false))
            {
                await db.SubmissionSnapshots.Where(s => s.PeriodKey == key && s.DocumentId == documentId)
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            cells += await documents.DeleteAsync(documentId, ct).ConfigureAwait(false);
        }

        // Прогони рівня проєкту (DocumentId = NULL): кроки, входи, результати й проблеми — FK на прогін.
        var runs = await db.CalculationRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .Select(r => new { r.Id, r.PeriodKey })
            .ToListAsync(ct).ConfigureAwait(false);
        if (runs.Count > 0)
        {
            var runIds = runs.Select(r => r.Id).ToList();
            var keys = runs.Where(r => r.PeriodKey is not null).Select(r => r.PeriodKey!.Value).ToHashSet();
            keys.Add(0);
            keys.UnionWith(await db.Periods.AsNoTracking().Where(p => p.ProjectId == projectId)
                .Select(p => p.PeriodKeyValue).ToListAsync(ct).ConfigureAwait(false));

            // ⛔ По одному ключу на DELETE (X1-03): рівність за PeriodKey — відсічка партицій найбільших таблиць.
            foreach (var key in keys.Order())
            {
                await db.CalculationSteps.Where(s => s.PeriodKey == key && runIds.Contains(s.CalculationRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.CalculationInputs.Where(i => i.PeriodKey == key && runIds.Contains(i.CalculationRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.CalculationResults.Where(r => r.PeriodKey == key && runIds.Contains(r.CalculationRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            await CalculationIssueRunCleanup.DeleteForRunsAsync(db, runIds, ct).ConfigureAwait(false);
            await db.CalculationRuns.Where(r => runIds.Contains(r.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        // Похідні рядки проєкту: погодження перерахунку, історія архівацій (даних в архіві немає — B69 відмова раніше),
        // пари перейменувань проєкту (B127), скоупи стабільних вʼюх (B129, перебудовує rpt.usp_GenerateStableViews).
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM calc.RecalculationApproval WHERE ProjectId = {projectId};
            DELETE FROM itg.ArchiveRun WHERE ProjectId = {projectId};
            DELETE FROM cfg.ColumnRenameProject WHERE ProjectId = {projectId};
            IF OBJECT_ID(N'rpt.ViewProjectScope', N'U') IS NOT NULL
                DELETE FROM rpt.ViewProjectScope WHERE ProjectId = {projectId};
            """, ct).ConfigureAwait(false);

        await projects.DeleteAsync(projectId, ct).ConfigureAwait(false);
        return new ProjectPurgeResult(cells, cancelledJobs);
    }

    private Task<long> CellCountAsync(long documentId, CancellationToken ct)
    {
        var instances = db.TableInstances.Where(i => i.DocumentId == documentId);
        var rows = db.TableRows.Where(r => instances.Any(i => i.Id == r.TableInstanceId && i.PeriodKeyValue == r.PeriodKeyValue));
        return db.CellValues.AsNoTracking()
            .LongCountAsync(c => rows.Any(r => r.Id == c.TableRowId && r.PeriodKeyValue == c.PeriodKeyValue), ct);
    }

    /// <summary>Мапа подій джерела на документ проєкту — налаштована інтеграція (L10-06): мовчки не видаляється.</summary>
    private async Task<List<ProjectPurgeDependant>> EventMapDependantsAsync(string projectCode, List<long> docIds, CancellationToken ct)
    {
        var mapped = await db.SourceEventMaps.AsNoTracking()
            .Where(m => docIds.Contains(m.DocumentId))
            .Select(m => m.DocumentId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (mapped.Count == 0)
        {
            return [];
        }

        return await db.Documents.AsNoTracking()
            .Where(d => mapped.Contains(d.Id))
            .OrderBy(d => d.Id)
            .Select(d => new ProjectPurgeDependant(d.Id, d.BusinessKey, projectCode, "SourceEventMap"))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// B80: документ цього проєкту — ОБРАНЕ джерело зв'язки за рік (той самий вибір, що в завантажувачі:
    /// <see cref="DocumentSourceBindingStore.ChosenSourceDocumentAsync(IReadOnlyCollection{int}, int, CancellationToken)"/>),
    /// і за той рік є документи-споживачі в інших проєктах (шаблон-споживач — з графа залежностей).
    /// </summary>
    private async Task<List<ProjectPurgeDependant>> SourceBindingDependantsAsync(
        int projectId, string projectCode, List<long> docIds, CancellationToken ct)
    {
        // ⚠ Проєкт золотих тестів джерелом не обирається ніколи (синтетичний ввід) — залежних у нього немає.
        if (docIds.Count == 0 || string.Equals(projectCode, Hse400CompositionMapping.GoldProjectCode, StringComparison.Ordinal))
        {
            return [];
        }

        var bindings = await db.DocumentSourceBindings.AsNoTracking()
            .Where(b => b.ReadFromDocument)
            .Select(b => new { b.Id, b.Code })
            .ToListAsync(ct).ConfigureAwait(false);
        if (bindings.Count == 0)
        {
            return [];
        }

        var store = new DocumentSourceBindingStore(db, graph);
        var dependencies = (await graph.GetAsync(ct).ConfigureAwait(false)).TemplateDependencies;
        var result = new List<ProjectPurgeDependant>();
        foreach (var binding in bindings)
        {
            var consumers = dependencies.Where(d => d.BindingId == binding.Id).Select(d => d.ConsumerTemplateId).Distinct().ToList();
            if (consumers.Count == 0)
            {
                continue;
            }

            var tableIds = await store.BoundTableIdsAsync(binding.Id, ct).ConfigureAwait(false);
            if (tableIds.Count == 0)
            {
                continue;
            }

            var years = (await db.TableInstances.AsNoTracking()
                    .Where(i => docIds.Contains(i.DocumentId) && tableIds.Contains(i.TableDefId) && i.SheetCopyId == null)
                    .Select(i => i.PeriodKeyValue / 100)
                    .Distinct()
                    .ToListAsync(ct).ConfigureAwait(false))
                .Order();

            foreach (var year in years)
            {
                var chosen = await store.ChosenSourceDocumentAsync(tableIds, year, ct).ConfigureAwait(false);
                if (chosen is null || !docIds.Contains(chosen.DocumentId))
                {
                    continue;
                }

                var yearStart = year * 100;
                var yearEnd = (year + 1) * 100;
                var found = await (
                        from i in db.TableInstances.AsNoTracking()
                        where i.PeriodKeyValue >= yearStart && i.PeriodKeyValue < yearEnd
                        join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                        join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                        join v in db.TemplateVersions.AsNoTracking() on s.TemplateVersionId equals v.Id
                        where consumers.Contains(v.TemplateId)
                        join d in db.Documents.AsNoTracking() on i.DocumentId equals d.Id
                        where d.ProjectId != projectId
                        join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                        select new { d.Id, d.BusinessKey, ProjectCode = p.Code })
                    .Distinct()
                    .ToListAsync(ct).ConfigureAwait(false);

                result.AddRange(found
                    .OrderBy(f => f.Id)
                    .Select(f => new ProjectPurgeDependant(
                        f.Id, f.BusinessKey, f.ProjectCode, $"SourceBinding:{binding.Code}:{year.ToString(System.Globalization.CultureInfo.InvariantCulture)}")));
            }
        }

        return [.. result.DistinctBy(r => (r.DocumentId, r.Reason))];
    }
}
