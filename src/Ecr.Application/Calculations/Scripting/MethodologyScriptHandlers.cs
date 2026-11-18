// src/Ecr.Application/Calculations/Scripting/MethodologyScriptHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>Усі діагностики тексту версії (компілятор + оточення) і структура скрипта.</summary>
/// <param name="Compilation">Результат компіляції.</param>
/// <param name="Environment">Перевірка оточення; <c>null</c> — скрипт не розібрано.</param>
/// <param name="Diagnostics">Усі діагностики за позицією.</param>
/// <param name="Outline">Структура.</param>
public sealed record ScriptAnalysis(
    ScriptCompilation Compilation,
    ScriptEnvironment? Environment,
    IReadOnlyList<ScriptDiagnostic> Diagnostics,
    ScriptOutlineDto Outline);

/// <summary>Аналіз тексту скрипта версії: компіляція, перевірки оточення, структура.</summary>
public sealed class ScriptAnalyzer(ScriptSourceCompiler compiler, ScriptEnvironmentChecker checker, IClock clock)
{
    /// <summary>Аналізує текст у контексті версії.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="methodology">Методологія-власник.</param>
    /// <param name="version">Версія (режим арифметики, дата для бібліотек).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="shadowScripts">
    /// Перенесення: бібліотеки з тіньовим скриптом теж годяться, а ще не збережені тексти версій беруться звідси;
    /// <c>null</c> — звичайне авторство.
    /// </param>
    public async Task<ScriptAnalysis> AnalyzeAsync(
        string text,
        Methodology methodology,
        MethodologyVersion version,
        CancellationToken ct,
        IReadOnlyDictionary<int, MethodologyScript>? shadowScripts = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(methodology);
        ArgumentNullException.ThrowIfNull(version);

        var onDate = version.EffectiveFrom ?? DateOnly.FromDateTime(clock.UtcNow);
        var compilation = await compiler
            .CompileAsync(
                text, methodology.Code, version.NumericMode, methodology.Kind == MethodologyKind.Library, onDate, ct,
                shadowScripts, IsModuleLevel(methodology, version))
            .ConfigureAwait(false);

        var diagnostics = new List<ScriptDiagnostic>(compilation.Diagnostics);
        ScriptEnvironment? environment = null;
        if (compilation.Script is not null)
        {
            environment = await checker.CheckAsync(compilation.Script, text, methodology.Id, ct).ConfigureAwait(false);
            diagnostics.AddRange(environment.Diagnostics);
        }

        var sorted = diagnostics
            .Select((d, i) => (d, i)).OrderBy(x => x.d.Line).ThenBy(x => x.d.Column).ThenBy(x => x.i).Select(x => x.d)
            .ToList();
        return new ScriptAnalysis(compilation, environment, sorted, ScriptOutlineDto.From(compilation.Script));
    }

    /// <summary>
    /// Версія рівня <c>Module</c> (HSE400): її скрипт без речення <c>rows</c> (набір аргументів складає модуль). Бібліотека
    /// ніколи не є модульною.
    /// </summary>
    /// <param name="methodology">Методологія-власник.</param>
    /// <param name="version">Версія.</param>
    public static bool IsModuleLevel(Methodology methodology, MethodologyVersion version)
    {
        ArgumentNullException.ThrowIfNull(methodology);
        ArgumentNullException.ThrowIfNull(version);
        return methodology.Kind != MethodologyKind.Library && version.Level == CalculationLevel.Module;
    }

    /// <summary>
    /// Найбільший текст скрипта, знаків. Скрипт компілюється кілька разів за запит (імпорти, перевірки оточення), тож
    /// без межі один запит із десятками мегабайтів тримав би процесор і пам'ять; реальні методології — десятки КБ.
    /// </summary>
    public const int MaxTextLength = 1_000_000;

    /// <summary>Відмовляє, якщо текст довший за <see cref="MaxTextLength"/>.</summary>
    /// <param name="text">Текст скрипта (чи літерал константи).</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0422</c> <c>scriptTooLarge</c>.</exception>
    public static void RequireTextSize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > MaxTextLength)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Текст скрипта завеликий: понад {MaxTextLength.ToString(CultureInfo.InvariantCulture)} знаків.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.scriptTooLarge",
                    ["limit"] = MaxTextLength.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>Hex малими літерами; порожній рядок — хешу немає.</summary>
    /// <param name="hash">Хеш.</param>
    public static string Hex(byte[]? hash) => hash is null ? string.Empty : Convert.ToHexStringLower(hash);
}

