// tests/Ecr.Architecture.Tests/UsageKindsTests.cs
using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Види «де використано» (<see cref="UsageItemDto.Kind"/>) — лише з
/// <see cref="UsageKinds"/>, і перелік дорівнює тому, що сховища віддають.
/// </summary>
/// <remarks>
/// ⚠ Сторож по тексту джерел, а не прогін на БД: вид, доданий сирим рядком,
/// на базі проявився б лише тоді, коли тест засіяв би саме такий об'єкт, —
/// тобто новий вид, про який ніхто не знає, і пройшов би непоміченим.
/// </remarks>
public sealed class UsageKindsTests
{
    /// <summary>Сирий рядок першим аргументом — там, де має стояти вид.</summary>
    private static readonly Regex RawKind = new(
        @"(?:\bAddAsync|\bnew\s+UsageItemDto)\(\s*""(?<kind>[^""]*)""", RegexOptions.CultureInvariant);

    private static readonly Regex KindReference = new(
        @"\bUsageKinds\.(?<name>[A-Za-z]+)\b", RegexOptions.CultureInvariant);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Directive", "BE-24")]
    public void Значення_на_дроті_ті_самі_що_їх_уже_читає_клієнт()
    {
        // ⛔ Літералами: перейменування константи змінило б JSON мовчки.
        Assert.Equal(
            [
                "data", "derivedUnit", "dimensionBase", "fieldMap", "methodologyConstant",
                "methodologyFormula", "methodologyOutput", "methodologySubstance", "registryField",
                "sourceEntity", "templateColumn", "unitConversion",
            ],
            UsageKinds.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Directive", "BE-24")]
    public void Сховища_віддають_рівно_види_з_UsageKinds_і_жодного_сирим_рядком()
    {
        var sources = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "Ecr.Infrastructure"), "*.cs", SearchOption.AllDirectories)
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .Where(f => f.Text.Contains("UsageItemDto(", StringComparison.Ordinal))
            .ToList();

        // Не порожній прохід: обидва відомі сховища мусять знайтися.
        Assert.Contains(sources, f => f.Name == "RegistryStore.cs");
        Assert.Contains(sources, f => f.Name == "UnitStore.cs");

        var raw = sources
            .SelectMany(f => RawKind.Matches(f.Text).Select(m => $"{f.Name}: \"{m.Groups["kind"].Value}\""))
            .ToList();
        Assert.Empty(raw);

        var used = sources
            .SelectMany(f => KindReference.Matches(f.Text).Select(m => m.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);

        // Константа, якої не віддає жодне сховище, — мертвий ключ каталогу в клієнті.
        var declared = typeof(UsageKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => f.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(declared, used.Where(n => n != nameof(UsageKinds.All)).Order(StringComparer.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
