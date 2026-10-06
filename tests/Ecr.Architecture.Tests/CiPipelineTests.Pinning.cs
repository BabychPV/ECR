using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Дії й інструменти конвеєра закріплені незмінно (<c>L10-12</c>).
/// </summary>
/// <remarks>
/// ⛔ Тег <c>@v4</c> — мутабельний: власник дії пересуває його на новий коміт,
/// і той самий workflow наступного дня виконує інший код. SHA цього не
/// дозволяє; тег стоїть поряд коментарем лише для читача.
///
/// ⚠ <c>dotnet-ef</c> без <c>--version</c> бере найновіший: після виходу
/// EF Core 11 CI тихо генерував би скрипт міграцій іншим інструментом, ніж
/// локально й у пакеті майстра. Тому версія інструмента дорівнює версії
/// <c>Microsoft.EntityFrameworkCore.Design</c> з <c>Directory.Packages.props</c>.
/// </remarks>
public sealed partial class CiPipelineTests
{
    /// <summary>Рядок <c>uses:</c> (не коментар).</summary>
    [GeneratedRegex(@"^\s*(?:-\s+)?uses:\s*(\S+)(.*)$", RegexOptions.Multiline)]
    private static partial Regex UsesLine();

    /// <summary>Закріплення: 40-символьний SHA і коментар із тегом.</summary>
    [GeneratedRegex(@"^[^@\s]+@[0-9a-f]{40}$")]
    private static partial Regex PinnedRef();

    [GeneratedRegex(@"^\s*#\s*v\d+(\.\d+)*\s*$")]
    private static partial Regex TagComment();

    [GeneratedRegex(@"dotnet tool install[^\n]*")]
    private static partial Regex ToolInstall();

    [GeneratedRegex(@"--version\s+(\S+)")]
    private static partial Regex ToolVersion();

    [GeneratedRegex(@"Include=""Microsoft\.EntityFrameworkCore\.Design""\s+Version=""([^""]+)""")]
    private static partial Regex EfDesignVersion();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_дія_закріплена_за_SHA_з_тегом_у_коментарі()
    {
        var files = WorkflowFiles();
        Assert.NotEmpty(files);

        var unpinned = files
            .SelectMany(file => UsesLine().Matches(File.ReadAllText(file))
                .Select(m => (File: Path.GetFileName(file), Ref: m.Groups[1].Value, Tail: m.Groups[2].Value.TrimEnd('\r'))))
            // Локальні дії (`./…`) живуть у цьому ж коміті — закріплювати нічого.
            .Where(u => !u.Ref.StartsWith("./", StringComparison.Ordinal))
            .Where(u => !PinnedRef().IsMatch(u.Ref) || !TagComment().IsMatch(u.Tail))
            .Select(u => $"{u.File}: {u.Ref}{u.Tail}")
            .ToList();

        Assert.Empty(unpinned);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Інструменти_dotnet_ставляться_з_версією_а_dotnet_ef_як_EF_Core()
    {
        var installs = WorkflowFiles()
            .SelectMany(file => ToolInstall().Matches(File.ReadAllText(file))
                .Select(m => (File: Path.GetFileName(file), Line: m.Value.TrimEnd('\r'))))
            .ToList();

        Assert.NotEmpty(installs);

        var withoutVersion = installs
            .Where(i => !ToolVersion().IsMatch(i.Line))
            .Select(i => $"{i.File}: {i.Line}")
            .ToList();

        Assert.Empty(withoutVersion);

        var ef = EfDesignVersion().Match(
            File.ReadAllText(Path.Combine(Root(), "Directory.Packages.props")));
        Assert.True(ef.Success, "Немає Microsoft.EntityFrameworkCore.Design у Directory.Packages.props.");

        var drift = installs
            .Where(i => i.Line.Contains(" dotnet-ef ", StringComparison.Ordinal))
            .Where(i => ToolVersion().Match(i.Line).Groups[1].Value != ef.Groups[1].Value)
            .Select(i => $"{i.File}: {i.Line} (EF Core {ef.Groups[1].Value})")
            .ToList();

        Assert.Empty(drift);
    }

    private static string[] WorkflowFiles()
        => Directory.GetFiles(Path.Combine(Root(), ".github", "workflows"), "*.yml");
}