/// <summary>Спільні кроки обробників скрипта.</summary>
internal static class ScriptHandlerSupport
{
    public static NotFoundException VersionNotFound(int versionId)
        => new(
            "ECR-CALC-0404",
            $"Версії методології {versionId} не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0404.version",
                ["methodologyVersionId"] = versionId.ToString(CultureInfo.InvariantCulture),
            });

    public static async Task<(Methodology Methodology, MethodologyVersion Version)> LoadAsync(
        IMethodologyDraftStore drafts, int versionId, CancellationToken ct)
    {
        var version = await drafts.FindVersionAsync(versionId, ct).ConfigureAwait(false)
            ?? throw VersionNotFound(versionId);
        var methodology = await drafts.FindAsync(version.MethodologyId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404",
                $"Методології {version.MethodologyId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.methodology",
                    ["methodologyId"] = version.MethodologyId.ToString(CultureInfo.InvariantCulture),
                });
        return (methodology, version);
    }
}

/// <summary>Читає скрипт версії для редактора: текст, хеш, діагностики, структура.</summary>
public sealed class GetMethodologyScriptHandler(
    IMethodologyDraftStore drafts,
    ScriptAnalyzer analyzer,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання скрипта.</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Віддає скрипт версії.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    public async Task<MethodologyScriptDto> HandleAsync(int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var (methodology, version) = await ScriptHandlerSupport.LoadAsync(drafts, methodologyVersionId, ct).ConfigureAwait(false);
        var text = version.ScriptText ?? string.Empty;

        // Діагностики — лише для версії-скрипта: у формульної версії текст (якщо є) — «тіньовий», перевіряти його
        // за правилами скрипта до перенесення нема чого.
        if (version.Engine != MethodologyEngine.Script || text.Length == 0)
        {
            return new MethodologyScriptDto(
                version.Engine, text, ScriptAnalyzer.Hex(version.ScriptHash),
                version.Status == TemplateVersionStatus.Draft, [], ScriptOutlineDto.Empty);
        }

        var analysis = await analyzer.AnalyzeAsync(text, methodology, version, ct).ConfigureAwait(false);
        return new MethodologyScriptDto(
            version.Engine, text, ScriptAnalyzer.Hex(version.ScriptHash), version.Status == TemplateVersionStatus.Draft,
            [.. analysis.Diagnostics.Select(ScriptDiagnosticDto.From)], analysis.Outline);
    }
}

/// <summary>Зберігає текст скрипта чернетки; матеріалізує, коли діагностик немає.</summary>
public sealed class SaveMethodologyScriptHandler(
    IMethodologyDraftStore drafts,
    ScriptAnalyzer analyzer,
    ScriptMaterializer materializer,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на правку скрипта.</summary>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Зберігає скрипт.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка з рушієм <c>Script</c>.</param>
    /// <param name="text">Новий текст.</param>
    /// <param name="expectedHash">Хеш, з якого редактор виходив; <c>null</c> — без звірки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0409</c>: <c>scriptNotEditable</c> — не чернетка чи не скрипт; <c>scriptChanged</c> — хеш не збігся.
    /// <c>ECR-CALC-0422</c>: <c>scriptEmpty</c> — порожній текст.
    /// </exception>
    public async Task<MethodologyScriptDto> HandleAsync(
        int methodologyVersionId, string text, string? expectedHash, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(text);
        ScriptAnalyzer.RequireTextSize(text);

        var (methodology, version) = await ScriptHandlerSupport.LoadAsync(drafts, methodologyVersionId, ct).ConfigureAwait(false);

        if (version.Status != TemplateVersionStatus.Draft || version.Engine != MethodologyEngine.Script)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Версія {version.Version} не є чернеткою зі скриптом: текст не редагується.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0409.scriptNotEditable",
                    ["version"] = version.Version,
                });
        }

        var current = ScriptAnalyzer.Hex(version.ScriptHash);
        if (!string.IsNullOrEmpty(expectedHash)
            && !string.Equals(expectedHash.Trim(), current, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Скрипт версії {version.Version} змінено після того, як ви його відкрили.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0409.scriptChanged",
                    ["version"] = version.Version,
                    ["hash"] = current,
                });
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Версія {version.Version}: текст скрипта порожній.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.scriptEmpty",
                    ["version"] = version.Version,
                });
        }

        var hash = ScriptCompiler.Hash(text);
        var analysis = await analyzer.AnalyzeAsync(text, methodology, version, ct).ConfigureAwait(false);

        // Текст зберігається ЗАВЖДИ — автор не губить напівнаписане; матеріалізація — лише з чистим текстом.
        version.SetScript(text, hash);
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                if (analysis.Diagnostics.Count == 0 && analysis.Compilation.Script is { } script && analysis.Environment is { } env)
                {
                    await materializer
                        .MaterializeAsync(version.Id, methodology.Id, script, env, new ScriptMaterializeOptions(Publish: false), token)
                        .ConfigureAwait(false);
                }

                await uow.SaveChangesAsync(token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return new MethodologyScriptDto(
            version.Engine, text, ScriptAnalyzer.Hex(hash), IsDraft: true,
            [.. analysis.Diagnostics.Select(ScriptDiagnosticDto.From)], analysis.Outline);
    }
}

