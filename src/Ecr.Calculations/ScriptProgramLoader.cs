using System.Text;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Scripting;

namespace Ecr.Calculations;

/// <summary>Бібліотека, завантажена для прогону: її програма, версія й власні імпорти.</summary>
/// <param name="Code">Код методології-бібліотеки (він же ім'я в <c>import</c>).</param>
/// <param name="VersionId">Версія бібліотеки, чинна на дату кінця періоду.</param>
/// <param name="Script">Скомпільована програма.</param>
/// <param name="Imports">Бібліотеки, які імпортує сама бібліотека, за кодом.</param>
/// <param name="HashHex">Хеш тексту бібліотеки (16-кове подання) — частина підпису кешу.</param>
internal sealed record LibraryProgram(
    string Code,
    int VersionId,
    CompiledScript Script,
    IReadOnlyDictionary<string, LibraryProgram> Imports,
    string HashHex);

/// <summary>Програма версії разом із бібліотеками.</summary>
/// <param name="Main">Скрипт версії.</param>
/// <param name="Libraries">Бібліотеки версії за кодом.</param>
/// <param name="NumericMode">Режим арифметики версії.</param>
internal sealed record LoadedProgram(
    CompiledScript Main, IReadOnlyDictionary<string, LibraryProgram> Libraries, NumericMode NumericMode)
{
    /// <summary>Усі бібліотеки (транзитивно), кожна по одному разу.</summary>
    public IEnumerable<LibraryProgram> AllLibraries()
    {
        var seen = new HashSet<int>();
        var pending = new Stack<LibraryProgram>(Libraries.Values);
        while (pending.TryPop(out var library))
        {
            if (!seen.Add(library.VersionId))
            {
                continue;
            }

            yield return library;
            foreach (var nested in library.Imports.Values)
            {
                pending.Push(nested);
            }
        }
    }
}

/// <summary>
/// Читає текст скрипта версії й бібліотек, на які вона посилається, і компілює (через <see cref="ScriptProgramCache"/>).
/// </summary>
/// <remarks>
/// ⚠ Бібліотека — версія, чинна на дату кінця періоду (<see cref="IMethodologyStore.ResolveImportsAsync"/>, та
/// сама вісь, що в констант і довідників). Бібліотека без версії, без скрипта або не на рушії <c>Script</c> —
/// <c>ECR-CALC-0422 scriptLibraryUnavailable</c>: рахувати «без неї» означало б мовчки видати інші числа.
/// </remarks>
internal sealed class ScriptProgramLoader(IMethodologyStore methodologies, ScriptProgramCache cache)
{
    /// <summary>Найбільша глибина вкладення бібліотек (план §3.8).</summary>
    internal const int MaxLibraryDepth = 8;

    /// <summary>Завантажує програму версії.</summary>
    /// <param name="methodology">Версія методології.</param>
    /// <param name="onDate">Бізнес-дата: кінець періоду.</param>
    /// <param name="textOverride">Текст замість збереженого (живий перегляд); <c>null</c> — збережений.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="importResolver">
    /// Імпорти ТЕКСТУ замість матеріалізованих (живий перегляд незбереженого тексту); <c>null</c> — збережені імпорти версії.
    /// </param>
    public async Task<LoadedProgram> LoadAsync(
        MethodologyDescriptor methodology,
        DateOnly onDate,
        string? textOverride,
        Func<DateOnly, CancellationToken, Task<IReadOnlyList<MethodologyLibrary>>>? importResolver,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodology);

