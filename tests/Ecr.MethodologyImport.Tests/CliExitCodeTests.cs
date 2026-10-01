using System.Diagnostics;
using Xunit;

namespace Ecr.MethodologyImport.Tests;

/// <summary>Справжній запуск консольного інструмента: код виходу — те, що читають скрипти й люди.</summary>
public sealed class CliExitCodeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ecr-methimport-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (int Exit, string Out) Run(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Ecr.MethodologyImport.dll");
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        info.ArgumentList.Add(dll);
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));
        return (process.ExitCode, output);
    }

    private string Write(string xml)
    {
        var path = Path.Combine(_dir, "af.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void Без_блокерів_код_0()
    {
        var path = Write(new AfXmlBuilder().Formula("M", "V1", "A", "", "1").Build());

        var (exit, output) = Run("analyze", path);

        Assert.Equal(0, exit);
        Assert.Contains("Блокерів немає", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Нерезолвне_посилання_дає_код_2_і_json_із_блокером()
    {
        var path = Write(new AfXmlBuilder().Formula("M", "V1", "A", "!Nope", "1").Build());

        var (exit, output) = Run("analyze", path, "--json");

        Assert.Equal(2, exit);
        Assert.Contains("UNRESOLVED_REFERENCES: 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_без_блокерів_пише_пакет_і_дає_код_0()
    {
        var path = Write(new AfXmlBuilder().Formula("M", "V1", "A", "", "1").Build());
        var package = Path.Combine(_dir, "package.json");

        var (exit, _) = Run("export", path, "--out", package);

        Assert.Equal(0, exit);
        Assert.Contains("\"format\": \"ecr-methodology-package\"", File.ReadAllText(package), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_за_блокерів_не_пише_пакет_код_2_а_з_allow_blockers_пише_разом_із_блокерами()
    {
        var path = Write(new AfXmlBuilder().Formula("M", "V1", "A", "!Nope", "1").Build());
        var package = Path.Combine(_dir, "package.json");

        Assert.Equal(2, Run("export", path, "--out", package).Exit);
        Assert.False(File.Exists(package));

        Assert.Equal(2, Run("export", path, "--out", package, "--allow-blockers").Exit);
        Assert.Contains("UNRESOLVED_REFERENCES: 1", File.ReadAllText(package), StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolved_пише_markdown_з_рекомендацією_і_дає_код_2()
    {
        var path = Write(new AfXmlBuilder()
            .Formula("Common", "V1", "Common_Flow", "", "1")
            .Formula("M", "V1", "A", "!common_flow", "1")
            .Build());
        var report = Path.Combine(_dir, "unresolved.md");

        var (exit, output) = Run("unresolved", path, "--out", report);

        Assert.Equal(2, exit);
        Assert.Equal(output, File.ReadAllText(report));
        Assert.Contains("`Common_Flow`", output, StringComparison.Ordinal);
        Assert.Contains("Усього: 1;", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_без_out_дає_код_1()
    {
        var path = Write(new AfXmlBuilder().Formula("M", "V1", "A", "", "1").Build());

        Assert.Equal(1, Run("export", path).Exit);
    }

    [Fact]
    public void Відсутній_файл_і_некоректний_xml_дають_код_1()
    {
        Assert.Equal(1, Run("analyze", Path.Combine(_dir, "немає.xml")).Exit);
        Assert.Equal(1, Run("analyze", Write("<a><b></a>")).Exit);
        Assert.Equal(1, Run("bogus", "x").Exit);
    }
}