/// <summary>Перевіряє текст без збереження.</summary>
public sealed class ValidateMethodologyScriptHandler(
    IMethodologyDraftStore drafts,
    ScriptAnalyzer analyzer,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на перевірку скрипта.</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Повертає діагностики й структуру тексту.</summary>
    /// <param name="methodologyVersionId">Версія (режими, дата для бібліотек).</param>
    /// <param name="text">Текст.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ScriptValidationDto> HandleAsync(int methodologyVersionId, string text, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(text);
        ScriptAnalyzer.RequireTextSize(text);

        var (methodology, version) = await ScriptHandlerSupport.LoadAsync(drafts, methodologyVersionId, ct).ConfigureAwait(false);
        var analysis = await analyzer.AnalyzeAsync(text, methodology, version, ct).ConfigureAwait(false);
        return new ScriptValidationDto([.. analysis.Diagnostics.Select(ScriptDiagnosticDto.From)], analysis.Outline);
    }
}

/// <summary>Канонічний друк літерала константи (редактор таблиць коефіцієнтів, MS 13.5).</summary>
public sealed class FormatScriptConstHandler(IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право на форматування.</summary>
    public const string Permission = "Calculation.View";

    private const string Name = "C";

    /// <summary>Розбирає літерал константи й друкує його в канонічному вигляді.</summary>
    /// <param name="literal">Літерал: <c>5</c>, <c>"text"</c>, <c>{ A: 1, B: { x: 2 } }</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<FormatConstDto> HandleAsync(string literal, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(literal);
        ScriptAnalyzer.RequireTextSize(literal);

        // Літерал — на власному рядку після службового заголовка: номери рядків діагностик зсуваються рівно на один,
        // колонки лишаються тими, що у вхідному тексті.
        var compilation = ScriptCompiler.Compile(
            $"const {Name} =\n{literal};", new ScriptCompileOptions(NumericMode.Strict, IsLibrary: true));

        var diagnostics = compilation.Diagnostics
            .Where(d => d.Line >= 2)
            .Select(d => ScriptDiagnosticDto.From(new ScriptDiagnostic(
                d.Code, d.Line - 1, d.Column, d.Length, d.Message, d.Params)))
            .ToList();

        if (compilation.Script is { } script
            && script.Constants.TryGetValue(Name, out var value)
            && compilation.Diagnostics.Count == 0)
        {
            return new FormatConstDto(ScriptFormatter.FormatConst(value), value.IsMap ? "map" : "scalar", []);
        }

        return new FormatConstDto(null, null, diagnostics);
    }
}

