// src/Ecr.Application/Calculations/Scripting/Migration/ScriptMigrationHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting.Migration;

/// <summary>Ключі відмов пакета й ліміти перенесення.</summary>
internal static class ScriptMigrationKeys
{
    public const string PackageFormat = "ecr-methodology-script-package";

    /// <summary>Формат пакета збігається з форматом відбитка: змінився відбиток — старі пакети несумісні.</summary>
    public const int SupportedFormatVersion = Fingerprint.FormatVersion;
    public const int MaxEntries = 2_000;
    public const int MaxSwitchVersions = 500;
    public const int MaxReasonLength = 500;
    public const int MaxDetails = 20;

    public const string VersionNotFound = "scriptMigration.refused.versionNotFound";
    public const string DuplicateEntry = "scriptMigration.refused.duplicateEntry";
    public const string EffectiveFromMismatch = "scriptMigration.refused.effectiveFromMismatch";
    public const string NotFormulaVersion = "scriptMigration.refused.notFormulaVersion";
    public const string SourceUnreadable = "scriptMigration.refused.sourceUnreadable";
    public const string FingerprintMismatch = "scriptMigration.refused.fingerprintMismatch";
    public const string TextEmpty = "scriptMigration.refused.textEmpty";
    public const string TextInvalid = "scriptMigration.refused.textInvalid";
    public const string PackageFormatUnsupported = "err.ECR-CALC-0422.migrationPackageFormatUnsupported";

    public static BusinessRuleException Invalid(string messageKey, string message, params (string Key, string Value)[] details)
    {
        var map = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        foreach (var (key, value) in details)
        {
            map[key] = value;
        }

        return new BusinessRuleException("ECR-CALC-0422", message, map);
    }
}

