// src/Ecr.Application/Calculations/Scripting/ScriptSourceCompiler.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>
/// Компіляція скрипта з розв'язанням імпортів: код методології → скомпільована бібліотека версії, чинної на дату
/// (MS, план 3.8). Цикл імпортів і глибина понад 8 — діагностика <c>importCycle</c>.
/// </summary>
/// <remarks>
/// ⚠ Компілятор (<see cref="ScriptCompiler"/>) синхронний, а версії бібліотек читаються зі сховища асинхронно, тож
/// компіляція йде двічі: перший прохід лише збирає назви імпортів, другий — із завантаженими бібліотеками.
/// Імпорт, що існує, але не має чинної версії з рушієм <c>Script</c>, дає <c>libraryNotScript</c> замість
/// безпредметного «бібліотеку не знайдено».
/// </remarks>
public sealed class ScriptSourceCompiler(IMethodologyDraftStore drafts, IMethodologyStore methodologies)
{
    /// <summary>Найбільша глибина вкладення імпортів (разом із самим скриптом).</summary>
    public const int MaxImportDepth = 8;

    /// <summary>Компілює текст версії.</summary>
    /// <param name="text">Текст скрипта.</param>
    /// <param name="ownerCode">Код методології-власника (початок ланцюга імпортів).</param>
    /// <param name="numericMode">Режим арифметики версії.</param>
    /// <param name="isLibrary">Методологія — бібліотека.</param>
    /// <param name="onDate">Дата, на яку добираються версії бібліотек.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="shadowScripts">
    /// Перенесення: бібліотека з рушієм <c>Formulas</c> і «тіньовим» скриптом теж годиться (текст зберігають ДО
    /// перемикання рушія бібліотеки), а тексти версій, ще не збережені (пакет <c>apply</c>), беруться звідси замість
    /// сховища (версія → скрипт). <c>null</c> — звичайне авторство: лише бібліотеки на рушії <c>Script</c>.
    /// </param>
    /// <returns>Результат компіляції з діагностиками.</returns>
    public async Task<ScriptCompilation> CompileAsync(
        string text,
        string ownerCode,
        NumericMode numericMode,
        bool isLibrary,
        DateOnly onDate,
        CancellationToken ct,
        IReadOnlyDictionary<int, MethodologyScript>? shadowScripts = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ownerCode);

