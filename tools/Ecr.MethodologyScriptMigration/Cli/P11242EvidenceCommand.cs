using System.Globalization;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.MethodologyScriptMigration.Cli;

/// <summary>
/// P-1124-2 phase 1 EVIDENCE ONLY (copy, read-only): <c>p11242-evidence history</c> — for every Formulas version that holds
/// stored <c>calc.CalculationResult</c> rows, evaluate the old engine and the version's shadow script on the SAME inputs of
/// every (document, period, table instance) the results came from, and compare three ways: old vs script (exact, ParityDiff),
/// stored vs script, stored vs old. <c>p11242-evidence water --text &lt;file&gt;</c> — preview a candidate script for the water
/// version on every T7b_00 instance and dump the output values. Nothing is written to the database.
/// </summary>
public static class P11242EvidenceCommand
{
    /// <summary>Runs the command.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        string? mode = args.Length > 1 ? args[1] : null, connection = null, outDir = null, text = null, texts = null;
        int? versionId = null;
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection" when i + 1 < args.Length: connection = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--text" when i + 1 < args.Length: text = args[++i]; break;
                case "--current": break;
                case "--texts" when i + 1 < args.Length: texts = args[++i]; break;
                case "--version" when i + 1 < args.Length: versionId = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    await error.WriteLineAsync($"unknown parameter {args[i]}").ConfigureAwait(false);
                    return MigrationCli.UsageOrEnvironment;
            }
        }

        if (mode == "selection" && connection is not null && outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            await using var p = ParityHost.Build(connection);
            await using var s = p.CreateAsyncScope();
            return await SelectionAsync(s.ServiceProvider, connection, outDir, output, ct).ConfigureAwait(false);
        }

        if (mode == "issues" && connection is not null && outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            await using var p = ParityHost.Build(connection);
            await using var s = p.CreateAsyncScope();
            return await IssuesAsync(s.ServiceProvider, connection, outDir, versionId ?? 0, text ?? "2026-11-24 14:36:00", output, ct).ConfigureAwait(false);
        }

        if (mode is not ("history" or "water") || connection is null || outDir is null)
        {
            await error.WriteLineAsync("p11242-evidence history|water --connection <cs> --out <dir> [--text <ecrs>] [--version <id>]").ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        Directory.CreateDirectory(outDir);
        await using var provider = ParityHost.Build(connection);
        await using var scope = provider.CreateAsyncScope();
        return mode == "history"
            ? await HistoryAsync(scope.ServiceProvider, connection, outDir, output, ct, versionId, args.Contains("--current"), texts).ConfigureAwait(false)
            : await WaterAsync(scope.ServiceProvider, connection, outDir, text!, versionId ?? 46, output, ct).ConfigureAwait(false);
    }

    private sealed record StoredValue(string RowKey, string OutputCode, int? SubstanceEntryId, CalculationResultKind Kind, decimal Value, int UnitId)
    {
        public string Key => string.Create(CultureInfo.InvariantCulture, $"{OutputCode}|{SubstanceEntryId?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{Kind}");
    }

    private static async Task<int> HistoryAsync(IServiceProvider sp, string connection, string outDir, TextWriter output, CancellationToken ct, int? onlyVersion = null, bool current = false, string? texts = null)
    {
        var methodologies = sp.GetRequiredService<IMethodologyStore>();
        var resolver = sp.GetRequiredService<MethodologyResolver>();
        var selector = sp.GetRequiredService<ScriptRowSelector>();
        var inputBuilder = sp.GetRequiredService<CalculationInputBuilder>();
        var modules = sp.GetServices<ICalculationModule>().ToList();
        var scriptModule = modules.OfType<ScriptCalculationModule>().First();
        var periodStore = sp.GetRequiredService<IPeriodStore>();

        await using var sql = new SqlConnection(connection);
        await sql.OpenAsync(ct).ConfigureAwait(false);

        var targets = new List<(int VersionId, string Code, string Version, string? Script, long Doc, int Period, long Instance)>();
        await using (var cmd = new SqlCommand(
            """
            SELECT DISTINCT v.Id, m.Code, v.Version, v.ScriptText, r.DocumentId, r.PeriodKey, ISNULL(r.TableInstanceId, 0)
            FROM calc.CalculationResult r JOIN calc.MethodologyVersion v ON v.Id = r.MethodologyVersionId
            JOIN calc.Methodology m ON m.Id = v.MethodologyId
            WHERE (v.Engine = 0 OR @current = 1) AND v.Level = 1 AND (@only = 0 OR v.Id = @only) ORDER BY m.Code, v.Id, r.DocumentId, r.PeriodKey, 7
            """, sql) { CommandTimeout = 1800 })
        {
            cmd.Parameters.AddWithValue("@only", onlyVersion ?? 0);
            cmd.Parameters.AddWithValue("@current", current ? 1 : 0);
            await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
            {
                targets.Add((rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetInt64(4), rd.GetInt32(5), rd.GetInt64(6)));
            }
        }

        var summary = new StringBuilder("versionId,code,version,doc,period,instance,storedValues,oldValues,scriptValues,oldVsScriptMismatches,storedVsScriptMismatches,storedVsOldMismatches,note,scriptHash\n");
        var detail = new StringBuilder("versionId,code,version,doc,period,instance,comparison,rowKey,aspect,key,left,right\n");
        var totalStored = 0; var totalOldScript = 0; var totalStoredScript = 0; var totalStoredOld = 0;
        foreach (var target0 in targets)
        {
            var t = target0;
            ct.ThrowIfCancellationRequested();
            var stored = new List<StoredValue>();
            await using (var cmd = new SqlCommand(
                """
                SELECT SourceRowKey, OutputCode, SubstanceEntryId, Kind, Value, UnitId FROM calc.CalculationResult
                WHERE MethodologyVersionId=@v AND DocumentId=@d AND PeriodKey=@p AND ISNULL(TableInstanceId,0)=@i
                """, sql) { CommandTimeout = 1800 })
            {
                cmd.Parameters.AddWithValue("@v", target0.VersionId);
                cmd.Parameters.AddWithValue("@d", t.Doc);
                cmd.Parameters.AddWithValue("@p", t.Period);
                cmd.Parameters.AddWithValue("@i", t.Instance);
                await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await rd.ReadAsync(ct).ConfigureAwait(false))
                {
                    stored.Add(new StoredValue(rd.IsDBNull(0) ? string.Empty : rd.GetString(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetInt32(2),
                        (CalculationResultKind)rd.GetByte(3), rd.GetDecimal(4), rd.GetInt32(5)));
                }
            }

            // --current: рахує ЧИННА на кінець періоду версія (як прогін сьогодні), а не та, що дала збережене число.
            if (current)
            {
                var owner = await methodologies.FindByVersionAsync(t.VersionId, ct).ConfigureAwait(false);
                var bounds = await periodStore.FindPeriodBoundsAsync(t.Doc, t.Period, ct).ConfigureAwait(false);
                var effective = owner is null || bounds is null ? null : (await resolver.ResolveBindingVersionAsync(owner.Id, bounds.PeriodEnd, ct).ConfigureAwait(false)).Descriptor;
                if (effective is not null)
                {
                    var effectiveVersion = owner!.Versions.Single(v => v.Id == effective.MethodologyVersionId);
                    t = (effective.MethodologyVersionId, t.Code, effectiveVersion.Version, effectiveVersion.ScriptText, t.Doc, t.Period, t.Instance);
                }
            }
            // --texts: текст із теки convert (той, що піде у finalize), якщо файл є.
            if (texts is not null && ScriptFiles.PathOf(texts, t.Code, t.Version) is var textFile && File.Exists(textFile))
            {
                t = t with { Script = (await File.ReadAllTextAsync(textFile, new UTF8Encoding(false), ct).ConfigureAwait(false)).Replace("\r\n", "\n", StringComparison.Ordinal) };
            }

            totalStored += stored.Count;
            string note = string.Empty;
            Dictionary<string, ParityRowOutcome> oldRows = new(StringComparer.Ordinal), scriptRows = new(StringComparer.Ordinal);
            var oldVsScript = 0;
            if (string.IsNullOrWhiteSpace(t.Script))
            {
                note = "no shadow script";
            }
            else if (t.Instance == 0)
            {
                note = "result without table instance";
            }
            else
            {
                try
                {
                    var methodology = await methodologies.FindByVersionAsync(t.VersionId, ct).ConfigureAwait(false);
                    var version = methodology!.Versions.Single(v => v.Id == t.VersionId);
                    var oldDescriptor = new MethodologyDescriptor(methodology.Id, version.Id, methodology.Code, version.Version, version.Level,
                        version.NumericMode, version.CalendarMode, version.TraceLevel, MethodologyEngine.Formulas);
                    var scriptDescriptor = oldDescriptor with { Engine = MethodologyEngine.Script };
                    var oldModule = modules.First(m => m.CanHandle(oldDescriptor));
                    var registries = new RegistrySnapshotCache(registryAsOfUtc: null);
                    var periodKey = new PeriodKey(t.Period);
                    var scriptContext = await scriptModule.PrepareAsync(scriptDescriptor, t.Doc, periodKey, registries,
                        new ScriptPrepareOptions(t.Script!, AllowShadowLibraries: true), ct).ConfigureAwait(false);
                    var oldOutcome = await resolver.MatchRowsDetailedAsync(t.VersionId, t.Instance, ct).ConfigureAwait(false);
                    var runtime = scriptContext.ScriptRuntime as ScriptRuntime;
                    var scriptOutcome = await selector.MatchAsync(scriptContext.Script!, t.Instance,
                        (registry, entry) => runtime?.EntryIdText(registry, entry), ct).ConfigureAwait(false);
                    IReadOnlyList<string> union = [.. oldOutcome.Matches.Select(m => m.RowKey).Union(scriptOutcome.Matches.Select(m => m.RowKey), StringComparer.Ordinal)
                        .Union(stored.Select(s => s.RowKey), StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                    var inputs = await inputBuilder.BuildAsync(t.Instance, union, periodKey, oldDescriptor, ct, oldOutcome.Read ?? scriptOutcome.Read).ConfigureAwait(false);
                    var byRow = inputs.ToDictionary(i => i.SourceRowKey ?? string.Empty, StringComparer.Ordinal);
                    var oldContext = await oldModule.PrepareAsync(oldDescriptor, t.Doc, periodKey, registries, ct).ConfigureAwait(false);
                    oldRows = await ParityComparer.ExecuteRowsAsync(oldOutcome.Matches.Select(m => m.RowKey), byRow, oldModule, oldContext, false, ct).ConfigureAwait(false);
                    scriptRows = await ParityComparer.ExecuteRowsAsync(scriptOutcome.Matches.Select(m => m.RowKey), byRow, scriptModule, scriptContext, false, ct).ConfigureAwait(false);
                    var target = new ParityTarget(t.Code, t.Version, t.VersionId, t.Doc, t.Period, t.Instance);
                    var report = ParityDiff.Compare(target,
                        new ParitySide([.. oldOutcome.Matches.Select(m => m.RowKey)], [.. oldOutcome.Excluded], oldOutcome.AllRowKeys, oldRows),
                        new ParitySide([.. scriptOutcome.Matches.Select(m => m.RowKey)], [.. scriptOutcome.Excluded], scriptOutcome.AllRowKeys, scriptRows));
                    oldVsScript = report.Mismatches.Count;
                    note = string.Create(CultureInfo.InvariantCulture,
                        $"old sel {oldOutcome.Matches.Count} excl {oldOutcome.Excluded?.Count ?? 0} all {oldOutcome.AllRowKeys?.Count ?? -1}; script sel {scriptOutcome.Matches.Count} excl {scriptOutcome.Excluded?.Count ?? 0} all {scriptOutcome.AllRowKeys?.Count ?? -1}");
                    foreach (var m in report.Mismatches)
                    {
                        detail.Append(Csv(t, "old-vs-script", m.RowKey, m.Aspect, string.Empty, m.Old, m.New));
                    }
                }
                catch (Exception ex) when (ex is DomainException or InvalidOperationException or ArgumentException)
                {
                    note = "error " + (ex is DomainException de ? de.ErrorCode : ex.GetType().Name) + " " + ex.Message.Replace(',', ';').Replace('\n', ' ');
                    oldVsScript = 1;
                }
            }

            var storedVsScript = CompareStored(t, stored, scriptRows, "stored-vs-script", detail);
            var storedVsOld = CompareStored(t, stored, oldRows, "stored-vs-old", detail);
            totalOldScript += oldVsScript; totalStoredScript += storedVsScript; totalStoredOld += storedVsOld;
            summary.Append(string.Join(',', t.VersionId, t.Code, t.Version, t.Doc, t.Period, t.Instance, stored.Count,
                oldRows.Values.Sum(r => r.Values.Count), scriptRows.Values.Sum(r => r.Values.Count), oldVsScript, storedVsScript, storedVsOld, note, string.IsNullOrWhiteSpace(t.Script) ? string.Empty : Convert.ToHexStringLower(Ecr.Scripting.ScriptCompiler.Hash(t.Script)))).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "history-parity-summary.csv"), summary.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outDir, "history-parity-detail.csv"), detail.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        await output.WriteLineAsync($"history: targets {targets.Count}, versions {targets.Select(t => t.VersionId).Distinct().Count()}, stored values {totalStored}, "
            + $"old-vs-script mismatches {totalOldScript}, stored-vs-script {totalStoredScript}, stored-vs-old {totalStoredOld}").ConfigureAwait(false);
        return totalOldScript + totalStoredScript == 0 ? MigrationCli.Ok : MigrationCli.Failures;
    }

    /// <summary>
    /// For every methodology with an active binding: the version effective on each document period that has a Current run,
    /// and the rows that version selects / excludes on each bound original table instance — next to the distinct result rows
    /// the Current run stored for that methodology. Read-only.
    /// </summary>
    private static async Task<int> SelectionAsync(IServiceProvider sp, string connection, string outDir, TextWriter output, CancellationToken ct)
    {
        var resolver = sp.GetRequiredService<MethodologyResolver>();
        var selector = sp.GetRequiredService<ScriptRowSelector>();
        var modules = sp.GetServices<ICalculationModule>().ToList();
        var scriptModule = modules.OfType<ScriptCalculationModule>().First();
        var periods = sp.GetRequiredService<IPeriodStore>();

        var targets = new List<(int MethodologyId, string Code, long Doc, int Period, long Instance, long RunId)>();
        var stored = new Dictionary<(int, long, int), (int Rows, int Values)>();
        await using (var sql = new SqlConnection(connection))
        {
            await sql.OpenAsync(ct).ConfigureAwait(false);
            await using (var cmd = new SqlCommand(
                """
                SELECT DISTINCT m.Id, m.Code, ti.DocumentId, ti.PeriodKey, ti.Id, run.Id
                FROM calc.CalculationRun run
                JOIN doc.TableInstance ti ON ti.DocumentId = run.DocumentId AND ti.PeriodKey = run.PeriodKey AND ti.SheetCopyId IS NULL
                JOIN cfg.CalculationBinding b ON b.TableDefId = ti.TableDefId AND b.IsActive = 1
                JOIN calc.Methodology m ON m.Id = b.MethodologyId
                WHERE run.Status = 'Current'
                ORDER BY m.Code, ti.DocumentId, ti.PeriodKey, ti.Id
                """, sql) { CommandTimeout = 1800 })
            await using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await rd.ReadAsync(ct).ConfigureAwait(false))
                {
                    targets.Add((rd.GetInt32(0), rd.GetString(1), rd.GetInt64(2), rd.GetInt32(3), rd.GetInt64(4), rd.GetInt64(5)));
                }
            }

            await using (var cmd = new SqlCommand(
                """
                SELECT v.MethodologyId, r.DocumentId, r.PeriodKey, COUNT(DISTINCT r.SourceRowKey), COUNT(*)
                FROM calc.CalculationResult r JOIN calc.CalculationRun run ON run.Id = r.CalculationRunId AND run.Status = 'Current'
                JOIN calc.MethodologyVersion v ON v.Id = r.MethodologyVersionId
                GROUP BY v.MethodologyId, r.DocumentId, r.PeriodKey
                """, sql) { CommandTimeout = 1800 })
            await using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await rd.ReadAsync(ct).ConfigureAwait(false))
                {
                    stored[(rd.GetInt32(0), rd.GetInt64(1), rd.GetInt32(2))] = (rd.GetInt32(3), rd.GetInt32(4));
                }
            }
        }

        var csv = new StringBuilder("code,doc,period,instance,versionId,engine,selected,excluded,all,storedRowsForMethodologyPeriod,storedValues,note\n");
        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            var note = string.Empty;
            int? versionId = null;
            var engine = string.Empty;
            int sel = -1, excl = -1, all = -1;
            try
            {
                var bounds = await periods.FindPeriodBoundsAsync(t.Doc, t.Period, ct).ConfigureAwait(false);
                var resolution = bounds is null ? null : await resolver.ResolveBindingVersionAsync(t.MethodologyId, bounds.PeriodEnd, ct).ConfigureAwait(false);
                var descriptor = resolution?.Descriptor;
                if (descriptor is null)
                {
                    note = "no effective version";
                }
                else
                {
                    versionId = descriptor.MethodologyVersionId;
                    engine = descriptor.Engine.ToString();
                    RowMatchOutcome outcome;
                    if (descriptor.Engine == MethodologyEngine.Script)
                    {
                        var ctx = await scriptModule.PrepareAsync(descriptor, t.Doc, new PeriodKey(t.Period), new RegistrySnapshotCache(null), ct).ConfigureAwait(false);
                        var runtime = ctx.ScriptRuntime as ScriptRuntime;
                        outcome = await selector.MatchAsync(ctx.Script!, t.Instance, (r, e) => runtime?.EntryIdText(r, e), ct,
                            allRows: descriptor.Level == CalculationLevel.Module).ConfigureAwait(false);
                    }
                    else
                    {
                        outcome = await resolver.MatchRowsDetailedAsync(descriptor.MethodologyVersionId, t.Instance, ct).ConfigureAwait(false);
                    }

                    sel = outcome.Matches.Count;
                    excl = outcome.Excluded?.Count ?? 0;
                    all = outcome.AllRowKeys?.Count ?? -1;
                }
            }
            catch (Exception ex) when (ex is DomainException or InvalidOperationException or ArgumentException)
            {
                note = "error " + ex.Message.Replace(',', ';').Replace('\n', ' ');
            }

            var s = stored.GetValueOrDefault((t.MethodologyId, t.Doc, t.Period));
            csv.Append(string.Join(',', t.Code, t.Doc, t.Period, t.Instance, versionId?.ToString(CultureInfo.InvariantCulture) ?? "", engine, sel, excl, all, s.Rows, s.Values, Q(note))).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "selection.csv"), csv.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        await output.WriteLineAsync($"selection: targets {targets.Count}").ConfigureAwait(false);
        return MigrationCli.Ok;
    }

    /// <summary>
    /// Документ × період (<c>--version</c> = документ, період — кожен із Current-прогоном): для кожної прив'язаної методології й
    /// екземпляра — версія, чинна ДО перемикання (остання опублікована до <c>--text</c> = момент, UTC), на рушії формул, і чинна
    /// ЗАРАЗ на її рушії: скільки рядків відібрано, з значеннями, MissingInputs, StepError, відхилено правилом категорії.
    /// </summary>
    private static async Task<int> IssuesAsync(IServiceProvider sp, string connection, string outDir, int documentId, string cutoffText, TextWriter output, CancellationToken ct)
    {
        var resolver = sp.GetRequiredService<MethodologyResolver>();
        var selector = sp.GetRequiredService<ScriptRowSelector>();
        var inputBuilder = sp.GetRequiredService<CalculationInputBuilder>();
        var methodologies = sp.GetRequiredService<IMethodologyStore>();
        var modules = sp.GetServices<ICalculationModule>().ToList();
        var scriptModule = modules.OfType<ScriptCalculationModule>().First();
        var periodStore = sp.GetRequiredService<IPeriodStore>();
        var cutoff = DateTime.Parse(cutoffText, CultureInfo.InvariantCulture);

        var targets = new List<(int MethodologyId, string Code, int Period, long Instance)>();
        await using (var sql = new SqlConnection(connection))
        {
            await sql.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new SqlCommand(
                """
                SELECT DISTINCT m.Id, m.Code, ti.PeriodKey, ti.Id
                FROM calc.CalculationRun run
                JOIN doc.TableInstance ti ON ti.DocumentId = run.DocumentId AND ti.PeriodKey = run.PeriodKey AND ti.SheetCopyId IS NULL
                JOIN cfg.CalculationBinding b ON b.TableDefId = ti.TableDefId AND b.IsActive = 1
                JOIN calc.Methodology m ON m.Id = b.MethodologyId
                WHERE run.Status = 'Current' AND run.DocumentId = @d AND m.Kind <> 2
                ORDER BY ti.PeriodKey, m.Code, ti.Id
                """, sql) { CommandTimeout = 1800 };
            cmd.Parameters.AddWithValue("@d", documentId);
            await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
            {
                targets.Add((rd.GetInt32(0), rd.GetString(1), rd.GetInt32(2), rd.GetInt64(3)));
            }
        }

        var csv = new StringBuilder("code,period,instance,side,versionId,engine,selected,withValues,values,missingInputs,stepError,rejected,error,missingNames\n");
        foreach (var t in targets)
        {
            var methodology = await methodologies.FindByVersionAsync(
                (await resolver.ResolveBindingVersionAsync(t.MethodologyId, new DateOnly(2099, 1, 1), ct).ConfigureAwait(false)).Descriptor?.MethodologyVersionId ?? 0, ct).ConfigureAwait(false);
            if (methodology is null)
            {
                continue;
            }

            var bounds = await periodStore.FindPeriodBoundsAsync(documentId, t.Period, ct).ConfigureAwait(false);
            var periodEnd = bounds?.PeriodEnd ?? new DateOnly(t.Period / 100, t.Period % 100, 28);
            var current = methodology.Versions
                .Where(v => v.Status == TemplateVersionStatus.Published && (v.EffectiveFrom ?? DateOnly.MinValue) <= periodEnd)
                .OrderByDescending(v => v.EffectiveFrom).ThenByDescending(v => v.Id).FirstOrDefault();
            var before = methodology.Versions
                .Where(v => v.Status is TemplateVersionStatus.Published or TemplateVersionStatus.Deprecated && v.PublishedAt < cutoff
                            && (v.EffectiveFrom ?? DateOnly.MinValue) <= periodEnd)
                .OrderByDescending(v => v.EffectiveFrom).ThenByDescending(v => v.Id).FirstOrDefault();
            foreach (var (side, version, engine) in new[] { ("before", before, MethodologyEngine.Formulas), ("after", current, current?.Engine ?? MethodologyEngine.Formulas) })
            {
                if (version is null)
                {
                    continue;
                }

                var descriptor = new MethodologyDescriptor(methodology.Id, version.Id, methodology.Code, version.Version, version.Level,
                    version.NumericMode, version.CalendarMode, version.TraceLevel, engine);
                string error = string.Empty, missingNames = string.Empty;
                int sel = 0, withValues = 0, values = 0, missing = 0, stepErr = 0, rejected = 0;
                try
                {
                    var registries = new RegistrySnapshotCache(registryAsOfUtc: null);
                    var periodKey = new PeriodKey(t.Period);
                    RowMatchOutcome outcome;
                    ICalculationModule module;
                    CalculationBindingContext context;
                    if (engine == MethodologyEngine.Script)
                    {
                        module = scriptModule;
                        context = await scriptModule.PrepareAsync(descriptor, documentId, periodKey, registries, ct).ConfigureAwait(false);
                        var runtime = context.ScriptRuntime as ScriptRuntime;
                        outcome = await selector.MatchAsync(context.Script!, t.Instance, (r, e) => runtime?.EntryIdText(r, e), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        module = modules.First(m => m.CanHandle(descriptor));
                        context = await module.PrepareAsync(descriptor, documentId, periodKey, registries, ct).ConfigureAwait(false);
                        outcome = await resolver.MatchRowsDetailedAsync(version.Id, t.Instance, ct).ConfigureAwait(false);
                    }

                    var keys = outcome.Matches.Select(m => m.RowKey).ToList();
                    sel = keys.Count;
                    var inputs = await inputBuilder.BuildAsync(t.Instance, keys, periodKey, descriptor, ct, outcome.Read).ConfigureAwait(false);
                    var byRow = inputs.ToDictionary(i => i.SourceRowKey ?? string.Empty, StringComparer.Ordinal);
                    var rows = await ParityComparer.ExecuteRowsAsync(keys, byRow, module, context, false, ct).ConfigureAwait(false);
                    withValues = rows.Values.Count(r => r.Values.Count > 0);
                    values = rows.Values.Sum(r => r.Values.Count);
                    missing = rows.Values.Count(r => r.MissingArguments is { Count: > 0 });
                    stepErr = rows.Values.Count(r => r.HasStepError || r.Error is not null);
                    rejected = rows.Values.Count(r => r.Rejected);
                    missingNames = string.Join(';', rows.Values.Where(r => r.MissingArguments is { Count: > 0 }).SelectMany(r => r.MissingArguments!).Distinct().Order());
                }
                catch (Exception ex) when (ex is DomainException or InvalidOperationException or ArgumentException)
                {
                    error = ex is DomainException de ? de.ErrorCode : ex.GetType().Name;
                }

                csv.Append(string.Join(',', t.Code, t.Period, t.Instance, side, version.Id, engine, sel, withValues, values, missing, stepErr, rejected, error, Q(missingNames))).Append('\n');
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "issues.csv"), csv.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        await output.WriteLineAsync($"issues: targets {targets.Count}").ConfigureAwait(false);
        return MigrationCli.Ok;
    }

    private static int CompareStored(
        (int VersionId, string Code, string Version, string? Script, long Doc, int Period, long Instance) t,
        List<StoredValue> stored, Dictionary<string, ParityRowOutcome> computed, string label, StringBuilder detail)
    {
        var count = 0;
        var storedByRow = stored.GroupBy(s => s.RowKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var (rowKey, values) in storedByRow)
        {
            var row = computed.GetValueOrDefault(rowKey);
            var have = row?.Values.GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.First()) ?? [];
            foreach (var s in values)
            {
                if (!have.TryGetValue(s.Key, out var c))
                {
                    count++;
                    detail.Append(Csv(t, label, rowKey, "value.missing", s.Key, Num(s.Value) + "@" + s.UnitId, row is null ? "row not computed" : row.Error ?? "-"));
                }
                else if (c.Value != s.Value || c.UnitId != s.UnitId)
                {
                    count++;
                    detail.Append(Csv(t, label, rowKey, c.UnitId != s.UnitId ? "value.unit" : "value.number", s.Key, Num(s.Value) + "@" + s.UnitId, Num(c.Value) + "@" + c.UnitId));
                }
            }

            var storedKeys = values.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var extra in have.Keys.Where(k => !storedKeys.Contains(k)))
            {
                count++;
                detail.Append(Csv(t, label, rowKey, "value.extra", extra, "-", Num(have[extra].Value) + "@" + have[extra].UnitId));
            }
        }

        foreach (var (rowKey, row) in computed.Where(r => !storedByRow.ContainsKey(r.Key) && r.Value.Values.Count > 0))
        {
            count++;
            detail.Append(Csv(t, label, rowKey, "row.extra", string.Empty, "-", string.Join(';', row.Values.Select(v => v.Key + "=" + Num(v.Value)))));
        }

        return count;
    }

    private static string Num(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    private static string Csv(
        (int VersionId, string Code, string Version, string? Script, long Doc, int Period, long Instance) t,
        string comparison, string? rowKey, string aspect, string key, string? left, string? right)
        => string.Join(',', t.VersionId, t.Code, t.Version, t.Doc, t.Period, t.Instance, comparison, Q(rowKey), aspect, Q(key), Q(left), Q(right)) + "\n";

    private static string Q(string? s) => "\"" + (s ?? string.Empty).Replace("\"", "\"\"") + "\"";

    private static async Task<int> WaterAsync(IServiceProvider sp, string connection, string outDir, string textPath, int versionId, TextWriter output, CancellationToken ct)
    {
        var preview = sp.GetRequiredService<IScriptPreviewRunner>();
        var text = await File.ReadAllTextAsync(textPath, ct).ConfigureAwait(false);
        var pairs = new List<(long Doc, int Period, long Instance)>();
        await using (var sql = new SqlConnection(connection))
        {
            await sql.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new SqlCommand(
                """
                SELECT ti.DocumentId, ti.PeriodKey, ti.Id FROM doc.TableInstance ti JOIN cfg.TableDef t ON t.Id = ti.TableDefId
                WHERE t.Code = 'T7b_00' AND ti.SheetCopyId IS NULL ORDER BY 1, 2, 3
                """, sql);
            await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
            {
                pairs.Add((rd.GetInt64(0), rd.GetInt32(1), rd.GetInt64(2)));
            }
        }

        var csv = new StringBuilder("doc,period,instance,rowKey,rowNumber,state,issue,name,kind,text,error\n");
        var diagnostics = new StringBuilder();
        foreach (var p in pairs)
        {
            try
            {
                var result = await preview.RunAsync(new ScriptPreviewRequest(versionId, text, p.Doc, p.Period, p.Instance, 500), ct).ConfigureAwait(false);
                foreach (var table in result.Tables)
                {
                    foreach (var row in table.Rows)
                    {
                        if (row.Values.Count == 0)
                        {
                            csv.Append(string.Join(',', p.Doc, p.Period, p.Instance, Q(row.RowKey), row.RowNumber, row.State, row.Issue, "", "", "", Q(string.Join(';', row.Missing ?? [])))).Append('\n');
                        }

                        foreach (var v in row.Values)
                        {
                            csv.Append(string.Join(',', p.Doc, p.Period, p.Instance, Q(row.RowKey), row.RowNumber, row.State, row.Issue, v.Name, v.Kind, Q(v.Text), Q(v.Error))).Append('\n');
                        }
                    }
                }
            }
            catch (DomainException ex)
            {
                diagnostics.Append(CultureInfo.InvariantCulture, $"{p.Doc}/{p.Period}/{p.Instance}: {ex.ErrorCode} {ex.Message}");
                if (ex.Details is { } d)
                {
                    foreach (var kv in d)
                    {
                        diagnostics.Append(CultureInfo.InvariantCulture, $" | {kv.Key}={System.Text.Json.JsonSerializer.Serialize(kv.Value)}");
                    }
                }

                diagnostics.Append('\n');
            }
        }

        var name = Path.GetFileNameWithoutExtension(textPath);
        await File.WriteAllTextAsync(Path.Combine(outDir, $"water-{name}-values.csv"), csv.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outDir, $"water-{name}-diagnostics.txt"), diagnostics.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        await output.WriteLineAsync($"water {name}: instances {pairs.Count}, diagnostics lines {diagnostics.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}").ConfigureAwait(false);
        return MigrationCli.Ok;
    }
}
