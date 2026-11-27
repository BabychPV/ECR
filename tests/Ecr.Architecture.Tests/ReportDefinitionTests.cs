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
    public void Жоден_клас_не_названий_на_честь_конкретного_звіту()
    {
        // ✎ P-1124-3 (D-NEW, людина 24.11): описів звітів у застосунку більше немає (конструктор і зрізи прибрано) —
        // форми живуть у RDL замовника поверх сирого шару rpt.*. Лишається друга половина правила: жодного класу на звіт.
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
    [Trait("Requirement", "ФВ-5.12")]
    [Trait("Requirement", "ФВ-10.11")]
    public void Кожна_вʼюха_rpt_класифікована_і_жодна_не_читає_прибрані_зрізи()
    {
        // ✎ P-1124-3 (D-NEW, людина 24.11): вʼюх ЗРІЗІВ більше немає — правило «фільтр за статусом у вʼюсі» (`ФВ-10.11`)
        // стосувалося саме їх. Сирий шар (рішення людини 2026-09-30: «для SSRS ми маємо просто підготувати сирі дані»)
        // віддає статус КОЛОНКОЮ (StatusNumberingTests), а звіт регулятору обирає статус сам.
        // ⛔ Тепер сторож — класифікація: кожна вʼюха файла — або сирі документи, або розшифровка Id, або поіменна вʼюха
        // розрахунків/методологій. Нова вʼюха поза трьома класами червонить тут, і рішення про неї приймається явно.
        var views = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "05-rpt-views.sql"));
        var all = ViewRegex.Matches(views).ToList();

        // Порожній файл пройшов би кожен цикл нижче — а означав би, що SSRS не бачить нічого.
        Assert.NotEmpty(all);

        var unclassified = all
            .Where(v => !IsRawDocumentView(v.Groups["body"].Value) && !IsReferenceView(v) && !IsCalculationView(v))
            .Select(v => v.Groups[1].Value)
            .ToList();
        Assert.Empty(unclassified);

        foreach (var view in all)
        {
            // Таблиць зрізів у базі немає (міграція P1124RemoveReportSnapshots): посилання на них — непрацююча вʼюха.
            Assert.DoesNotContain("rpt.Report", view.Groups["body"].Value, StringComparison.Ordinal);
        }

        // ⛔ R-4 (аудит 2026-11): ІМЕНОВАНІ вʼюхи-розшифровки Id (записи довідників, одиниці, підписи колонок) не читають
        // документів і розрахунків — інакше під назвою «довідник» до SSRS доїхали б дані документів.
        var references = all.Where(IsReferenceView).ToList();
        Assert.Equal(ReferenceViews.Count, references.Count);
        foreach (var reference in references)
        {
            Assert.DoesNotContain("doc.", reference.Groups["body"].Value, StringComparison.Ordinal);
            Assert.DoesNotContain("calc.", reference.Groups["body"].Value, StringComparison.Ordinal);
        }

        // Поіменні вʼюхи розрахунків — усі на місці (видалити одну — зміна контракту D-53, RptCalculationViewsTests).
        Assert.Equal(CalculationViews.Count, all.Count(IsCalculationView));
    }
    // PERMUSE: шар сирих даних для SSRS - це ще й вʼюхи даних дозволів (`rpt.v_Permit`, `rpt.v_PermitAir*`), що читають записи
    // довідників `dic.RegistryValue`: у них немає ні зрізу, ні статусу подання. Правило «жодного `rpt.Report*`» на них діє так само.
    /// <summary>R-4: вʼюхи-розшифровки Id (без даних документів і зрізів) — перелік поіменний.</summary>
    private static readonly HashSet<string> ReferenceViews = new(StringComparer.Ordinal)
    {
        "rpt.v_RegistryEntry", "rpt.v_Unit", "rpt.v_TemplateColumn",
    };

    private static bool IsReferenceView(Match view) => ReferenceViews.Contains(view.Groups[1].Value);

    /// <summary>
    /// ✎ P-1124-3 (людина 24.11: «дані розрахунків, методологія тощо мають бути доступні для SSRS»): сирі вʼюхи результатів і
    /// проблем розрахунку, методологій і розшифровок проєкту/періоду/документа — поіменно.
    /// </summary>
    private static readonly HashSet<string> CalculationViews = new(StringComparer.Ordinal)
    {
        "rpt.v_Project", "rpt.v_Period", "rpt.v_Document", "rpt.v_MethodologyVersion", "rpt.v_MethodologyOutput",
        "rpt.v_CalculationResult", "rpt.v_CalculationIssue",
    };

    private static bool IsCalculationView(Match view) => CalculationViews.Contains(view.Groups[1].Value);

    private static bool IsRawDocumentView(string body) =>
        body.Contains("doc.CellValue", StringComparison.Ordinal)
        || body.Contains("dic.RegistryValue", StringComparison.Ordinal);

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