        var (compilation, _) = await CompileCoreAsync(
                text, numericMode, isLibrary, onDate, [ownerCode], shadowScripts, ct)
            .ConfigureAwait(false);
        return compilation;
    }

    /// <summary>
    /// Бібліотеки, на які посилається ТЕКСТ (<c>import X;</c>), з версіями, чинними на дату. Імпорт без чинної версії
    /// повертається з <c>MethodologyVersionId = null</c>; методологію, якої немає, пропущено (компіляція дала б
    /// <c>unknownImport</c>). Рушій перегляду бере імпорти з тексту, а не з матеріалізованих рядків.
    /// </summary>
    /// <param name="text">Текст скрипта.</param>
    /// <param name="numericMode">Режим арифметики версії.</param>
    /// <param name="isLibrary">Методологія — бібліотека.</param>
    /// <param name="onDate">Дата, на яку добираються версії бібліотек.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<MethodologyLibrary>> ResolveTextImportsAsync(
        string text, NumericMode numericMode, bool isLibrary, DateOnly onDate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);

        var requested = new List<string>();
        ScriptCompiler.Compile(
            text,
            new ScriptCompileOptions(numericMode, isLibrary, name =>
            {
                if (!requested.Contains(name, StringComparer.Ordinal))
                {
                    requested.Add(name);
                }

                return null;
            }));

        var result = new List<MethodologyLibrary>();
        foreach (var name in requested.Order(StringComparer.Ordinal))
        {
            if (await drafts.FindByCodeAsync(name, ct).ConfigureAwait(false) is { } library)
            {
                result.Add(new MethodologyLibrary(library.Id, library.Code, library.VersionOn(onDate)?.Id, []));
            }
        }

        return result;
    }

    private async Task<(ScriptCompilation Compilation, string? CyclePath)> CompileCoreAsync(
        string text,
        NumericMode numericMode,
        bool isLibrary,
        DateOnly onDate,
        List<string> chain,
        IReadOnlyDictionary<int, MethodologyScript>? shadowScripts,
        CancellationToken ct)
    {
        var requested = new List<string>();
        var probe = ScriptCompiler.Compile(
            text,
            new ScriptCompileOptions(numericMode, isLibrary, name =>
            {
                if (!requested.Contains(name, StringComparer.Ordinal))
                {
                    requested.Add(name);
                }

                return null;
            }));

        if (requested.Count == 0)
        {
            return (probe, null);
        }

        var resolved = new Dictionary<string, CompiledScript>(StringComparer.Ordinal);
        var notScript = new HashSet<string>(StringComparer.Ordinal);
        var cycles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in requested)
        {
            if (chain.Contains(name, StringComparer.OrdinalIgnoreCase) || chain.Count >= MaxImportDepth)
            {
                cycles[name] = Path([.. chain, name]);
                continue;
            }

            var library = await drafts.FindByCodeAsync(name, ct).ConfigureAwait(false);
            if (library is null)
            {
                continue;
            }

            var version = library.VersionOn(onDate);
            MethodologyScript? script = null;
            if (version is { } found)
            {
                if (shadowScripts is not null && shadowScripts.TryGetValue(found.Id, out var pending))
                {
                    script = pending;
                }
                else if (found.Engine == MethodologyEngine.Script || shadowScripts is not null)
                {
                    script = await methodologies.GetScriptAsync(found.Id, ct).ConfigureAwait(false);
                }
            }

            if (script is null)
            {
                notScript.Add(name);
                continue;
            }

            chain.Add(name);
            try
            {
                var (inner, innerCycle) = await CompileCoreAsync(
                        script.Text, script.NumericMode, script.IsLibrary, onDate, chain, shadowScripts, ct)
                    .ConfigureAwait(false);
                if (innerCycle is not null)
                {
                    cycles[name] = innerCycle;
                }
                else if (inner.Script is not null)
                {
                    resolved[name] = inner.Script;
                }
            }
            finally
            {
                chain.RemoveAt(chain.Count - 1);
            }
        }

        var final = ScriptCompiler.Compile(
            text,
            new ScriptCompileOptions(numericMode, isLibrary, name => resolved.GetValueOrDefault(name)));

        if (cycles.Count == 0 && notScript.Count == 0)
        {
            return (final, null);
        }

        var diagnostics = final.Diagnostics
            .Select(d => Rewrite(d, notScript, cycles))
            .ToList();
        var cycle = cycles.Values.FirstOrDefault();
        return (new ScriptCompilation(final.Script, diagnostics), cycle);
    }

    private static ScriptDiagnostic Rewrite(
        ScriptDiagnostic diagnostic, HashSet<string> notScript, Dictionary<string, string> cycles)
    {
        if (diagnostic.Code != ScriptDiagnosticCodes.UnknownImport
            || !diagnostic.Params.TryGetValue("name", out var name))
        {
            return diagnostic;
        }

        if (cycles.TryGetValue(name, out var path))
        {
            return new ScriptDiagnostic(
                ScriptDiagnosticCodes.ImportCycle, diagnostic.Line, diagnostic.Column, diagnostic.Length,
                $"Import '{name}' closes a cycle or is nested too deeply (limit {MaxImportDepth.ToString(CultureInfo.InvariantCulture)}): {path}.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = name, ["path"] = path });
        }

        if (notScript.Contains(name))
        {
            return new ScriptDiagnostic(
                ScriptDiagnosticCodes.LibraryNotScript, diagnostic.Line, diagnostic.Column, diagnostic.Length,
                $"Methodology '{name}' has no version in effect on the date that is written as a script.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = name });
        }

        return diagnostic;
    }

    private static string Path(IEnumerable<string> chain) => string.Join(" -> ", chain);
}