        var script = await methodologies.GetScriptAsync(methodology.MethodologyVersionId, ct).ConfigureAwait(false);
        var text = textOverride ?? script?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Failure(
                "err.ECR-CALC-0422.scriptMissing",
                $"Версія {methodology.Code} {methodology.VersionNumber} не має тексту скрипта.",
                ("code", methodology.Code));
        }

        var mode = script?.NumericMode ?? methodology.NumericMode;
        IReadOnlyList<MethodologyLibrary>? topImports = importResolver is null || textOverride is null
            ? null
            : await importResolver(onDate, ct).ConfigureAwait(false);
        var libraries = await LoadLibrariesAsync(
                methodology.MethodologyVersionId, onDate, 1, [methodology.Code], topImports, ct)
            .ConfigureAwait(false);

        var hash = textOverride is null && script?.Hash is { } stored ? stored : ScriptCompiler.Hash(text);
        var key = ScriptProgramCache.Key(methodology.MethodologyVersionId, hash, Signature(libraries));
        var compiled = cache.GetOrAdd(
            key,
            () => CompileOrThrow(
                text,
                new ScriptCompileOptions(mode, script?.IsLibrary ?? false, code => libraries.GetValueOrDefault(code)?.Script),
                methodology.Code));

        return new LoadedProgram(compiled, libraries, mode);
    }

    private async Task<Dictionary<string, LibraryProgram>> LoadLibrariesAsync(
        int versionId,
        DateOnly onDate,
        int depth,
        IReadOnlyList<string> chain,
        IReadOnlyList<MethodologyLibrary>? importsOverride,
        CancellationToken ct)
    {
        var result = new Dictionary<string, LibraryProgram>(StringComparer.Ordinal);
        var imports = importsOverride
            ?? await methodologies.ResolveImportsAsync(versionId, onDate, ct).ConfigureAwait(false);
        if (imports.Count == 0)
        {
            return result;
        }

        if (depth > MaxLibraryDepth)
        {
            throw Failure(
                "err.ECR-CALC-0422.scriptLibraryUnavailable",
                $"Бібліотеки вкладені глибше за {MaxLibraryDepth}.",
                ("library", chain[^1]));
        }

        foreach (var import in imports)
        {
            if (chain.Contains(import.MethodologyCode, StringComparer.Ordinal))
            {
                throw Failure(
                    "err.ECR-CALC-0422.scriptLibraryUnavailable",
                    $"Бібліотека {import.MethodologyCode} імпортується циклічно.",
                    ("library", import.MethodologyCode));
            }

            if (import.MethodologyVersionId is not { } libraryVersionId
                || await methodologies.GetScriptAsync(libraryVersionId, ct).ConfigureAwait(false) is not { } libraryScript
                || libraryScript.Engine != MethodologyEngine.Script
                || string.IsNullOrWhiteSpace(libraryScript.Text))
            {
                throw Failure(
                    "err.ECR-CALC-0422.scriptLibraryUnavailable",
                    $"Бібліотека {import.MethodologyCode} не має чинної версії зі скриптом на дату {onDate:yyyy-MM-dd}.",
                    ("library", import.MethodologyCode));
            }

            var nested = await LoadLibrariesAsync(libraryVersionId, onDate, depth + 1, [.. chain, import.MethodologyCode], null, ct)
                .ConfigureAwait(false);
            var hash = libraryScript.Hash ?? ScriptCompiler.Hash(libraryScript.Text);
            var key = ScriptProgramCache.Key(libraryVersionId, hash, Signature(nested));
            var compiled = cache.GetOrAdd(
                key,
                () => CompileOrThrow(
                    libraryScript.Text,
                    new ScriptCompileOptions(
                        libraryScript.NumericMode, IsLibrary: true, code => nested.GetValueOrDefault(code)?.Script),
                    import.MethodologyCode));

            result[import.MethodologyCode] = new LibraryProgram(
                import.MethodologyCode, libraryVersionId, compiled, nested, Convert.ToHexString(hash));
        }

        return result;
    }

    private static string Signature(IReadOnlyDictionary<string, LibraryProgram> libraries)
    {
        if (libraries.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var library in libraries.Values.OrderBy(l => l.Code, StringComparer.Ordinal))
        {
            builder.Append(library.Code).Append(':').Append(library.VersionId).Append(':').Append(library.HashHex)
                .Append('[').Append(Signature(library.Imports)).Append("],");
        }

        return builder.ToString();
    }

    private static CompiledScript CompileOrThrow(string text, ScriptCompileOptions options, string code)
    {
        var compilation = ScriptCompiler.Compile(text, options);
        if (compilation.Script is not { } script || compilation.Diagnostics.Count > 0)
        {
            var first = compilation.Diagnostics.Count > 0 ? compilation.Diagnostics[0] : null;
            throw Failure(
                "err.ECR-CALC-0422.scriptCompileFailed",
                $"Скрипт {code} не компілюється: " + (first is null ? "невідома помилка" : $"{first.Code} ({first.Line}:{first.Column}) {first.Message}"),
                ("code", code),
                ("diagnostic", first?.Code ?? string.Empty),
                ("line", first?.Line.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"));
        }

        return script;
    }

    private static DomainException Failure(string messageKey, string message, params (string Key, string Value)[] details)
    {
        var map = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        foreach (var (key, value) in details)
        {
            map[key] = value;
        }

        return new DomainException("ECR-CALC-0422", message, map);
    }
}
