using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.MethodologyScriptMigration.Tests;

public sealed class CorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void Package_AF_Land_кожна_методологія_конвертується_і_компілюється_без_діагностик()
    {
        var sources = PackageCorpus.Load();
        Assert.Equal(12, sources.Count);

        var failures = new List<string>();
        var converted = 0;
        CompiledScript? common = null;
        foreach (var source in sources.OrderByDescending(s => s.IsLibrary))
        {
            var result = ScriptConverter.Convert(source);
            var errors = result.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
            if (result.Text is null || errors.Count > 0)
            {
                failures.Add(source.MethodologyCode + ": " + string.Join(" | ", errors.Select(e => e.Code + ": " + e.Message)));
                continue;
            }

            var compiled = ScriptCompiler.Compile(
                result.Text,
                new ScriptCompileOptions(source.NumericMode, source.IsLibrary, code => code == PackageCorpus.LibraryCode ? common : null));
            if (compiled.Diagnostics.Count > 0)
            {
                failures.Add(source.MethodologyCode + " (compile): " + string.Join(
                    " | ", compiled.Diagnostics.Select(d => $"{d.Code}@{d.Line}:{d.Column} {d.Message}")));
                continue;
            }

            if (source.IsLibrary)
            {
                common = compiled.Script;
            }

            converted++;
            output.WriteLine(
                $"{source.MethodologyCode}: OK, {result.Text.Length} chars, issues: "
                + string.Join(", ", result.Issues.GroupBy(i => i.Severity + "/" + i.Code).Select(g => g.Key + "x" + g.Count())));
        }

        output.WriteLine($"converted {converted}, failed {failures.Count}");
        foreach (var failure in failures)
        {
            output.WriteLine("FAIL " + failure);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Equal(12, converted);
    }

    [Fact]
    public void Package_AF_Land_конвертація_детермінована()
    {
        var first = PackageCorpus.Load().Select(s => ScriptConverter.Convert(s).Text).ToList();
        var second = PackageCorpus.Load().Select(s => ScriptConverter.Convert(s).Text).ToList();
        Assert.Equal(first, second);
    }
}
