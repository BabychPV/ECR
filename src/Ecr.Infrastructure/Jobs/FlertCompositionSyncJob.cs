// src/Ecr.Infrastructure/Jobs/FlertCompositionSyncJob.cs
using System.Text.Encodings.Web;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Flert;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Синк складу газу FLERT → дзеркало ECR <c>STREAM</c> → <c>STREAM_CASE</c> → <c>GAS_COMPOSITION</c> (<c>D-476</c>,
/// план FLERT F2): щодня о 02:00 часу сервера (<see cref="FlertCompositionSyncOptions.Cron"/>).
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Читання</b> — порт <see cref="IExternalTableSource"/> джерела <c>ext.DataSource</c> (транспорт <c>Sql</c>) за
/// НАЛАШТОВАНИМ запитом <see cref="FlertCompositionSchema.QueryKey"/>; адаптер у FLERT не пише (транзакція завжди
/// відкочується). FLERT недоступний — виняток адаптера (<c>ECR-INT-0503</c>), прогін <c>Failed</c>, копія незмінна.</item>
/// <item><b>Зіставлення</b> — <see cref="FlertCompositionMapping"/>. Помилка зіставлення (дрейф схеми, нечитане значення),
/// обрізаний знімок — <c>Failed</c>, у копію не пишеться нічого.</item>
/// <item><b>Порожній результат НЕ видаляє нічого</b>: статус <c>Degraded</c> (запит, що раптом повернув 0 рядків, —
/// ознака збою, а не «FLERT видалив усі потоки»; той самий запобіжник, що в синку довідників AF, <c>L4-02</c>).</item>
/// <item><b>Запис</b> — лише через <see cref="RegistryEntryWriter"/> від <c>svc-integration</c>, ОДНІЄЮ транзакцією на
/// весь знімок (довідники F0 — <see cref="FlertRegistryProvisioner"/> — у тій самій): потоки → кейси (вікно дії) → склад
/// → вимкнення відсутніх (<c>IsActive = false</c>, не видалення: повернення в FLERT вмикає запис знову). Повтор того
/// самого знімка — 0 змін і без нової ревізії даних (writer не зберігає незмінне). Рядки, які writer відхилив, —
/// одним повтором без них (і без складу їхніх кейсів), статус <c>Degraded</c>; другий відказ — <c>Failed</c>, копія незмінна.</item>
/// </list>
/// ⚠ Одночасний другий прогін (розклад + майбутня кнопка) — <see cref="JobDeferredException"/> за applock-ом.
/// <para>
/// ⚠ Перерахунок залежних документів після зміни (F6) і кнопка «Синхронізувати» (F3) — наступні кроки.
/// </para>
/// </remarks>
public sealed class FlertCompositionSyncJob(
    EcrDbContext db,
    IEnumerable<IExternalTableSource> sources,
    IntegrationActor actor,
    IClock clock,
    IServiceScopeFactory scopes,
    IConfiguration configuration) : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування (<c>itg.MaintenanceRun</c>).</summary>
    public static string Code => "flert-composition-sync";

    /// <summary>Ім'я applock-а: один синк FLERT за раз.</summary>
    public const string LockResource = "ecr.flert-composition-sync";

    /// <summary>Статус: застосовано без зауважень.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>Статус: застосовано не все або нічого не застосовано через запобіжник (порожній результат).</summary>
    public const string Degraded = "Degraded";

    /// <summary>Статус: копію не змінено (помилка зіставлення, обрізаний знімок, несумісний довідник).</summary>
    public const string Failed = MaintenanceRunFailure.FailedStatus;

    /// <summary>Скільки зауважень потрапляє в <c>DetailsJson</c>.</summary>
    public const int MaxListedIssues = 50;

    /// <summary>Коди довідників дзеркала: у проді — §11.4; тести підставляють свої (спільна тестова база).</summary>
    public FlertRegistryCodes Codes { get; init; } = FlertRegistryCodes.Default;

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            var outcome = await RunAsync(FlertCompositionSyncOptions.Read(configuration), Codes, ct)
                .ConfigureAwait(false);
            run.Complete(outcome.Status, outcome.ToJson(), clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await progress.ReportAsync(100, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not JobDeferredException)
        {
            await MaintenanceRunFailure.RecordAsync(db, run, ex, clock.UtcNow).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Один прохід синку. Публічний для тестів і майбутнього ручного запуску (F3).</summary>
    /// <param name="options">Налаштування.</param>
    /// <param name="codes">Коди довідників дзеркала.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-INT-0503</c>: джерела немає, транспорт не зареєстровано, FLERT недоступний.</exception>
    public async Task<FlertSyncOutcome> RunAsync(FlertCompositionSyncOptions options, FlertRegistryCodes codes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(codes);

        if (!options.Enabled)
        {
            return FlertSyncOutcome.Disabled();
        }

        using var author = await actor.EnterAsync(ct).ConfigureAwait(false);

        var dataSource = await db.DataSources
                             .AsNoTracking()
                             .FirstOrDefaultAsync(s => s.Code == options.DataSourceCode && s.IsActive, ct)
                             .ConfigureAwait(false)
                         ?? throw Unavailable(
                             $"Джерела FLERT «{options.DataSourceCode}» ({FlertCompositionSyncOptions.DataSourceCodeKey}) немає або воно вимкнене.",
                             "err.ECR-INT-0503.sourceMissing",
                             ("dataSourceId", options.DataSourceCode));

        var adapter = sources.FirstOrDefault(s => s.Transport == dataSource.Transport)
                      ?? throw Unavailable(
                          $"Транспорт {dataSource.Transport} не читає таблиць-довідників.",
                          "err.ECR-INT-0503.transportNotRegistered",
                          ("transport", dataSource.Transport.ToString()));

        // ⛔ Читання ДО будь-якого запису: недоступний FLERT кидає тут, копія незмінна.
        var table = await adapter
            .ReadTableAsync(dataSource.Id, FlertCompositionSchema.QueryKey, FlertCompositionSyncOptions.MaxRows, ct)
            .ConfigureAwait(false);

        if (table.Truncated)
        {
            return FlertSyncOutcome.Refused(Failed, "truncated", table.Rows.Count, []);
        }

        if (table.Rows.Count == 0)
        {
            return FlertSyncOutcome.Refused(Degraded, "emptyResult", 0, []);
        }

        var mirror = FlertCompositionMapping.Map(table);
        if (mirror.HasErrors)
        {
            return FlertSyncOutcome.Refused(Failed, "mappingErrors", mirror.SourceRows, mirror.Issues);
        }

        await using var gate = await AcquireGateAsync(ct).ConfigureAwait(false);

        // ⛔ Запобіжник масового вимкнення: непорожній, але ЧАСТКОВИЙ результат FLERT (обірваний запит, фільтр у тексті
        // запиту) мовчки вимкнув би кейси. Понад межу прогін не пише НІЧОГО — ні вимкнень, ні змін, ні нових записів:
        // копія не змішує два стани FLERT. Межа — max(DeactivateFloor, MaxDeactivatePercent % чинних кейсів).
        if (await DeactivationRefusalAsync(options, codes, mirror, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; ; attempt++)
        {
            var result = await ApplyAsync(mirror, codes, excluded, ct).ConfigureAwait(false);
            if (result.Rejected.Count == 0 || attempt == 1)
            {
                return result.Rejected.Count == 0
                    ? result.Outcome
                    : FlertSyncOutcome.Refused(Failed, "writerRejected", mirror.SourceRows, [.. mirror.Issues, .. result.Rejected]);
            }

            // Відхилені рядки — одним повтором без них (і без складу відхилених кейсів).
            excluded.UnionWith(result.Rejected.Select(r => r.Subject));
        }
    }

    /// <summary>
    /// Відмова за межею вимкнення: скільки чинних кейсів дзеркала знімок вимкнув би і скільки дозволено.
    /// <c>null</c> — у межах (або дзеркала ще немає).
    /// </summary>
    private async Task<FlertSyncOutcome?> DeactivationRefusalAsync(
        FlertCompositionSyncOptions options, FlertRegistryCodes codes, FlertCompositionMirror mirror, CancellationToken ct)
    {
        var caseRegistryId = await db.RegistryDefs
            .AsNoTracking()
            .Where(d => d.Code == codes.Case)
            .Select(d => (int?)d.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (caseRegistryId is not { } registryId)
        {
            return null;
        }

        var active = await db.RegistryEntries
            .AsNoTracking()
            .Where(e => e.RegistryDefId == registryId && !e.IsDeleted && e.IsActive)
            .Select(e => e.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var incoming = mirror.Cases.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = active.Count(code => !incoming.Contains(code));
        var limit = options.DeactivationLimit(active.Count);
        if (missing <= limit)
        {
            return null;
        }

        var detail = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Знімок FLERT вимкнув би {missing} з {active.Count} чинних кейсів, дозволено {limit} ({FlertCompositionSyncOptions.MaxDeactivatePercentKey} = {options.MaxDeactivatePercent} %, не менше {FlertCompositionSyncOptions.DeactivateFloor}): знімок не застосовано, копія незмінна.");
        return FlertSyncOutcome.Refused(Degraded, "deactivationLimit", mirror.SourceRows,
            [.. mirror.Issues, new FlertMappingIssue(FlertIssueSeverity.Error, "deactivationLimit", codes.Case, detail)]);
    }

    /// <summary>Одна спроба: власний DI-scope від <c>svc-integration</c> і ОДНА транзакція на весь знімок.</summary>
    private async Task<(FlertSyncOutcome Outcome, IReadOnlyList<FlertMappingIssue> Rejected)> ApplyAsync(
        FlertCompositionMirror mirror, FlertRegistryCodes codes, HashSet<string> excluded, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var author = await services.GetRequiredService<IntegrationActor>().EnterAsync(ct).ConfigureAwait(false);

        var scoped = services.GetRequiredService<EcrDbContext>();
        var uow = services.GetRequiredService<IUnitOfWork>();
        var writer = services.GetRequiredService<RegistryEntryWriter>();
        var provisioner = services.GetRequiredService<FlertRegistryProvisioner>();

        FlertSyncOutcome? outcome = null;
        var rejected = new List<FlertMappingIssue>();

        try
        {
            await uow.ExecuteInTransactionAsync(
                async tx =>
                {
                    scoped.ChangeTracker.Clear();
                    rejected.Clear();

                    var set = await provisioner.EnsureAsync(codes, tx).ConfigureAwait(false);
                    if (set.Problems.Count > 0)
                    {
                        outcome = FlertSyncOutcome.Refused(Failed, "registryIncompatible", mirror.SourceRows,
                            [.. set.Problems.Select(p => new FlertMappingIssue(FlertIssueSeverity.Error, "registryIncompatible", p, p))]);
                        throw new RollbackAttempt();
                    }

                    var counts = new FlertSyncCounts();
                    var issues = new List<FlertMappingIssue>(mirror.Issues);
                    issues.AddRange(set.Warnings.Select(w => new FlertMappingIssue(FlertIssueSeverity.Warning, "registrySource", w, w)));

                    var cases = mirror.Cases.Where(c => !excluded.Contains(c.Code)).ToList();
                    var streams = mirror.Streams.Where(s => !excluded.Contains(s.Code)).ToList();
                    var keptCases = cases.Where(c => streams.Any(s => s.Code == c.StreamCode)).ToList();
                    var keptCaseCodes = keptCases.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
                    var rows = mirror.Rows.Where(r => keptCaseCodes.Contains(r.CaseCode) && !excluded.Contains(r.Code)).ToList();

                    // 1. Потоки.
                    var streamResult = await writer.WriteAsync(
                        new RegistryEntryWriteBatch(set.Stream!.Id, [.. streams.Select(StreamWrite)]), tx).ConfigureAwait(false);
                    if (Reject(streamResult, streams.Select(s => s.Code).ToList(), rejected))
                    {
                        throw new RollbackAttempt();
                    }

                    await uow.SaveChangesAsync(tx).ConfigureAwait(false);
                    counts.Add("streams", streamResult);
                    var streamIds = await IdsAsync(scoped, set.Stream.Id, tx).ConfigureAwait(false);

                    // 2. Кейси (версії з вікном дії).
                    var caseResult = await writer.WriteAsync(
                        new RegistryEntryWriteBatch(set.Case!.Id, [.. keptCases.Select(c => CaseWrite(c, streamIds))]), tx).ConfigureAwait(false);
                    if (Reject(caseResult, keptCases.Select(c => c.Code).ToList(), rejected))
                    {
                        throw new RollbackAttempt();
                    }

                    await uow.SaveChangesAsync(tx).ConfigureAwait(false);
                    counts.Add("cases", caseResult);
                    var caseIds = await IdsAsync(scoped, set.Case.Id, tx).ConfigureAwait(false);

                    // 3. Склад. Компонента немає в COMPONENT — рядок не пишеться, зауваження (не мовчазний нуль).
                    var componentIds = await IdsAsync(scoped, set.Component!.Id, tx).ConfigureAwait(false);
                    var writable = new List<FlertCompositionEntry>();
                    foreach (var row in rows)
                    {
                        if (componentIds.ContainsKey(row.Component))
                        {
                            writable.Add(row);
                        }
                        else
                        {
                            issues.Add(new FlertMappingIssue(FlertIssueSeverity.Warning, "componentMissing", row.Code,
                                $"Компонента {row.Component} немає в {set.Component.Code}: рядок складу {row.Code} не записано."));
                        }
                    }

                    var rowResult = await writer.WriteAsync(
                        new RegistryEntryWriteBatch(set.Composition!.Id, [.. writable.Select(r => RowWrite(r, caseIds, componentIds))]), tx)
                        .ConfigureAwait(false);
                    if (Reject(rowResult, writable.Select(r => r.Code).ToList(), rejected))
                    {
                        throw new RollbackAttempt();
                    }

                    await uow.SaveChangesAsync(tx).ConfigureAwait(false);
                    counts.Add("compositions", rowResult);

                    // 4. Вимкнення відсутніх і ввімкнення повернутих. «Присутнє» — усе, що є у ЗНІМКУ FLERT, зокрема
                    // відхилене writer'ом і склад відхилених кейсів: його стан лишається попереднім, а не вимикається.
                    var present = mirror.Streams.Select(s => s.Code)
                        .Concat(mirror.Cases.Select(c => c.Code))
                        .Concat(mirror.Rows.Select(r => r.Code))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, registryId) in new[] { ("streams", set.Stream.Id), ("cases", set.Case.Id), ("compositions", set.Composition.Id) })
                    {
                        var (off, on) = await ToggleAsync(scoped, writer, registryId, present, tx).ConfigureAwait(false);
                        counts.Deactivated[name] = off;
                        counts.Reactivated[name] = on;
                        await uow.SaveChangesAsync(tx).ConfigureAwait(false);
                    }

                    var status = excluded.Count > 0 || issues.Any(i => i.Severity == FlertIssueSeverity.Warning && i.Kind is "componentMissing" or "registrySource")
                        ? Degraded
                        : Succeeded;
                    outcome = new FlertSyncOutcome(status, null, mirror.SourceRows, counts, set.Created, set.ComponentsAdded,
                        [.. issues, .. excluded.Select(e => new FlertMappingIssue(FlertIssueSeverity.Warning, "writerRejected", e, "Рядок відхилено writer'ом — пропущено."))]);
                },
                ct).ConfigureAwait(false);
        }
        catch (RollbackAttempt)
        {
            // Задуманий відкат: відмова рядків writer'а або несумісний довідник.
        }

        return (outcome ?? FlertSyncOutcome.Refused(Failed, "writerRejected", mirror.SourceRows, rejected), rejected);
    }

    /// <summary>Відмови рядків writer'а → зауваження з кодом запису; <c>true</c> — є хоч одна.</summary>
    private static bool Reject(RegistryEntryWriteResult result, List<string> codes, List<FlertMappingIssue> rejected)
    {
        foreach (var error in result.Errors)
        {
            var code = error.Row >= 1 && error.Row <= codes.Count ? codes[error.Row - 1] : error.Key;
            rejected.Add(new FlertMappingIssue(FlertIssueSeverity.Error, "writerRejected", code,
                $"{code}: {error.MessageKey}{(error.Field is null ? string.Empty : " (" + error.Field + ")")}"));
        }

        return result.Errors.Count > 0;
    }

    /// <summary>Код → Id живих записів довідника.</summary>
    private static async Task<Dictionary<string, long>> IdsAsync(EcrDbContext scoped, int registryDefId, CancellationToken ct)
        => (await scoped.RegistryEntries
                .AsNoTracking()
                .Where(e => e.RegistryDefId == registryDefId && !e.IsDeleted)
                .Select(e => new { e.Code, e.Id })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToDictionary(e => e.Code, e => e.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Вимикає живі ввімкнені записи, яких немає в знімку, і вмикає вимкнені, що повернулися.</summary>
    private static async Task<(int Off, int On)> ToggleAsync(
        EcrDbContext scoped, RegistryEntryWriter writer, int registryDefId, HashSet<string> present, CancellationToken ct)
    {
        var entries = await scoped.RegistryEntries
            .AsNoTracking()
            .Where(e => e.RegistryDefId == registryDefId && !e.IsDeleted)
            .Select(e => new { e.Id, e.Code, e.IsActive })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var updates = entries
            .Where(e => e.IsActive != present.Contains(e.Code))
            .Select(e => new RegistryEntryUpdate(e.Id, new Dictionary<string, object?>()) { IsActive = !e.IsActive })
            .ToList();

        if (updates.Count == 0)
        {
            return (0, 0);
        }

        var result = await writer.UpdateAsync(new RegistryEntryUpdateBatch(registryDefId, updates), ct).ConfigureAwait(false);
        if (result.Errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Синк FLERT: увімкнення/вимкнення записів довідника {registryDefId} відхилено ({result.Errors[0].MessageKey}).");
        }

        return (updates.Count(u => u.IsActive == false), updates.Count(u => u.IsActive == true));
    }

    private static RegistryEntryWrite StreamWrite(FlertStreamEntry s)
        => new(s.Code, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [FlertCompositionSchema.StreamLegacyId] = s.LegacyId,
            [FlertCompositionSchema.StreamNumber] = s.Number,
            [FlertCompositionSchema.StreamName] = s.Name,
            [FlertCompositionSchema.StreamIsOnshore] = s.IsOnshore,
            [FlertCompositionSchema.StreamIsSour] = s.IsSour,
            [FlertCompositionSchema.StreamIsHmb] = s.IsHmb,
            [FlertCompositionSchema.StreamHmbDoc] = s.HmbDoc,
            [FlertCompositionSchema.StreamGroup] = s.Group,
            [FlertCompositionSchema.StreamExcludeInList] = s.ExcludeInList,
        })
        {
            DisplayName = s.Name,
        };

    private static RegistryEntryWrite CaseWrite(FlertCaseEntry c, Dictionary<string, long> streamIds)
        => new(c.Code, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [FlertCompositionSchema.CaseStream] = streamIds[c.StreamCode],
            [FlertCompositionSchema.CaseName] = c.CaseName,
            [FlertCompositionSchema.CaseTemperature] = c.TemperatureC,
            [FlertCompositionSchema.CaseDensityOp] = c.DensityOp,
            [FlertCompositionSchema.CaseMwHysys] = c.MwHysys,
            [FlertCompositionSchema.CaseZ] = c.Z,
            [FlertCompositionSchema.CaseRuleParse] = c.RuleParse,
            [FlertCompositionSchema.CaseLegacyTypeId] = c.LegacyTypeId,
            [FlertCompositionSchema.CaseLegacyCompositionId] = c.LegacyCompositionId,
            [FlertCompositionSchema.CaseNoComposition] = c.NoComposition,
        })
        {
            DisplayName = c.CaseName,
            Validity = new ValidityWindow(c.ValidFrom, c.ValidTo),
        };

    private static RegistryEntryWrite RowWrite(
        FlertCompositionEntry r, Dictionary<string, long> caseIds, Dictionary<string, long> componentIds)
        => new(r.Code, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [FlertCompositionSchema.RowCase] = caseIds[r.CaseCode],
            [FlertCompositionSchema.RowComponent] = componentIds[r.Component],
            [FlertCompositionSchema.RowMolPct] = r.MolPct,
        });

    /// <summary>Applock на окремому з'єднанні; не SQL Server — без замка. Зайнято — відкладення.</summary>
    private async Task<SqlDistributedLock?> AcquireGateAsync(CancellationToken ct)
    {
        if (!db.Database.IsSqlServer() || db.Database.GetConnectionString() is not { } connectionString)
        {
            return null;
        }

        return await SqlDistributedLock.TryAcquireAsync(connectionString, LockResource, ct).ConfigureAwait(false)
               ?? throw new JobDeferredException(TimeSpan.FromSeconds(60), "Синк FLERT уже виконується; відкладено на 60 с.", LockResource);
    }

    private static BusinessRuleException Unavailable(string message, string messageKey, (string Key, string Value) param)
        => new(
            "ECR-INT-0503",
            message,
            new Dictionary<string, object?>
            {
                ["messageKey"] = messageKey,
                [param.Key] = param.Value,
            });

    /// <summary>Задуманий відкат спроби.</summary>
    private sealed class RollbackAttempt : Exception
    {
    }
}

/// <summary>Лічильники запису по довідниках дзеркала.</summary>
public sealed class FlertSyncCounts
{
    /// <summary>Створено записів.</summary>
    public Dictionary<string, int> Added { get; } = new(StringComparer.Ordinal);

    /// <summary>Записів зі зміненим значенням чи вікном.</summary>
    public Dictionary<string, int> Updated { get; } = new(StringComparer.Ordinal);

    /// <summary>Записів без змін.</summary>
    public Dictionary<string, int> Unchanged { get; } = new(StringComparer.Ordinal);

    /// <summary>Вимкнено (немає в знімку).</summary>
    public Dictionary<string, int> Deactivated { get; } = new(StringComparer.Ordinal);

    /// <summary>Увімкнено знову (повернулися в знімок).</summary>
    public Dictionary<string, int> Reactivated { get; } = new(StringComparer.Ordinal);

    /// <summary>Чи прогін змінив хоч щось.</summary>
    public bool AnyChange => Added.Values.Sum() + Updated.Values.Sum() + Deactivated.Values.Sum() + Reactivated.Values.Sum() > 0;

    internal void Add(string registry, RegistryEntryWriteResult result)
    {
        Added[registry] = result.Added;
        Updated[registry] = result.Updated;
        Unchanged[registry] = result.Unchanged;
    }
}

/// <summary>Підсумок прогону синку FLERT (<c>itg.MaintenanceRun.DetailsJson</c>).</summary>
/// <param name="Status"><c>Succeeded</c>, <c>Degraded</c> або <c>Failed</c>.</param>
/// <param name="Reason">Чому знімок не застосовано (<c>disabled</c>, <c>emptyResult</c>, <c>truncated</c>,
/// <c>mappingErrors</c>, <c>registryIncompatible</c>, <c>writerRejected</c>); <c>null</c> — застосовано.</param>
/// <param name="SourceRows">Рядків прочитано з FLERT.</param>
/// <param name="Counts">Лічильники запису; <c>null</c> — не писалося нічого.</param>
/// <param name="CreatedRegistries">Довідники, заведені цим прогоном (F0).</param>
/// <param name="ComponentsAdded">Компоненти, додані в <c>COMPONENT</c> цим прогоном.</param>
/// <param name="Issues">Зауваження.</param>
public sealed record FlertSyncOutcome(
    string Status,
    string? Reason,
    int SourceRows,
    FlertSyncCounts? Counts,
    IReadOnlyList<string> CreatedRegistries,
    IReadOnlyList<string> ComponentsAdded,
    IReadOnlyList<FlertMappingIssue> Issues)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Синк вимкнено налаштуванням.</summary>
    public static FlertSyncOutcome Disabled()
        => new(FlertCompositionSyncJob.Succeeded, "disabled", 0, null, [], [], []);

    /// <summary>Знімок не застосовано.</summary>
    /// <param name="status">Статус.</param>
    /// <param name="reason">Причина.</param>
    /// <param name="sourceRows">Рядків прочитано.</param>
    /// <param name="issues">Зауваження.</param>
    public static FlertSyncOutcome Refused(string status, string reason, int sourceRows, IReadOnlyList<FlertMappingIssue> issues)
        => new(status, reason, sourceRows, null, [], [], issues);

    /// <summary>Підсумок для журналу; зауважень — не більше <see cref="FlertCompositionSyncJob.MaxListedIssues"/>.</summary>
    public string ToJson()
        => JsonSerializer.Serialize(
            new
            {
                status = Status,
                reason = Reason,
                sourceRows = SourceRows,
                added = Counts?.Added,
                updated = Counts?.Updated,
                unchanged = Counts?.Unchanged,
                deactivated = Counts?.Deactivated,
                reactivated = Counts?.Reactivated,
                createdRegistries = CreatedRegistries,
                componentsAdded = ComponentsAdded,
                issueCount = Issues.Count,
                issues = Issues.Take(FlertCompositionSyncJob.MaxListedIssues)
                    .Select(i => new { severity = i.Severity.ToString(), kind = i.Kind, subject = i.Subject, detail = i.Detail }),
            },
            Json);
}
