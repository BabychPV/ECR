// src/Ecr.Application/Calculations/Scripting/Migration/FinalizeFormulasHandler.cs
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

/// <summary>Версія, яку переносять на скрипт остаточно, і доказ цього тексту.</summary>
/// <param name="VersionId">Версія (рушій <c>Formulas</c>).</param>
/// <param name="ScriptHash">SHA-256 (hex) тексту, на якому доведено паритет; має збігатися з <c>ScriptHash</c> у базі.</param>
/// <param name="Proof">
/// Джерело доказу: <c>reference</c> (рядки старої системи), <c>synthetic</c> (рядки гілок), <c>legacy</c> (результати
/// старої системи), <c>stored</c> (збережені <c>calc.CalculationResult</c> версії відтворено на тих самих входах), <c>noRules</c> (у версії немає правил рядків — ні формули, ні скрипт нічого не рахують),
/// <c>unusedLibrary</c> (бібліотека, яку ніхто не імпортує).
/// </param>
/// <param name="ProvenValues">Скільки значень порівняно з 0 розбіжностей (для <c>reference</c>/<c>synthetic</c>/<c>legacy</c> ≥ 1).</param>
/// <param name="Text">
/// Текст скрипта, що стане текстом версії (convert «повного життя»: чернетки, витіснені версії, тінь яких усічена межею наступної
/// версії); <c>null</c> — береться тіньовий текст із бази. Хеш цього тексту мусить дорівнювати <paramref name="ScriptHash"/>
/// (доведено саме його); попередня тінь іде в журнал.
/// </param>
public sealed record FinalizeFormulasEntryDto(int VersionId, string? ScriptHash, string? Proof, int ProvenValues, string? Text = null);

/// <summary>Запит остаточного перенесення.</summary>
/// <param name="Reason">Причина (у журнал).</param>
/// <param name="Entries">Версії.</param>
public sealed record FinalizeFormulasRequest(string? Reason, IReadOnlyList<FinalizeFormulasEntryDto>? Entries);

/// <summary>Перенесена версія.</summary>
/// <param name="VersionId">Версія.</param>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="Version">Номер версії.</param>
/// <param name="Proof">Джерело доказу.</param>
public sealed record FinalizedVersionDto(int VersionId, string MethodologyCode, string Version, string Proof);

/// <summary>Відповідь остаточного перенесення.</summary>
/// <param name="Finalized">Перенесені версії.</param>
/// <param name="FormulasVersionsLeft">Скільки версій на <c>Formulas</c> лишилось після запиту.</param>
public sealed record FinalizeFormulasResponse(IReadOnlyList<FinalizedVersionDto> Finalized, int FormulasVersionsLeft);

