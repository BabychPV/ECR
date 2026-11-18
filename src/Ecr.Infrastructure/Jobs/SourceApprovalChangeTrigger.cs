// src/Ecr.Infrastructure/Jobs/SourceApprovalChangeTrigger.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// B80 (N2a, RW.2) → ✎ B179 L2: за зв'язкою джерела в стані <see cref="DocumentSourceState.ApprovedOnly"/> — та сама
/// злита задача <see cref="IRegistryImpactRecalculationJob"/>, що й у хука завершення (<see cref="SourceDocumentChangeHook"/>),
/// але від остаточного Approve, Reopen із затвердженого і видалення документа-джерела (перерахунок затвердженого аркуша
/// чисел не міняє).
/// </summary>
/// <remarks>
/// <para>
/// ✎ B179 L2: будь-яка зв'язка (шаблон-джерело — за самою зв'язкою, без коду HSE400), споживачі — з графа залежностей
/// у задачі (без ручного <c>cfg.DocumentSourceBindingConsumer</c> і без «є результат»). Затвердження/повернення місяця —
/// ціль «зв'язка × рік × місяць» (місяць затвердженого періоду місячного проєкту), видалення — «зв'язка × рік».
/// </para>
/// <para>
/// Ворота: стан <c>ApprovedOnly</c>, біт <c>RecalculateDependents</c> (ключа-перекриття більше немає, P-0118-34) і
/// читання з документа (біт <c>ReadFromDocument</c> або явний ключ <c>Hse400CompositionFromDocument</c>). Затверджені
/// аркуші споживачів задача пропускає, подані позначає лише за справжньою зміною чисел (B63/B79) — тут не змінюється.
/// </para>
/// </remarks>
public sealed partial class SourceApprovalChangeTrigger(
    EcrDbContext db,
    IBackgroundJobScheduler jobs,
    IDocumentSourceFlagOverrides? overrides = null,
    ILogger<SourceApprovalChangeTrigger>? logger = null) : ISourceApprovalChangeTrigger
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public async Task OnSourceSheetApprovalChangedAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, int? userId, CancellationToken ct)
    {
        try
        {
            var bindings = await ActiveBindingsAsync(ct).ConfigureAwait(false);
            if (bindings.Count == 0)
            {
                return;
            }

            // Аркуш шаблону-джерела — і проєкт документа (місяць лише в місячного).
            var sheet = await (
                    from s in db.SheetDefs.AsNoTracking()
                    where s.Id == sheetDefId
                    join v in db.TemplateVersions.AsNoTracking() on s.TemplateVersionId equals v.Id
                    select new { v.TemplateId })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            var matching = bindings.Where(b => b.SourceTemplateId == sheet?.TemplateId).ToList();
            if (sheet is null || matching.Count == 0)
            {
                return;
            }

            var monthly = await (
                    from d in db.Documents.AsNoTracking()
                    where d.Id == documentId
                    join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                    select p.PeriodKind == PeriodKind.Monthly)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            var verb = change == SourceApprovalChange.Approved ? "approved" : "reopened";
            foreach (var binding in matching)
            {
                await EnqueueAsync(
                        binding, periodKey / 100, monthly ? periodKey % 100 : null,
                        string.Create(CultureInfo.InvariantCulture, $"source binding {binding.Id}: {periodKey} {verb}"),
                        userId, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(_logger, documentId, ex);
        }
    }

    /// <inheritdoc />
    public async Task<SourceDocumentDeletion?> PrepareDocumentDeletionAsync(long documentId, CancellationToken ct)
    {
        try
        {
            var bindings = await ActiveBindingsAsync(ct).ConfigureAwait(false);
            if (bindings.Count == 0)
            {
                return null;
            }

            var templates = bindings.Select(b => b.SourceTemplateId).Distinct().ToList();
            var found = await (
                    from i in db.TableInstances.AsNoTracking()
                    where i.DocumentId == documentId && i.SheetCopyId == null
                    join d in db.TableDefs.AsNoTracking() on i.TableDefId equals d.Id
                    join s in db.SheetDefs.AsNoTracking() on d.SheetDefId equals s.Id
                    join v in db.TemplateVersions.AsNoTracking() on s.TemplateVersionId equals v.Id
                    where templates.Contains(v.TemplateId)
                    select new { v.TemplateId, i.PeriodKeyValue })
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);

            var bindingIds = bindings
                .Where(b => found.Any(f => f.TemplateId == b.SourceTemplateId))
                .Select(b => b.Id)
                .Order()
                .ToList();
            var years = found.Select(f => f.PeriodKeyValue / 100).Distinct().Order().ToList();
            return bindingIds.Count == 0 || years.Count == 0 ? null : new SourceDocumentDeletion(documentId, bindingIds, years);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(_logger, documentId, ex);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task OnSourceDocumentDeletedAsync(SourceDocumentDeletion deletion, int? userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        try
        {
            var bindings = (await ActiveBindingsAsync(ct).ConfigureAwait(false))
                .Where(b => deletion.BindingIds.Contains(b.Id))
                .ToList();
            foreach (var binding in bindings)
            {
                foreach (var year in deletion.Years)
                {
                    await EnqueueAsync(
                            binding, year, null,
                            string.Create(CultureInfo.InvariantCulture, $"source binding {binding.Id}: document deleted"),
                            userId, ct)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(_logger, deletion.DocumentId, ex);
        }
    }

    /// <summary>Зв'язки в стані <c>ApprovedOnly</c> з бітом перерахунку й читанням з документа.</summary>
    private async Task<List<ActiveBinding>> ActiveBindingsAsync(CancellationToken ct)
        => [.. (await db.DocumentSourceBindings.AsNoTracking()
                .Where(b => b.SourceState == DocumentSourceState.ApprovedOnly && b.RecalculateDependents)
                .Select(b => new { b.Id, b.SourceTemplateId, b.RegistryDefId, b.ReadFromDocument })
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(b => overrides?.ReadFromDocument ?? b.ReadFromDocument)
            .Select(b => new ActiveBinding(b.Id, b.SourceTemplateId, b.RegistryDefId))];

    private Task<string> EnqueueAsync(ActiveBinding binding, int year, int? month, string reason, int? userId, CancellationToken ct)
        => jobs.EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
            RegistryImpactRecalculationTarget.ForSourceChange(binding.Id, year, month),
            new RegistryImpactRecalculationRequest(binding.RegistryDefId, [], reason, year, binding.Id, month),
            ct,
            createdByUserId: userId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "B179: не вдалось поставити перерахунок залежних після дії з документом-джерелом {DocumentId}.")]
    private static partial void LogEnqueueFailed(ILogger logger, long documentId, Exception exception);

    private sealed record ActiveBinding(int Id, int SourceTemplateId, int RegistryDefId);
}
