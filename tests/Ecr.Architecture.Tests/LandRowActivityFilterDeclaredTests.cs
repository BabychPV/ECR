// tests/Ecr.Architecture.Tests/LandRowActivityFilterDeclaredTests.cs
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-425: кожна методологія Land має явне рішення щодо фільтра «лише Running» — або прапорець, або виняток із
/// причиною (<c>tools/land/land-row-filter.json</c>).
/// </summary>
/// <remarks>
/// ⛔ Сторож порівнює ДВА JSON-файли і нічого не парсить із C#/ps1: коди методологій пакета AF
/// (<c>af-land-package.json</c>) і результатів (<c>land-result-outputs.json</c>) проти явного переліку
/// <c>runningOnly</c> ∪ <c>exempt</c>. Нова методологія Land без рішення — червоне з готовим рядком для додавання.
/// Інакше вона рахувала б усі рядки, включно з Removed, мовчки — рівно так жили 25 методологій до D-425.
/// Мутація: прибрати код із <c>runningOnly</c> — перший тест червоніє; дати винятку порожню причину — третій.
/// </remarks>
public sealed class LandRowActivityFilterDeclaredTests
{
    private const string FilterFile = "tools/land/land-row-filter.json";
    private const string PackageFile = "docs/delivery/reference-data/land-template/af-land-package.json";
    private const string OutputsFile = "docs/delivery/reference-data/land-template/land-result-outputs.json";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "D-425")]
    public void Кожна_методологія_Land_має_рішення_щодо_фільтра_лише_Running()
    {
        var decided = RunningOnly().Concat(Exempt().Keys).ToHashSet(StringComparer.Ordinal);

        var missing = LandMethodologyCodes()
            .Where(code => !decided.Contains(code))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Методології Land без рішення про фільтр «лише Running» у {FilterFile}: "
            + string.Join(", ", missing) + ". Додай код у runningOnly (колонка статусу є) "
            + "або в exempt з причиною (колонки статусу немає).");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "D-425")]
    public void Перелік_фільтра_не_містить_методологій_яких_немає_в_Land_і_не_суперечить_собі()
    {
        var known = LandMethodologyCodes();
        var running = RunningOnly();
        var exempt = Exempt();

        var unknown = running.Concat(exempt.Keys)
            .Where(code => !known.Contains(code))
            .Order(StringComparer.Ordinal)
            .ToList();
        var both = running.Where(exempt.ContainsKey).Order(StringComparer.Ordinal).ToList();
        var duplicates = running.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(unknown.Count == 0, $"У {FilterFile} коди, яких немає в пакетах Land: {string.Join(", ", unknown)}.");
        Assert.True(both.Count == 0, $"Код і в runningOnly, і в exempt: {string.Join(", ", both)}.");
        Assert.True(duplicates.Count == 0, $"Дубль у runningOnly: {string.Join(", ", duplicates)}.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "D-425")]
    public void Кожен_виняток_має_непорожню_причину_а_фільтр_має_формат_довідник_запис()
    {
        var empty = Exempt()
            .Where(e => string.IsNullOrWhiteSpace(e.Value))
            .Select(e => e.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(empty.Count == 0, $"Винятки без причини: {string.Join(", ", empty)}.");

        using var document = Read(FilterFile);
        var filter = document.RootElement.GetProperty("filter").GetString();
        Assert.NotNull(Ecr.Domain.Entities.Calculations.RowActivityFilterSpec.TryParse(filter));
        Assert.NotEmpty(RunningOnly());
    }

    private static HashSet<string> LandMethodologyCodes()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);

        using (var package = Read(PackageFile))
        {
            foreach (var item in package.RootElement.GetProperty("methodologies").EnumerateArray())
            {
                codes.Add(item.GetProperty("name").GetString()!);
            }
        }

        using (var outputs = Read(OutputsFile))
        {
            foreach (var name in new[] { "methodologies", "supplementalMethodologies" })
            {
                foreach (var property in outputs.RootElement.GetProperty(name).EnumerateObject())
                {
                    codes.Add(property.Name);
                }
            }
        }

        Assert.True(codes.Count >= 40, $"Розібрано підозріло мало методологій Land: {codes.Count}.");

        return codes;
    }

    private static List<string> RunningOnly()
    {
        using var document = Read(FilterFile);

        return [.. document.RootElement.GetProperty("runningOnly").EnumerateArray().Select(e => e.GetString()!)];
    }

    private static Dictionary<string, string> Exempt()
    {
        using var document = Read(FilterFile);

        return document.RootElement.GetProperty("exempt").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    private static JsonDocument Read(string relative)
    {
        var path = Path.Combine(SolutionRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Немає {path}.");

        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
