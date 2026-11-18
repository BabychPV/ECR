// src/Ecr.Application/Calculations/Scripting/ScriptSourceCompiler.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
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

    /// <summary>
    /// ЄДИНА точка компіляції тексту ВЕРСІЇ: опції (бібліотека, рівень <c>Module</c>, режим арифметики) беруться з самої
    /// версії, тож нове місце не може забути ознаку <c>IsModuleLevel</c> (її відсутність дає хибний <c>missingRows</c>).
    /// Усі шляхи авторингу (<c>GET/PUT script</c>, <c>validate</c>, <c>preview</c>, публікація, перенесення) йдуть сюди.
    /// </summary>
    /// <param name="text">Текст скрипта.</param>
    /// <param name="methodology">Методологія-власник.</param>
    /// <param name="version">Версія.</param>
    /// <param name="onDate">Дата, на яку добираються версії бібліотек.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="shadowScripts">Перенесення: див. <see cref="CompileAsync"/>.</param>
    public Task<ScriptCompilation> CompileForVersionAsync(
        string text,
        Methodology methodology,
        MethodologyVersion version,
        DateOnly onDate,
        CancellationToken ct,
        IReadOnlyDictionary<int, MethodologyScript>? shadowScripts = null)
    {
        ArgumentNullException.ThrowIfNull(methodology);
        ArgumentNullException.ThrowIfNull(version);

        return CompileAsync(
            text, methodology.Code, version.NumericMode, methodology.Kind == MethodologyKind.Library, onDate, ct,
            shadowScripts, IsModuleLevel(methodology, version));
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
    /// <param name="isModuleLevel">
    /// Версія рівня <c>Module</c> (HSE400): речення <c>rows</c> не вимагається (набір аргументів складає модуль). Стосується
    /// лише самого скрипта-власника, а не його бібліотек.
    /// </param>
    /// <returns>Результат компіляції з діагностиками.</returns>
    public async Task<ScriptCompilation> CompileAsync(
        string text,
        string ownerCode,
        NumericMode numericMode,
        bool isLibrary,
        DateOnly onDate,
        CancellationToken ct,
        IReadOnlyDictionary<int, MethodologyScript>? shadowScripts = null,
        bool isModuleLevel = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ownerCode);

        var result = await CompileCoreAsync(
                text, numericMode, isLibrary, onDate, [ownerCode],
                new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase), shadowScripts, ct, isModuleLevel)
            .ConfigureAwait(false);
        return result.Compilation;
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

    /// <summary>Страховка від ланцюга бібліотек, що нескінченно росте: глибше розвідка не йде (результат однаково помилка).</summary>
    private const int MaxExploreDepth = 64;

    private enum EntryKind
    {
        Missing,
        NotScript,
        Compiled,
    }

    /// <summary>
    /// Результат бібліотеки, не залежний від місця імпорту. <paramref name="Route"/> — найглибший маршрут імпортів
    /// від неї (вона сама й нащадки): за ним межа глибини перевіряється при кожному використанні запису, бо один і той
    /// самий запис дозволений неглибоко й заборонений глибоко.
    /// </summary>
    private sealed record Entry(EntryKind Kind, CompiledScript? Script, string? CyclePath, IReadOnlyList<string> Route)
    {
        public int Height => Route.Count - 1;
    }

    private sealed record CoreResult(ScriptCompilation Compilation, string? CyclePath, IReadOnlyList<string> Below);

    private static IReadOnlyList<string> Longer(IReadOnlyList<string> a, IReadOnlyList<string> b) => b.Count > a.Count ? b : a;

    /// <summary>
    /// Компілює текст з імпортами. <paramref name="memo"/> — результати бібліотек у межах однієї компіляції: кожна
    /// бібліотека завантажується й компілюється не більше разу (ромбоподібні імпорти інакше давали до F^глибина
    /// повторів). Цикл виявляється за ланцюгом (<paramref name="chain"/>) раніше за кеш; межа глибини — за маршрутом запису.
    /// </summary>
    private async Task<CoreResult> CompileCoreAsync(
        string text,
        NumericMode numericMode,
        bool isLibrary,
        DateOnly onDate,
        List<string> chain,
        Dictionary<string, Entry> memo,
        IReadOnlyDictionary<int, MethodologyScript>? shadowScripts,
        CancellationToken ct,
        bool isModuleLevel = false)
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
            }, isModuleLevel));

        if (requested.Count == 0)
        {
            return new CoreResult(probe, null, []);
        }

        var resolved = new Dictionary<string, CompiledScript>(StringComparer.Ordinal);
        var notScript = new HashSet<string>(StringComparer.Ordinal);
        var cycles = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<string> below = [];

        foreach (var name in requested)
        {
            if (chain.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                cycles[name] = Path([.. chain, name]);
                below = Longer(below, [name]);
                continue;
            }

            if (chain.Count >= MaxExploreDepth)
            {
                cycles[name] = Path([.. chain, name]);
                below = Longer(below, [.. Enumerable.Repeat(name, MaxImportDepth + 1)]);
                continue;
            }

            if (!memo.TryGetValue(name, out var entry))
            {
                entry = await ComputeEntryAsync(name, onDate, chain, memo, shadowScripts, ct).ConfigureAwait(false);
                memo[name] = entry;
            }

            below = Longer(below, entry.Route);
            if (entry.CyclePath is not null)
            {
                cycles[name] = entry.CyclePath;
                continue;
            }

            if (chain.Count + entry.Height >= MaxImportDepth)
            {
                cycles[name] = Path(chain.Concat(entry.Route).Take(MaxImportDepth + 1));
                continue;
            }

            switch (entry.Kind)
            {
                case EntryKind.NotScript:
                    notScript.Add(name);
                    break;
                case EntryKind.Compiled when entry.Script is not null:
                    resolved[name] = entry.Script;
                    break;
                default:
                    break;
            }
        }

        var final = ScriptCompiler.Compile(
            text,
            new ScriptCompileOptions(numericMode, isLibrary, name => resolved.GetValueOrDefault(name), isModuleLevel));

        if (cycles.Count == 0 && notScript.Count == 0)
        {
            return new CoreResult(final, null, below);
        }

        var diagnostics = final.Diagnostics
            .Select(d => Rewrite(d, notScript, cycles))
            .ToList();
        var cycle = cycles.Values.FirstOrDefault();
        return new CoreResult(new ScriptCompilation(final.Script, diagnostics), cycle, below);
    }

    private async Task<Entry> ComputeEntryAsync(
        string name,
        DateOnly onDate,
        List<string> chain,
        Dictionary<string, Entry> memo,
        IReadOnlyDictionary<int, MethodologyScript>? shadowScripts,
        CancellationToken ct)
    {
        var library = await drafts.FindByCodeAsync(name, ct).ConfigureAwait(false);
        if (library is null)
        {
            return new Entry(EntryKind.Missing, null, null, [name]);
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
            return new Entry(EntryKind.NotScript, null, null, [name]);
        }

        chain.Add(name);
        try
        {
            var inner = await CompileCoreAsync(
                    script.Text, script.NumericMode, script.IsLibrary, onDate, chain, memo, shadowScripts, ct)
                .ConfigureAwait(false);
            return new Entry(EntryKind.Compiled, inner.Compilation.Script, inner.CyclePath, [name, .. inner.Below]);
        }
        finally
        {
            chain.RemoveAt(chain.Count - 1);
        }
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
