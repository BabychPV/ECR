// src/Ecr.Infrastructure/Jobs/Hse400SourceApprovalTrigger.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// B80 (N2a, RW.2): за зв'язкою HSE400 → склад газу в стані <see cref="DocumentSourceState.ApprovedOnly"/> — та сама
/// злита задача року, що й у <see cref="Hse400LandDependentsTrigger"/>, але від остаточного Approve, Reopen із
/// затвердженого і видалення документа HSE400 (перерахунок затвердженого аркуша чисел не міняє).
/// </summary>
/// <remarks>
/// <para>
/// Ті самі перекриття, що в хуку завершення: <c>enabled</c> — ключ <see cref="Hse400LandDependentsTrigger.EnabledKey"/>,
/// заданий явно, інакше біт <c>RecalculateDependents</c>; <c>readOverride</c> — ключ завантажувача, інакше біт
/// <c>ReadFromDocument</c>. Зв'язки немає або вона в стані <c>Any</c> — нічого (тригер за <c>Any</c> — хук завершення).
/// </para>
/// <para>
/// ⚠ Обсяг — рік періоду (надмножина; звуження до місяця — борг). Затверджені аркуші Land задача пропускає, подані
/// позначає лише за справжньою зміною чисел (B63/B79) — тут не змінюється.
/// </para>
/// </remarks>
public sealed partial class Hse400SourceApprovalTrigger(
    EcrDbContext db,
    IBackgroundJobScheduler jobs,
    bool? enabled,
    bool? readOverride,
    ILogger<Hse400SourceApprovalTrigger>? logger = null,
    string registryCode = Hse400CompositionMapping.RegistryCode,
    string templateCode = Hse400CompositionMapping.TemplateCode) : ISourceApprovalChangeTrigger
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Створює тригер із перекриттями обох ключів (не задано — біт зв'язки).</summary>
    /// <param name="sp">Провайдер.</param>
    /// <param name="configuration">Конфігурація.</param>
    public static Hse400SourceApprovalTrigger Create(IServiceProvider sp, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(configuration);
        return new Hse400SourceApprovalTrigger(
            (EcrDbContext)sp.GetService(typeof(EcrDbContext))!,
            (IBackgroundJobScheduler)sp.GetService(typeof(IBackgroundJobScheduler))!,
            Hse400CompositionSnapshotLoader.Override(configuration, Hse400LandDependentsTrigger.EnabledKey),
            Hse400CompositionSnapshotLoader.Override(configuration, Hse400CompositionSnapshotLoader.EnabledKey),
            (ILogger<Hse400SourceApprovalTrigger>?)sp.GetService(typeof(ILogger<Hse400SourceApprovalTrigger>)));
    }

    /// <inheritdoc />
    public async Task OnSourceSheetApprovalChangedAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, int? userId, CancellationToken ct)
    {
        try
        {
            var registryDefId = await ActiveRegistryAsync(ct).ConfigureAwait(false);
            if (registryDefId is not { } registry)
            {
                return;
            }

            // Лише аркуш шаблону-джерела.
            var isSource = await (
                    from s in db.SheetDefs.AsNoTracking()
                    where s.Id == sheetDefId
                    join v in db.TemplateVersions.AsNoTracking() on s.TemplateVersionId equals v.Id
                    join t in db.Templates.AsNoTracking() on v.TemplateId equals t.Id
                    where t.Code == templateCode
                    select s.Id)
                .AnyAsync(ct).ConfigureAwait(false);
            if (!isSource)
            {
                return;
            }

            var verb = change == SourceApprovalChange.Approved ? "approved" : "reopened";
            await EnqueueAsync(
                    registry, periodKey / 100,
                    string.Create(CultureInfo.InvariantCulture, $"HSE400 {periodKey} {verb}"), userId, ct)
                .ConfigureAwait(false);
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
            var registryDefId = await ActiveRegistryAsync(ct).ConfigureAwait(false);
            if (registryDefId is not { } registry)
            {
                return null;
            }

            var periods = await (
                    from t in db.Templates.AsNoTracking()
                    where t.Code == templateCode
                    join v in db.TemplateVersions.AsNoTracking() on t.Id equals v.TemplateId
                    join s in db.SheetDefs.AsNoTracking() on v.Id equals s.TemplateVersionId
                    join d in db.TableDefs.AsNoTracking() on s.Id equals d.SheetDefId
                    join i in db.TableInstances.AsNoTracking() on d.Id equals i.TableDefId
                    where i.DocumentId == documentId && i.SheetCopyId == null
                    select i.PeriodKeyValue)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);

            var years = periods.Select(p => p / 100).Distinct().Order().ToList();
            return years.Count == 0 ? null : new SourceDocumentDeletion(documentId, registry, years);
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
            foreach (var year in deletion.Years)
            {
                await EnqueueAsync(deletion.RegistryDefId, year, "HSE400 document deleted", userId, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(_logger, deletion.DocumentId, ex);
        }
    }

    /// <summary>Довідник зв'язки, коли вона в стані <c>ApprovedOnly</c> і обидва перемикачі ввімкнено; інакше <c>null</c>.</summary>
    private async Task<int?> ActiveRegistryAsync(CancellationToken ct)
    {
        if (enabled == false || readOverride == false)
        {
            return null;
        }

        var binding = await DocumentSourceGate.FindBindingAsync(db, templateCode, registryCode, ct).ConfigureAwait(false);
        if (binding is null
            || binding.SourceState != DocumentSourceState.ApprovedOnly
            || !(enabled ?? binding.RecalculateDependents)
            || !(readOverride ?? binding.ReadFromDocument))
        {
            return null;
        }

        return binding.RegistryDefId;
    }

    private Task<string> EnqueueAsync(int registryDefId, int year, string reason, int? userId, CancellationToken ct)
        => jobs.EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
            RegistryImpactRecalculationTarget.ForYear(registryDefId, year),
            new RegistryImpactRecalculationRequest(registryDefId, [], reason, year),
            ct,
            createdByUserId: userId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "HSE400 (B80): не вдалось поставити перерахунок залежних документів Land після дії з документом {DocumentId}.")]
    private static partial void LogEnqueueFailed(ILogger logger, long documentId, Exception exception);
}
