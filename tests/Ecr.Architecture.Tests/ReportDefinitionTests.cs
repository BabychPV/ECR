using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Визначення звіту — <b>дані</b>, а вʼюхи <c>rpt.v_*</c> — згенерований
/// артефакт розгортання (<c>ФВ-10.4</c>, <c>D-14</c>, <c>D-66</c>).
/// </summary>
/// <remarks>
/// ⛔ Сенс вимоги операційний, а не архітектурний: нова державна форма не має
/// потребувати релізу коду. Порушення тут виглядає нешкідливо — «опишу цей
/// звіт класом, поки немає конструктора» — і після цього кожна зміна форми
/// вимагає збірки, тестів і вікна розгортання. Саме так виглядає чинне
/// рішення, від якого система тікає.
/// </remarks>
public sealed partial class ReportDefinitionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-10.4")]
    public void Визначення_звіту_живе_в_таблицях_а_не_в_коді()
    {
        // Опис звіту — сутності з полями-даними: перелік колонок і правила
        // лежать у JSON, а не в типах. Клас на кожен звіт означав би 166
        // класів і реліз на кожну зміну форми.
        var definitions = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Domain", "Entities", "Reporting", "ReportDefinitions.cs"));

        Assert.Contains("ColumnsJson", definitions, StringComparison.Ordinal);
        Assert.Contains("RulesJson", definitions, StringComparison.Ordinal);

        // ⛔ І жодного типу, названого на честь конкретного звіту. Перший же
        // `WaterReportDef : ReportDef` перетворив би дані назад на код.
        var offenders = Directory
            .EnumerateFiles(Path.Combine(SolutionRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                       StringComparison.Ordinal))
            .SelectMany(f => NamedReportTypeRegex
                .Matches(File.ReadAllText(f))
                .Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-10.4")]
    public void Фільтр_за_статусом_стоїть_у_вьюсі_а_не_в_RDL()
    {
        // ⛔ `ФВ-10.11`: звіти для регулятора читають лише `Approved` і
        // `Submitted`, і фільтр стоїть у вʼюсі. 166 RDL не можуть покладатися
        // на те, що кожен автор не забув умову — забуде один, і чернетка
        // потрапить у державну форму.
        var views = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "05-rpt-views.sql"));

        foreach (Match view in ViewRegex.Matches(views))
        {
            var body = view.Groups["body"].Value;

            Assert.True(
                body.Contains("s.Status IN", StringComparison.Ordinal),
                $"Вʼюха {view.Groups[1].Value} не фільтрує за статусом: чернетка потрапить "
                + "у державну форму, і помітить це регулятор.");

            Assert.True(
                body.Contains("IsCurrent", StringComparison.Ordinal),
                $"Вʼюха {view.Groups[1].Value} не обмежує зріз поточним: SSRS отримав би "
                + "всі побудовані зрізи разом, тобто кожен рядок стільки разів, скільки їх було.");
        }

        // Порожній файл теж пройшов би цикл — а він означав би, що регулятор
        // не бачить нічого.
        Assert.NotEmpty(ViewRegex.Matches(views));
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

    /// <summary>Тип, названий на честь конкретного звіту.</summary>
    [GeneratedRegex(@"class\s+\w+Report(?:Def|Version)\s*:")]
    private static partial Regex NamedReportTypeRegex { get; }

    /// <summary>Вʼюха звітності разом із тілом до <c>GO</c>.</summary>
    [GeneratedRegex(@"CREATE OR ALTER VIEW (rpt\.v_\w+)\s*AS(?<body>.*?)\nGO", RegexOptions.Singleline)]
    private static partial Regex ViewRegex { get; }
}
