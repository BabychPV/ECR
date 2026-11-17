using System.Security.Cryptography;
using System.Text;
using Ecr.Domain.Enums;
using Ecr.Scripting.Binding;
using Ecr.Scripting.Parsing;

namespace Ecr.Scripting;

/// <summary>Параметри компіляції.</summary>
/// <param name="NumericMode">Числовий режим версії (визначає дозволені функції).</param>
/// <param name="IsLibrary">Методологія виду <c>Library</c>: без <c>rows</c> і <c>output</c>.</param>
/// <param name="ResolveImport">Код методології → скомпільована бібліотека; <c>null</c> — імпорт недоступний.
/// Виявлення циклів між бібліотеками — справа викликача.</param>
public sealed record ScriptCompileOptions(
    NumericMode NumericMode,
    bool IsLibrary,
    Func<string, CompiledScript?>? ResolveImport = null);

/// <summary>Результат компіляції.</summary>
/// <param name="Script">Скомпільований скрипт. <c>null</c>, якщо були синтаксичні помилки або перевищено межі
/// (<c>tooLarge</c>, <c>tooDeep</c>, <c>foreachTooLarge</c>); за семантичних помилок скрипт будується «як є».</param>
/// <param name="Diagnostics">Діагностики, відсортовані за позицією.</param>
public sealed record ScriptCompilation(CompiledScript? Script, IReadOnlyList<ScriptDiagnostic> Diagnostics)
{
    /// <summary>Успіх: є скрипт і немає жодної діагностики.</summary>
    public bool IsSuccess => Script is not null && Diagnostics.Count == 0;
}

/// <summary>Компілятор «ECR Script»: текст → <see cref="CompiledScript"/>. Не обчислює нічого.</summary>
public static class ScriptCompiler
{
    /// <summary>Найбільша довжина тексту, символів.</summary>
    public const int MaxTextLength = 200_000;

    /// <summary>Компілює текст скрипта.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="options">Параметри.</param>
    public static ScriptCompilation Compile(string text, ScriptCompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);

        if (text.Length > MaxTextLength)
        {
            var d = ScriptDiagnostic.At(ScriptDiagnosticCodes.TooLarge, new ScriptSpan(0, 1, 1, 1),
                $"Script is too large ({text.Length} characters, the limit is {MaxTextLength}).",
                ("length", text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("max", MaxTextLength.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return new ScriptCompilation(null, [d]);
        }

        var parsed = ScriptParser.Parse(text);
        if (parsed.Aborted || parsed.Diagnostics.Count > 0)
        {
            return new ScriptCompilation(null, Sort(parsed.Diagnostics));
        }

        try
        {
            var (script, diagnostics) = ScriptBinder.Bind(text, options, parsed.Program);
            return new ScriptCompilation(script, Sort(diagnostics));
        }
        catch (InsufficientExecutionStackException)
        {
            var d = ScriptDiagnostic.At(ScriptDiagnosticCodes.TooDeep, new ScriptSpan(0, 1, 1, 1),
                "Expression nesting is too deep.", ("max", ScriptParser.MaxNesting.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return new ScriptCompilation(null, [d]);
        }
    }

    /// <summary>SHA-256 від UTF-8 тексту (без BOM); переводи рядків нормалізовано до <c>\n</c>.</summary>
    /// <param name="text">Текст.</param>
    public static byte[] Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return SHA256.HashData(new UTF8Encoding(false).GetBytes(normalized));
    }

    private static List<ScriptDiagnostic> Sort(IEnumerable<ScriptDiagnostic> diagnostics)
        => [.. diagnostics.Select((d, i) => (d, i)).OrderBy(x => x.d.Line).ThenBy(x => x.d.Column).ThenBy(x => x.i).Select(x => x.d)];
}
