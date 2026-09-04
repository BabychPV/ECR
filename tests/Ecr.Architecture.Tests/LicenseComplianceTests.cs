using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Ліцензійна чистота стека.
/// </summary>
/// <remarks>
/// Пакет із невідповідною ліцензією виявляється не на code review, а на
/// юридичній перевірці перед здачею — коли переписувати вже нема коли.
/// </remarks>
public sealed class LicenseComplianceTests
{
    /// <summary>
    /// Пакети, які не можна використовувати.
    /// </summary>
    /// <remarks>
    /// <c>FluentAssertions</c> з версії 8 має комерційну ліцензію для
    /// організацій — саме тому в проєкті голі <c>Assert</c>. Решта —
    /// копілефт або платні за замовчуванням.
    /// </remarks>
    private static readonly string[] Forbidden =
    [
        "FluentAssertions",
        "AutoMapper",          // з v13 платний для комерційного використання
        "MediatR",             // з v13 платний
        "Newtonsoft.Json",     // не ліцензія, а дублювання System.Text.Json
        "iTextSharp",          // AGPL
        "GPL",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Жоден_проєкт_не_посилається_на_заборонений_пакет()
    {
        var offenders = new List<string>();

        foreach (var project in EnumerateProjects())
        {
            var text = File.ReadAllText(project);
            foreach (var package in Forbidden)
            {
                if (Regex.IsMatch(text, $@"PackageReference\s+Include\s*=\s*""{Regex.Escape(package)}"))
                {
                    offenders.Add($"{Path.GetFileName(project)}: {package}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Версії_пакетів_оголошені_централізовано_а_не_в_csproj()
    {
        // ⚠ Версія в csproj обходить Directory.Packages.props, і два проєкти
        // починають тягнути різні версії того самого пакета. Виявляється це
        // конфліктом складання в рантаймі, а не при збірці.
        var offenders = new List<string>();

        foreach (var project in EnumerateProjects())
        {
            foreach (Match match in Regex.Matches(
                File.ReadAllText(project), @"<PackageReference[^>]*\bVersion\s*=\s*""[^""]+"""))
            {
                offenders.Add($"{Path.GetFileName(project)}: {match.Value.Trim()}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Кожен_прямий_пакет_має_запис_у_реєстрі_стека()
    {
        // Реєстр — це Directory.Packages.props: пакет, якого в ньому немає,
        // не має і версії, тобто збірка просто не пройде. Тест фіксує, що
        // жоден csproj не посилається на щось поза реєстром.
        var registry = File.ReadAllText(Path.Combine(SourceTree.Root, "Directory.Packages.props"));
        var declared = Regex.Matches(registry, @"PackageVersion\s+Include\s*=\s*""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(declared);

        var offenders = new List<string>();
        foreach (var project in EnumerateProjects())
        {
            foreach (Match match in Regex.Matches(
                File.ReadAllText(project), @"PackageReference\s+Include\s*=\s*""([^""]+)"""))
            {
                var package = match.Groups[1].Value;
                if (!declared.Contains(package))
                {
                    offenders.Add($"{Path.GetFileName(project)}: {package}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Домен_не_має_жодного_PackageReference()
    {
        // Домен без залежностей — не естетика: будь-який пакет у ньому
        // приносить свою модель світу, і предметні правила починають
        // залежати від чужої версії.
        var domain = File.ReadAllText(
            Path.Combine(SourceTree.Root, "src/Ecr.Domain/Ecr.Domain.csproj"));

        // ⚠ XML-коментарі прибираються: у csproj домену слово PackageReference
        // стоїть у коментарі «⛔ ItemGroup із PackageReference тут заборонений» —
        // тобто рівно як пояснення заборони. Перевірка по сирому тексту ловила
        // б власне пояснення.
        var withoutComments = Regex.Replace(domain, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotContain("PackageReference", withoutComments, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", withoutComments, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateProjects()
        => Directory.EnumerateFiles(SourceTree.Root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                                       StringComparison.Ordinal));
}