/// <summary>
/// ✎ P-1124-2 (рішення людини 24.11: «перенести систему на нове представлення без втрат … лише наш новий редактор скриптів»):
/// ОДНОРАЗОВЕ остаточне перенесення версій, що ще лишились на рушії <c>Formulas</c> — витіснених новішими, і чернеток —
/// на <c>Script</c> ТИМ САМИМ Id версії (результати й знімки подань і далі посилаються на версію, що дала число).
/// </summary>
/// <remarks>
/// <para>
/// Чим відрізняється від <see cref="SwitchScriptEngineHandler"/> — і лише тут, і лише для НЕ чинних версій: (1) золотий
/// набір PSS-23 не вимагається — доказ дає паритет на рядках (поле <see cref="FinalizeFormulasEntryDto.Proof"/>); (2)
/// діагностики ОТОЧЕННЯ (сьогоднішній шаблон міг уже не мати колонки витісненої версії) не блокують — блокують лише
/// діагностики компіляції; (3) виведену з ужитку (<c>Deprecated</c>) версію теж переносять (заміна PSS-23 §6 п. 9 словом
/// людини 24.11). Чинну версію (за неї нічого новішого не опубліковано) цей шлях не бере ніколи — для неї звичайне
/// перемикання з усіма воротами.
/// </para>
/// <para>
/// ⛔ Що перевіряється для кожної версії (будь-яка відмова — нічого не змінено для всього запиту): рушій ще
/// <c>Formulas</c>; версія не чинна; хеш тексту в базі = хешу доказу = хешу самого тексту; доказ із ≥ 1 порівняним
/// значенням (або перевірений тут <c>noRules</c>/<c>unusedLibrary</c>); текст компілюється; склад, виведений зі скрипта,
/// збігається з наявним. Перед перемиканням вміст формул (канонічне джерело версії і його відбиток) пишеться в незмінний
/// журнал <c>aud.PublicationEvent</c> — це походження для етапу видалення формул.
/// </para>
/// <para>
/// Шлях помирає разом із даними: коли версій на <c>Formulas</c> немає, запит відмовляє (<c>finalizeNothingLeft</c>).
/// Матеріалізація таблиць читання й залежностей для витіснених версій НЕ виконується — вони не рахують жодного періоду, а
/// перезапис ребер методології зі старого тексту зіпсував би граф чинної версії.
/// </para>
/// </remarks>
public sealed class FinalizeFormulasHandler(
    IMethodologyStore methodologies,
    IScriptMigrationStore migrationStore,
    IVersionSourceReader reader,
    ScriptAnalyzer analyzer,
    ScriptMaterializer materializer,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право: те саме, що й на публікацію.</summary>
    public const string Permission = "Calculation.Publish";

    /// <summary>Операція в журналі.</summary>
    public const string Operation = "scriptMigration.finalize";

    /// <summary>Джерела доказу, що потребують ≥ 1 порівняного значення.</summary>
    public static readonly IReadOnlySet<string> ValueProofs = new HashSet<string>(StringComparer.Ordinal) { "reference", "synthetic", "legacy", "stored" };

    /// <summary>Переносить версії.</summary>
    /// <param name="request">Версії, хеші, доказ, причина.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0409</c> <c>finalizeNothingLeft</c> — версій на формулах немає; <c>ECR-CALC-0422</c>
    /// <c>switchReasonRequired</c>, <c>switchVersionsRequired</c>, <c>finalizeRefused</c> (перелік проблем, нічого не змінено).
    /// </exception>
    public async Task<FinalizeFormulasResponse> HandleAsync(FinalizeFormulasRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не переносить методологій.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        // ⛔ Одноразовість: коли формульних версій немає, шлях закритий назавжди.
        if (await migrationStore.CountFormulasVersionsAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                "No methodology version runs on formulas any more: the one-time finalize path is closed.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0409.finalizeNothingLeft" });
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > ScriptMigrationKeys.MaxReasonLength)
        {
            throw ScriptMigrationKeys.Invalid(
                "err.ECR-CALC-0422.switchReasonRequired",
                $"Причина обов'язкова (до {ScriptMigrationKeys.MaxReasonLength} символів).",
                ("limit", ScriptMigrationKeys.MaxReasonLength.ToString(CultureInfo.InvariantCulture)));
        }

        var entries = request.Entries ?? [];
        if (entries.Count == 0 || entries.Count > ScriptMigrationKeys.MaxSwitchVersions
            || entries.Select(e => e.VersionId).Distinct().Count() != entries.Count)
        {
            throw ScriptMigrationKeys.Invalid(
                "err.ECR-CALC-0422.switchVersionsRequired",
                $"Потрібно від 1 до {ScriptMigrationKeys.MaxSwitchVersions} різних версій.",
                ("limit", ScriptMigrationKeys.MaxSwitchVersions.ToString(CultureInfo.InvariantCulture)));
        }

        var problems = new List<string>();
        var staged = new List<(Methodology Methodology, MethodologyVersion Version, FinalizeFormulasEntryDto Entry, string Canonical, string Fingerprint, int EnvironmentDiagnostics)>();
        foreach (var entry in entries)
        {
            var checkedEntry = await CheckAsync(entry, problems, ct).ConfigureAwait(false);
            if (checkedEntry is { } ok)
            {
                staged.Add(ok);
            }
        }

        if (problems.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                "Finalize refused, nothing changed: " + string.Join("; ", problems.Take(ScriptMigrationKeys.MaxDetails)),
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.finalizeRefused",
                    ["count"] = problems.Count.ToString(CultureInfo.InvariantCulture),
                    ["problems"] = problems.Take(ScriptMigrationKeys.MaxDetails).ToList(),
                });
        }

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                foreach (var (methodology, version, entry, canonical, fingerprint, environmentDiagnostics) in staged)
                {
                    // Текст із запиту (хеш уже звірено з доведеним) стає текстом версії; попередня тінь — у журнал. Дозволено, поки рушій Formulas.
                    var previousShadow = ScriptAnalyzer.Hex(version.ScriptHash);
                    if (entry.Text is not null && !string.Equals(entry.Text, version.ScriptText, StringComparison.Ordinal))
                    {
                        version.SetScript(entry.Text, ScriptCompiler.Hash(entry.Text));
                    }

                    // Походження ПЕРЕД перемиканням, у тій самій транзакції: без нього формули не можна видаляти.
                    await audit.WritePublicationEventAsync(
                        new PublicationEventRecord(
                            ChangedAt: clock.UtcNow,
                            EntityType: "calc.MethodologyVersion",
                            EntityId: version.Id,
                            ResultDiffJson: JsonSerializer.Serialize(new
                            {
                                operation = Operation,
                                decision = "P-1124-2",
                                methodologyCode = methodology.Code,
                                version = version.Version,
                                status = version.Status.ToString(),
                                proof = entry.Proof,
                                provenValues = entry.ProvenValues,
                                scriptHash = ScriptAnalyzer.Hex(version.ScriptHash),
                                previousShadowHash = previousShadow,
                                contentHash = ScriptAnalyzer.Hex(version.ContentHash),
                                environmentDiagnostics,
                                sourceFingerprint = fingerprint,
                                formulaSource = canonical,
                            }),
                            ChangeReason: reason,
                            ChangedByUserId: userId),
                        token).ConfigureAwait(false);

                    if (version.Status == TemplateVersionStatus.Draft)
                    {
                        version.SwitchEngine(MethodologyEngine.Script);
                    }
                    else
                    {
                        version.SwitchEngineForMigration(MethodologyEngine.Script);
                    }
                }

                await uow.SaveChangesAsync(token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        var left = await migrationStore.CountFormulasVersionsAsync(ct).ConfigureAwait(false);
        return new FinalizeFormulasResponse(
            [.. staged.Select(s => new FinalizedVersionDto(s.Version.Id, s.Methodology.Code, s.Version.Version, s.Entry.Proof!))],
            left);
    }

    private async Task<(Methodology, MethodologyVersion, FinalizeFormulasEntryDto, string, string, int)?> CheckAsync(
        FinalizeFormulasEntryDto entry, List<string> problems, CancellationToken ct)
    {
        var id = entry.VersionId.ToString(CultureInfo.InvariantCulture);
        var methodology = await methodologies.FindByVersionAsync(entry.VersionId, ct).ConfigureAwait(false);
        var version = methodology?.Versions.FirstOrDefault(v => v.Id == entry.VersionId);
        if (methodology is null || version is null)
        {
            problems.Add($"{id}: version not found");
            return null;
        }

        var name = $"{methodology.Code} {version.Version} ({id})";
        if (version.Engine != MethodologyEngine.Formulas)
        {
            problems.Add($"{name}: not on formulas");
            return null;
        }

        // ⛔ Лише НЕ чинні: чернетка, виведена з ужитку або витіснена новішою опублікованою версією тієї самої методології.
        if (IsCurrent(methodology, version))
        {
            problems.Add($"{name}: the version is current (nothing newer is published) - use the normal switch with all gates");
            return null;
        }

        // ⛔ Доведено саме цей текст: переданий у запиті (він і буде текстом версії), інакше — тіньовий у базі.
        // ✎ P-1124-2: тінь витісненої версії, записана після появи наступної, усічена межею життя (константи з періодами
        // чинності — лише до наступної версії), а версія рахувала й пізніші періоди, поки була чинною (C08_04_02 V1). Тому
        // finalize бере текст «повного життя» із запиту — і лише доведений: хеш тексту = хешу доказу.
        var stored = entry.Text is null;
        var text = stored ? version.ScriptText : entry.Text;
        if (string.IsNullOrWhiteSpace(text) || (stored && version.ScriptHash is null))
        {
            problems.Add($"{name}: no script text");
            return null;
        }

        var actual = Convert.ToHexStringLower(ScriptCompiler.Hash(text));
        if ((stored && !string.Equals(actual, ScriptAnalyzer.Hex(version.ScriptHash), StringComparison.Ordinal))
            || !string.Equals(actual, entry.ScriptHash?.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            problems.Add($"{name}: script hash differs from the proven one (stored {ScriptAnalyzer.Hex(version.ScriptHash)}, text {actual}, proven {entry.ScriptHash})");
            return null;
        }

        var loaded = await reader.LoadAsync(version.Id, ct).ConfigureAwait(false);
        if (loaded.HasErrors)
        {
            problems.Add($"{name}: the formula source cannot be read completely (provenance would be incomplete)");
            return null;
        }

        // ⛔ Доказ.
        var proof = entry.Proof?.Trim() ?? string.Empty;
        var proven = proof switch
        {
            _ when ValueProofs.Contains(proof) => entry.ProvenValues >= 1,
            "noRules" => loaded.Source.Rules.Count == 0 && loaded.Source.RowsClauses.Count == 0 && methodology.Kind != MethodologyKind.Library,
            "unusedLibrary" => methodology.Kind == MethodologyKind.Library
                               && (await migrationStore.GetImporterVersionsAsync(methodology.Id, ct).ConfigureAwait(false)).Count == 0,
            _ => false,
        };
        if (!proven)
        {
            problems.Add($"{name}: not proven (proof '{proof}', values {entry.ProvenValues.ToString(CultureInfo.InvariantCulture)})");
            return null;
        }

        // ⛔ Компілюється; оточення сьогоднішнього шаблону для витісненої версії — лише в журнал.
        var analysis = await analyzer.AnalyzeAsync(text, methodology, version, ct).ConfigureAwait(false);
        if (analysis.Compilation.Script is not { } script || analysis.Compilation.Diagnostics.Count > 0)
        {
            var first = analysis.Compilation.Diagnostics.Count > 0 ? analysis.Compilation.Diagnostics[0] : null;
            problems.Add($"{name}: the script does not compile ({first?.Code} {first?.Message})");
            return null;
        }

        var onDate = version.EffectiveFrom ?? DateOnly.FromDateTime(clock.UtcNow);
        var diff = await materializer
            .DiffAgainstStoredAsync(version.Id, script, onDate, ct, ScriptAnalyzer.IsModuleLevel(methodology, version))
            .ConfigureAwait(false);
        if (diff.Count > 0)
        {
            problems.Add($"{name}: composition derived from the script differs from the stored one ({diff[0].Text})");
            return null;
        }

        var environmentDiagnostics = analysis.Diagnostics.Count - analysis.Compilation.Diagnostics.Count;
        return (methodology, version, entry, Fingerprint.Canonical(loaded.Source), Fingerprint.ComputeHex(loaded.Source), environmentDiagnostics);
    }

    /// <summary>
    /// Чинна: опублікована, і в методології немає іншої опублікованої версії з пізнішою датою чинності (тобто за неї
    /// нічого новішого не опубліковано). Чернетка й виведена з ужитку — не чинні.
    /// </summary>
    /// <param name="methodology">Методологія з версіями.</param>
    /// <param name="version">Версія.</param>
    public static bool IsCurrent(Methodology methodology, MethodologyVersion version)
    {
        ArgumentNullException.ThrowIfNull(methodology);
        ArgumentNullException.ThrowIfNull(version);

        if (version.Status != TemplateVersionStatus.Published)
        {
            return false;
        }

        var start = version.EffectiveFrom ?? DateOnly.MinValue;
        return !methodology.Versions.Any(v => v.Id != version.Id
                                              && v.Status == TemplateVersionStatus.Published
                                              && (v.EffectiveFrom ?? DateOnly.MinValue) > start);
    }
}