/// <summary>
/// Записує тіньові тексти з пакета <c>pack</c> у версії, що ще працюють на формулах (MS, план 8.5). Відбиток джерела
/// звіряється з БАЗОЮ тим самим алгоритмом, що й в інструменті (<see cref="Fingerprint"/>).
/// </summary>
/// <remarks>
/// ⛔ Рушій версії НЕ змінюється: числа лишаються формульними, текст лежить «тінню» до перемикання
/// (<see cref="SwitchScriptEngineHandler"/>). Версії обробляються незалежно: відмова однієї не зупиняє решти;
/// записані версії (<c>dryRun = false</c>) потрапляють в одну транзакцію з подіями журналу.
/// </remarks>
public sealed class ApplyScriptPackageHandler(
    IMethodologyDraftStore drafts,
    IVersionSourceReader reader,
    ScriptAnalyzer analyzer,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на перенесення: те саме, що й на публікацію.</summary>
    public const string Permission = "Calculation.Publish";

    /// <summary>Звіряє й записує пакет.</summary>
    /// <param name="request">Пакет.</param>
    /// <param name="dryRun"><c>true</c> — лише перевірити.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0422</c> <c>migrationPackageInvalid</c> — пакет порожній чи чужої назви формату;
    /// <c>migrationPackageFormatUnsupported</c> — інший номер формату (зокрема пакети формату 1 до зміни відбитка).</exception>
    public async Task<ApplyScriptPackageResponse> HandleAsync(ApplyScriptPackageRequest request, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не переносить методологій.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        if (request.Format is not null && !string.Equals(request.Format, ScriptMigrationKeys.PackageFormat, StringComparison.Ordinal))
        {
            throw ScriptMigrationKeys.Invalid(
                "err.ECR-CALC-0422.migrationPackageInvalid",
                "Пакет має чужий формат: очікується ecr-methodology-script-package.",
                ("format", request.Format));
        }

        // Номер формату обов'язковий: відбиток джерела в пакеті має ту саму версію канону, що й пакет. Пакет іншого
        // (старішого) формату має відбитки іншого канону — їх не звірити, тож відмова явна, а не «відбиток не збігся».
        if (request.FormatVersion != ScriptMigrationKeys.SupportedFormatVersion)
        {
            var found = request.FormatVersion?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var supported = ScriptMigrationKeys.SupportedFormatVersion.ToString(CultureInfo.InvariantCulture);
            throw ScriptMigrationKeys.Invalid(
                ScriptMigrationKeys.PackageFormatUnsupported,
                $"The package has format version {found}, this server accepts {supported}: make the package again with the current tool (convert, then pack).",
                ("found", found),
                ("supported", supported));
        }

        if (request.Entries is not { Count: > 0 } entries || entries.Count > ScriptMigrationKeys.MaxEntries)
        {
            throw ScriptMigrationKeys.Invalid(
                "err.ECR-CALC-0422.migrationPackageInvalid",
                $"Пакет порожній або має понад {ScriptMigrationKeys.MaxEntries} версій.",
                ("limit", ScriptMigrationKeys.MaxEntries.ToString(CultureInfo.InvariantCulture)));
        }

        var results = new ScriptMigrationEntryResultDto?[entries.Count];
        var staged = new List<(MethodologyVersion Version, string Text, string Fingerprint, int Index)>();
        var overlay = new Dictionary<int, MethodologyScript>();

        // Повтор версії в пакеті — відмова всім, крім першої.
        var first = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!first.Add((entry.MethodologyCode ?? string.Empty) + "\u0001" + (entry.Version ?? string.Empty)))
            {
                results[i] = new ScriptMigrationEntryResultDto(
                    entry.MethodologyCode ?? string.Empty, entry.Version ?? string.Empty, null, "refused",
                    ScriptMigrationKeys.DuplicateEntry, "The package lists this version more than once.", null);
            }
            else
            {
                pending.Add(i);
            }
        }

        // ⚠ Тексти ще не збережені, а версія-імпортер компілюється з бібліотекою пакета: прийняті тексти йдуть у
        // накладку, і версії, що не скомпілювалися, повторюються, доки є поступ (порядок у пакеті не важить).
        var progress = true;
        while (progress && pending.Count > 0)
        {
            progress = false;
            foreach (var index in pending.ToList())
            {
                var outcome = await EvaluateAsync(entries[index], overlay, ct).ConfigureAwait(false);
                results[index] = outcome.Entry;
                if (outcome.Retry)
                {
                    continue;
                }

                pending.Remove(index);
                progress = true;
                if (outcome.Version is { } version)
                {
                    overlay[version.Id] = outcome.Shadow!;
                    if (!dryRun)
                    {
                        staged.Add((version, entries[index].Text, outcome.Fingerprint!, index));
                    }
                }
            }
        }

        if (!dryRun && staged.Count > 0)
        {
            await uow.ExecuteInTransactionAsync(
                async token =>
                {
                    foreach (var (version, text, fingerprint, _) in staged)
                    {
                        var hash = ScriptCompiler.Hash(text);
                        version.SetScript(text, hash);
                        await audit.WritePublicationEventAsync(
                            new PublicationEventRecord(
                                ChangedAt: clock.UtcNow,
                                EntityType: "calc.MethodologyVersion",
                                EntityId: version.Id,
                                ResultDiffJson: JsonSerializer.Serialize(new
                                {
                                    operation = "scriptMigration.apply",
                                    sourceFingerprint = fingerprint,
                                    scriptHash = Convert.ToHexStringLower(hash),
                                }),
                                ChangeReason: "ScriptMigration.apply",
                                ChangedByUserId: userId),
                            token).ConfigureAwait(false);
                    }

                    await uow.SaveChangesAsync(token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            foreach (var (_, _, _, index) in staged)
            {
                results[index] = results[index]! with { Status = "applied" };
            }
        }

        var done = results.Select(r => r!).ToList();
        var applied = done.Count(r => r.Status != "refused");
        return new ApplyScriptPackageResponse(dryRun, applied, done.Count - applied, done);
    }

    private sealed record Outcome(
        ScriptMigrationEntryResultDto Entry,
        bool Retry = false,
        MethodologyVersion? Version = null,
        string? Fingerprint = null,
        MethodologyScript? Shadow = null);

    private async Task<Outcome> EvaluateAsync(
        ScriptPackageEntryDto entry, Dictionary<int, MethodologyScript> overlay, CancellationToken ct)
    {
        var code = entry.MethodologyCode ?? string.Empty;
        var number = entry.Version ?? string.Empty;

        Outcome Refuse(string reason, string message, int? versionId = null, IReadOnlyList<string>? details = null, bool retry = false)
            => new(new ScriptMigrationEntryResultDto(code, number, versionId, "refused", reason, message, details), retry);

        var found = string.IsNullOrWhiteSpace(code) ? null : await drafts.FindByCodeAsync(code, ct).ConfigureAwait(false);
        var methodology = found is null ? null : await drafts.FindAsync(found.Id, ct).ConfigureAwait(false);
        var version = methodology?.Versions.FirstOrDefault(v => string.Equals(v.Version, number, StringComparison.Ordinal));
        if (methodology is null || version is null)
        {
            return Refuse(ScriptMigrationKeys.VersionNotFound, "No such methodology version in the database.");
        }

        if (entry.EffectiveFrom is { Length: > 0 } effective
            && DateOnly.TryParseExact(effective, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var packed)
            && packed != version.EffectiveFrom)
        {
            return Refuse(
                ScriptMigrationKeys.EffectiveFromMismatch,
                "The version's effective date in the database differs from the package.",
                version.Id,
                [$"database: {version.EffectiveFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}", $"package: {effective}"]);
        }

        if (version.Engine != MethodologyEngine.Formulas)
        {
            return Refuse(
                ScriptMigrationKeys.NotFormulaVersion,
                "A shadow script is stored only for versions that still run on formulas.",
                version.Id);
        }

        var loaded = await reader.LoadAsync(version.Id, ct).ConfigureAwait(false);
        if (loaded.HasErrors)
        {
            return Refuse(
                ScriptMigrationKeys.SourceUnreadable,
                "The old content of the version cannot be read completely, so its fingerprint is not trustworthy.",
                version.Id,
                [.. loaded.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(ScriptMigrationKeys.MaxDetails).Select(i => $"{i.Code}: {i.Message}")]);
        }

        var actual = Fingerprint.ComputeHex(loaded.Source);
        if (!string.Equals(actual, entry.SourceFingerprint?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                ScriptMigrationKeys.FingerprintMismatch,
                "The version was changed after the package was made: the source fingerprint differs.",
                version.Id,
                [$"database: {actual}", $"package: {entry.SourceFingerprint}"]);
        }

        if (string.IsNullOrWhiteSpace(entry.Text))
        {
            return Refuse(ScriptMigrationKeys.TextEmpty, "The script text is empty.", version.Id);
        }

        if (entry.Text.Length > ScriptAnalyzer.MaxTextLength)
        {
            return Refuse(
                ScriptMigrationKeys.TextInvalid,
                "The script text is longer than the limit.",
                version.Id,
                [$"limit: {ScriptAnalyzer.MaxTextLength.ToString(CultureInfo.InvariantCulture)} characters"]);
        }

        // Бібліотеки з тіньовим скриптом годяться: текст зберігають ДО перемикання рушія бібліотеки.
        var analysis = await analyzer.AnalyzeAsync(entry.Text, methodology, version, ct, overlay).ConfigureAwait(false);
        if (analysis.Diagnostics.Count > 0)
        {
            return Refuse(
                ScriptMigrationKeys.TextInvalid,
                $"The script has {analysis.Diagnostics.Count.ToString(CultureInfo.InvariantCulture)} diagnostic(s).",
                version.Id,
                [.. analysis.Diagnostics.Take(ScriptMigrationKeys.MaxDetails).Select(d => $"{d.Code} ({d.Line}:{d.Column}) {d.Message}")],
                retry: true);
        }

        var shadow = new MethodologyScript(
            entry.Text, ScriptCompiler.Hash(entry.Text), MethodologyEngine.Formulas,
            methodology.Kind == MethodologyKind.Library, version.NumericMode);
        return new Outcome(
            new ScriptMigrationEntryResultDto(code, number, version.Id, "wouldApply", null, null, null),
            Retry: false,
            version,
            actual,
            shadow);
    }
}

/// <summary>
/// Перемикає рушій групи ОПУБЛІКОВАНИХ версій однією транзакцією (MS, план 8.5). На <c>Script</c> — лише коли текст
/// компілюється без діагностик, а склад, виведений зі скрипта (виходи, речовини, імпорти), ТОЙ САМИЙ, що вже лежить у версії;
/// бібліотека й усі її імпортери — в одній групі. Назад на <c>Formulas</c> — просте перемикання.
/// </summary>
/// <remarks>
/// ⛔ Старі формули, константи й правила не видаляються до етапу 9 — повернення завжди можливе. Рушій версії не
/// кешується ніде: дескриптор береться з бази на кожен прогін, а програма скрипта в <c>ScriptProgramCache</c> має ключ
/// (версія, хеш тексту, підпис бібліотек) — після перемикання нічого скидати не потрібно.
/// </remarks>
public sealed class SwitchScriptEngineHandler(
    IMethodologyStore methodologies,
    IMethodologyDraftStore drafts,
    IScriptMigrationStore migrationStore,
    ScriptAnalyzer analyzer,
    ScriptMaterializer materializer,
    IFormulaLinkRebuilder formulaLinks,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    IDependencyGraphProvider? dependencyGraph = null)
{
    /// <summary>Право на перемикання: те саме, що й на публікацію.</summary>
    public const string Permission = "Calculation.Publish";

    /// <summary>Перемикає групу версій.</summary>
    /// <param name="request">Версії, рушій, причина.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0422</c>: <c>switchReasonRequired</c>, <c>switchEngineInvalid</c>, <c>switchVersionsRequired</c>,
    /// <c>switchGroupIncomplete</c> (немає бібліотеки чи імпортера в запиті), <c>switchChecksFailed</c> (є проблеми);
    /// <c>ECR-CALC-0409</c>: <c>scriptSwitchNotPublished</c> — версія не опублікована.
    /// </exception>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    public async Task<SwitchScriptEngineResponse> HandleAsync(SwitchScriptEngineRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не перемикає рушія методологій.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > ScriptMigrationKeys.MaxReasonLength)
        {
            throw ScriptMigrationKeys.Invalid(
                "err.ECR-CALC-0422.switchReasonRequired",
                $"Причина перемикання обов'язкова (до {ScriptMigrationKeys.MaxReasonLength} символів).",
                ("limit", ScriptMigrationKeys.MaxReasonLength.ToString(CultureInfo.InvariantCulture)));
        }

        if (!Enum.TryParse<MethodologyEngine>(request.Engine, ignoreCase: true, out var engine) || !Enum.IsDefined(engine))
        {
            throw ScriptMigrationKeys.Invalid(
                "err.ECR-CALC-0422.switchEngineInvalid",
                "Рушій має бути Script або Formulas.",
                ("engine", request.Engine ?? string.Empty));
        }

        var ids = (request.VersionIds ?? []).Distinct().ToList();
        if (ids.Count == 0 || ids.Count > ScriptMigrationKeys.MaxSwitchVersions)
        {
            throw ScriptMigrationKeys.Invalid(
                "err.ECR-CALC-0422.switchVersionsRequired",
                $"Потрібно від 1 до {ScriptMigrationKeys.MaxSwitchVersions} версій.",
                ("limit", ScriptMigrationKeys.MaxSwitchVersions.ToString(CultureInfo.InvariantCulture)));
        }

        var group = new List<(Methodology Methodology, MethodologyVersion Version)>();
        foreach (var id in ids)
        {
            var methodology = await methodologies.FindByVersionAsync(id, ct).ConfigureAwait(false)
                ?? throw ScriptHandlerSupport.VersionNotFound(id);
            var version = methodology.Versions.Single(v => v.Id == id);
            if (version.Status is not (TemplateVersionStatus.Published or TemplateVersionStatus.Deprecated))
            {
                throw new BusinessRuleException(
                    "ECR-CALC-0409",
                    $"Версія {methodology.Code} {version.Version} не опублікована: перемикання стосується лише опублікованих.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-CALC-0409.scriptSwitchNotPublished",
                        ["version"] = version.Version,
                    });
            }

            group.Add((methodology, version));
        }

        if (engine == MethodologyEngine.Script)
        {
            await RequireCompleteGroupAsync(group, ct).ConfigureAwait(false);
        }

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                // MS x B179: перемикання переписує ребра довідників і залежності методологій; нове замикання циклу
                // залежностей документів відмовляє (як L3) і відкочує всю групу.
                var groupIds = group.Select(g => g.Methodology.Id).Distinct().ToList();
                var onCycle = await Ecr.Application.Templates.DependencyCyclePublishCheck
                    .MethodologiesOnCycleAsync(dependencyGraph, groupIds, token).ConfigureAwait(false);
                if (engine == MethodologyEngine.Script)
                {
                    await SwitchToScriptAsync(group, token).ConfigureAwait(false);
                }
                else
                {
                    foreach (var (_, version) in group)
                    {
                        version.SwitchEngineForMigration(MethodologyEngine.Formulas);
                        await methodologies.ReplaceScriptReadTablesAsync(version.Id, [], token).ConfigureAwait(false);
                    }

                    // ⛔ F13a: скрипт при матеріалізації переписав ребра методологій і довідників за СВОЇМ текстом; формули
                    // їх виводять із формул. Після повернення стан має збігатися зі станом до перемикання на скрипт, тож
                    // ребра перебудовуються тією ж дорогою, що й публікація формул (після зміни рушія всієї групи).
                    foreach (var (methodology, version) in group)
                    {
                        await formulaLinks.RebuildAsync(methodology, version, token).ConfigureAwait(false);
                    }
                }

                await uow.SaveChangesAsync(token).ConfigureAwait(false);
                await Ecr.Application.Templates.DependencyCyclePublishCheck
                    .RefuseMethodologiesAsync(dependencyGraph, groupIds, onCycle, token).ConfigureAwait(false);

                foreach (var (_, version) in group)
                {
                    await audit.WritePublicationEventAsync(
                        new PublicationEventRecord(
                            ChangedAt: clock.UtcNow,
                            EntityType: "calc.MethodologyVersion",
                            EntityId: version.Id,
                            ResultDiffJson: JsonSerializer.Serialize(new
                            {
                                operation = "scriptMigration.switch",
                                engine = engine.ToString(),
                                group = ids,
                            }),
                            ChangeReason: reason,
                            ChangedByUserId: userId),
                        token).ConfigureAwait(false);
                }

                await uow.SaveChangesAsync(token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return new SwitchScriptEngineResponse(
            engine.ToString(),
            [.. group.Select(g => new SwitchedVersionDto(g.Version.Id, g.Methodology.Code, g.Version.Version))]);
    }

    /// <summary>Бібліотека й усі її імпортери — в одній групі; імпортована бібліотека вже на скрипті або в групі.</summary>
    private async Task RequireCompleteGroupAsync(
        List<(Methodology Methodology, MethodologyVersion Version)> group, CancellationToken ct)
    {
        var inGroup = group.Select(g => g.Version.Id).ToHashSet();
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (methodology, version) in group)
        {
            // Імпортер: КОЖНА версія бібліотеки, яку рантайм може розв'язати для будь-якого періоду життя версії (на дату
            // чинності й на кожну дату виходу нової версії бібліотеки всередині життя; транзитивно через бібліотеки
            // бібліотек), має бути на скрипті або в групі, — інакше історична версія лишилася б на Formulas і прогін
            // періоду дав би scriptLibraryUnavailable.
            var start = version.EffectiveFrom ?? new DateOnly(2000, 1, 1);
            var dates = new SortedSet<DateOnly> { start };
            foreach (var date in await migrationStore.GetLibraryEditionDatesAsync(version.Id, ct).ConfigureAwait(false) ?? [])
            {
                dates.Add(date);
            }

            var seen = new HashSet<int>();
            foreach (var onDate in dates)
            {
                var pending = new Stack<int>();
                pending.Push(version.Id);
                var visited = new HashSet<int>();
                while (pending.TryPop(out var currentId))
                {
                    if (!visited.Add(currentId))
                    {
                        continue;
                    }

                    foreach (var library in await methodologies.ResolveImportsAsync(currentId, onDate, ct).ConfigureAwait(false))
                    {
                        if (library.MethodologyVersionId is not { } libraryVersionId)
                        {
                            continue;
                        }

                        pending.Push(libraryVersionId);
                        if (inGroup.Contains(libraryVersionId) || !seen.Add(libraryVersionId))
                        {
                            continue;
                        }

                        var libraryVersion = await drafts.FindVersionAsync(libraryVersionId, ct).ConfigureAwait(false);
                        if (libraryVersion is { Engine: not MethodologyEngine.Script })
                        {
                            missing.Add($"{library.MethodologyCode} {libraryVersion.Version} (library of {methodology.Code} {version.Version}, id {libraryVersionId.ToString(CultureInfo.InvariantCulture)})");
                        }
                    }
                }
            }

            // Бібліотека: усі її імпортери (ще на формулах) — у групі.
            if (methodology.Kind == MethodologyKind.Library)
            {
                foreach (var importer in await migrationStore.GetImporterVersionsAsync(methodology.Id, ct).ConfigureAwait(false))
                {
                    if (importer.Engine != MethodologyEngine.Script
                        && importer.Status is TemplateVersionStatus.Published or TemplateVersionStatus.Deprecated
                        && !inGroup.Contains(importer.VersionId))
                    {
                        missing.Add($"{importer.MethodologyCode} {importer.Version} (imports {methodology.Code}, id {importer.VersionId.ToString(CultureInfo.InvariantCulture)})");
                    }
                }
            }
        }

        if (missing.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                "A library and all its importers must be switched in one request. Missing: " + string.Join("; ", missing) + ".",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.switchGroupIncomplete",
                    ["count"] = missing.Count.ToString(CultureInfo.InvariantCulture),
                    ["missing"] = missing.ToList(),
                });
        }
    }

    private async Task SwitchToScriptAsync(
        List<(Methodology Methodology, MethodologyVersion Version)> group, CancellationToken ct)
    {
        var problems = new List<PublishProblem>();
        foreach (var (methodology, version) in group)
        {
            if (string.IsNullOrWhiteSpace(version.ScriptText))
            {
                problems.Add(PublishProblem.Of(
                    "switch.problem.scriptMissing",
                    $"Version {methodology.Code} {version.Version} has no script text.",
                    ("code", methodology.Code),
                    ("version", version.Version)));
            }
        }

        Reject(problems);

        // Спершу рушій усієї групи (бібліотеки мають бути на скрипті, щоб імпортери скомпілювалися), потім перевірки.
        foreach (var (_, version) in group)
        {
            version.SwitchEngineForMigration(MethodologyEngine.Script);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        var analyses = new List<(Methodology Methodology, MethodologyVersion Version, ScriptAnalysis Analysis)>();
        foreach (var (methodology, version) in group)
        {
            var analysis = await analyzer.AnalyzeAsync(version.ScriptText!, methodology, version, ct).ConfigureAwait(false);
            problems.AddRange(analysis.Diagnostics.Select(d => Prefixed(methodology, version, ScriptPublishChecks.ToProblem(d))));
            if (analysis.Compilation.Script is { } script)
            {
                var onDate = version.EffectiveFrom ?? new DateOnly(2000, 1, 1);
                problems.AddRange(await materializer
                    .DiffAgainstStoredAsync(version.Id, script, onDate, ct, ScriptAnalyzer.IsModuleLevel(methodology, version))
                    .ConfigureAwait(false));
            }

            analyses.Add((methodology, version, analysis));
        }

        // Будь-яка проблема відкочує всю групу: транзакцію тримає викликач.
        Reject(problems);

        foreach (var (methodology, version, analysis) in analyses)
        {
            await materializer
                .WriteForSwitchAsync(version.Id, methodology.Id, analysis.Compilation.Script!, analysis.Environment!, ct)
                .ConfigureAwait(false);
        }
    }

    private static PublishProblem Prefixed(Methodology methodology, MethodologyVersion version, PublishProblem problem)
        => problem with
        {
            Text = $"[{methodology.Code} {version.Version}] {problem.Text}",
        };

    private static void Reject(List<PublishProblem> problems)
    {
        if (problems.Count == 0)
        {
            return;
        }

        throw new BusinessRuleException(
            "ECR-CALC-0422",
            MethodologyPublishChecks.Describe(problems),
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0422.switchChecksFailed",
                ["count"] = problems.Count.ToString(CultureInfo.InvariantCulture),
                ["problems"] = problems,
            });
    }
}