/// <summary>
/// Перегляд скрипта на документі: нічого не пише. Право, доступ до документа, компіляція й перевірки — тут;
/// сам прогін — <see cref="IScriptPreviewRunner"/> рушія.
/// </summary>
public sealed class PreviewMethodologyScriptHandler(
    IMethodologyDraftStore drafts,
    ScriptAnalyzer analyzer,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IScriptPreviewRunner? runner = null)
{
    /// <summary>Право на перегляд — читання результатів, як і для самих результатів документа.</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Типова кількість рядків з таблиці.</summary>
    public const int DefaultMaxRows = 50;

    /// <summary>Найбільша кількість рядків з таблиці.</summary>
    public const int MaxRowsLimit = 200;

    /// <summary>Проганяє текст на документі.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або документа (для цього користувача) немає.</exception>
    /// <exception cref="AccessDeniedException">Профіль із обмеженим доступом до колонок.</exception>
    public async Task<ScriptPreviewDto> HandleAsync(
        int methodologyVersionId, PreviewMethodologyScriptRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Той самий доступ, що й до результатів розрахунку документа (`GetCalculationResultsHandler`).
        var profile = await PermissionCheck.RequireInAnyProjectAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, request.DocumentId, Permission, ct).ConfigureAwait(false);

        // ⛔ Перегляд показує значення входів рядків: читач із вужчим доступом до колонок їх бачити не може.
        if (DocumentSheetVisibility.HasRestrictions(profile))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403",
                "Перегляд скрипта на документі недоступний із обмеженим доступом до колонок.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0403.scriptPreviewRestricted" });
        }

        var key = new PeriodKey(request.PeriodKey);
        if (!key.IsValid || key.Sequence > 12)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Ключ періоду {request.PeriodKey} не є місяцем: очікується РРРРММ, напр. 202609.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.simulatePeriodInvalid",
                    ["periodKey"] = request.PeriodKey.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (request.Text is not null)
        {
            ScriptAnalyzer.RequireTextSize(request.Text);
        }

        var (methodology, version) = await ScriptHandlerSupport.LoadAsync(drafts, methodologyVersionId, ct).ConfigureAwait(false);
        var text = string.IsNullOrWhiteSpace(request.Text) ? version.ScriptText : request.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Версія {version.Version}: текст скрипта порожній.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.scriptEmpty",
                    ["version"] = version.Version,
                });
        }

        var analysis = await analyzer.AnalyzeAsync(text, methodology, version, ct).ConfigureAwait(false);
        var diagnostics = analysis.Diagnostics.Select(ScriptDiagnosticDto.From).ToList();
        if (analysis.Diagnostics.Count > 0)
        {
            return new ScriptPreviewDto([], diagnostics);
        }

        // Рушій перегляду підключає проєкт розрахунків; без нього перегляд недоступний, а не «порожній».
        if (runner is null)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                "Перегляд скрипта на документі в цьому складанні недоступний: рушій розрахунків не підключено.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0422.scriptPreviewUnavailable" });
        }

        var maxRows = Math.Clamp(request.MaxRows ?? DefaultMaxRows, 1, MaxRowsLimit);
        var result = await runner
            .RunAsync(
                new ScriptPreviewRequest(
                    methodologyVersionId, text, request.DocumentId, request.PeriodKey, request.TableInstanceId, maxRows),
                ct)
            .ConfigureAwait(false);

        return new ScriptPreviewDto(result.Tables, diagnostics);
    }
}

/// <summary>
/// Паритет версії на документі: чи дає збережений (тіньовий) скрипт ті самі рядки й числа, що й рушій формул.
/// Нічого не пише. Право, доступ до документа, версія й текст — тут; сам прогін — <see cref="IScriptParityRunner"/> рушія.
/// </summary>
public sealed class ParityMethodologyScriptHandler(
    IMethodologyDraftStore drafts,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IScriptParityRunner? runner = null)
{
    /// <summary>Право на перевірку паритету — те саме, що й на перемикання рушія.</summary>
    public const string Permission = "Calculation.Publish";

    /// <summary>Найбільше розбіжностей у відповіді.</summary>
    public const int MaxMismatches = 500;

    /// <summary>Порівнює шляхи версії на документі й періоді.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="request">Документ і період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або документа (для цього користувача) немає.</exception>
    /// <exception cref="AccessDeniedException">Профіль із обмеженим доступом до колонок.</exception>
    /// <exception cref="BusinessRuleException">Період не місяць або версія без тексту.</exception>
    public async Task<ScriptParityDto> HandleAsync(
        int methodologyVersionId, ScriptParityRequestDto request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await PermissionCheck.RequireInAnyProjectAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, request.DocumentId, Permission, ct).ConfigureAwait(false);

        // ⛔ Розбіжності показують значення входів рядків: читач із вужчим доступом до колонок їх бачити не може.
        if (DocumentSheetVisibility.HasRestrictions(profile))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403",
                "Паритет скрипта на документі недоступний із обмеженим доступом до колонок.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0403.scriptPreviewRestricted" });
        }

        var key = new PeriodKey(request.PeriodKey);
        if (!key.IsValid || key.Sequence > 12)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Ключ періоду {request.PeriodKey} не є місяцем: очікується РРРРММ, напр. 202609.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.simulatePeriodInvalid",
                    ["periodKey"] = request.PeriodKey.ToString(CultureInfo.InvariantCulture),
                });
        }

        var (_, version) = await ScriptHandlerSupport.LoadAsync(drafts, methodologyVersionId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(version.ScriptText))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Версія {version.Version}: текст скрипта порожній.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.scriptEmpty",
                    ["version"] = version.Version,
                });
        }

        if (runner is null)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                "Паритет скрипта на документі в цьому складанні недоступний: рушій розрахунків не підключено.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0422.scriptPreviewUnavailable" });
        }

        var result = await runner
            .RunAsync(new ScriptParityRequest(methodologyVersionId, version.ScriptText, request.DocumentId, request.PeriodKey), ct)
            .ConfigureAwait(false);

        return new ScriptParityDto(
            result.Targets,
            result.RowsCompared,
            result.ValuesCompared,
            result.Mismatches.Count,
            result.Mismatches.Count > MaxMismatches,
            [.. result.Mismatches.Take(MaxMismatches)]);
    }
}
