using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Цілісність контракту: те, що оголошене, має існувати, а те, що існує, —
/// бути оголошеним.
/// </summary>
public sealed class ContractIntegrityTests
{
    /// <summary>
    /// Порти, у яких реалізацій свідомо більше однієї.
    /// </summary>
    /// <remarks>
    /// Резолвляться як колекція, а не як одиничний сервіс: транспортів
    /// зовнішніх даних два (SQL-клієнт і Web API), а модулів розрахунку може
    /// не бути жодного — і відсутність не має ламати систему (K-1).
    ///
    /// <c>IBackgroundJob</c> тут за іншою причиною: це точка розширення за
    /// побудовою. Кожна фонова задача — окрема реалізація, і їх рівно стільки,
    /// скільки задач (архівація, збір, перевірка інваріантів, стан періодів,
    /// сповіщення, запас партицій, пошук осиротілих рядків).
    /// </remarks>
    private static readonly string[] Plural =
        ["IExternalDataSource", "ICalculationModule", "IBackgroundJob"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Кожен_порт_має_рівно_одну_реалізацію_окрім_явно_множинних()
    {
        var ports = SourceTree.Production("Ecr.Application")
            .Where(f => f.Path.Contains("/Ports/", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(f.Text, @"public interface (I\w+)")
                                  .Select(m => m.Groups[1].Value))
            .ToList();

        Assert.NotEmpty(ports);

        // ⚠ Шукаються саме ОГОЛОШЕННЯ КЛАСІВ, а не згадки імені. Наївне
        // «двокрапка і десь поруч ім'я» ловить XML-документацію і
        // перелічення в коментарях: IBackgroundJob давав 11 «реалізацій»,
        // жодної з яких не існує.
        var declarations = SourceTree
            .Production("Ecr.Infrastructure", "Ecr.Adapters.PiAf", "Ecr.Adapters.Excel",
                        "Ecr.Expressions", "Ecr.Calculations", "Ecr.Api")
            .SelectMany(f => Regex.Matches(f.Text, @"(?m)^\s*(?:public|internal)\s+(?:sealed\s+)?class\s+\w+[^\n{{]*:\s*([^\n{{]+)")
                                  .Select(m => m.Groups[1].Value))
            .ToList();

        var counts = ports.ToDictionary(
            port => port,
            port => declarations.Count(
                d => Regex.IsMatch(d, $@"\b{port}\b")));

        // ⚠ ДВІ реалізації одного порту без явного дозволу — це або забутий
        // прототип, або дві правди про одне й те саме. DI резолвить останню
        // зареєстровану, і яку саме — залежить від порядку рядків.
        var duplicated = counts
            .Where(kv => kv.Value > 1 && !Plural.Contains(kv.Key, StringComparer.Ordinal))
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();

        Assert.Empty(duplicated);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Коди_помилок_унікальні_і_відповідають_формату()
    {
        var codes = typeof(Ecr.Api.Errors.ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (Name: f.Name, Value: (string)f.GetRawConstantValue()!))
            .ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, c => Assert.Matches(@"^ECR-[A-Z]+-\d{4}$", c.Value));

        // Один код на два стани означає, що клієнт не може їх розрізнити —
        // а весь сенс коду саме в цьому.
        var duplicates = codes.GroupBy(c => c.Value, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Name))}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Кожне_значення_EditDenyReason_повертається_хоча_б_одним_шляхом()
    {
        // ⚠ Причина, яку не повертає жоден шлях, — гірше за її відсутність:
        // вона є в контракті, клієнт готує для неї текст, а користувач цього
        // тексту не побачить ніколи. Перевіряється саме ПОВЕДІНКА: кожен
        // сценарій нижче проганяється через EditRules, і зібрані причини
        // звіряються з переліком.
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage)
            .Build();

        var stranger = new AccessBuilder { UserId = 8 }.Build();

        var simulation = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage)
            .Build(simulation: true, simulatedFor: 42);

        var produced = new HashSet<EditDenyReason>
        {
            EditDenyReason.None,

            EditRules.CanEdit(simulation, AccessBuilder.Cell()).Reason,
            EditRules.CanEdit(profile, AccessBuilder.Cell(project: ProjectStatus.Archived)).Reason,
            EditRules.CanEdit(profile, AccessBuilder.Cell(archiving: true)).Reason,
            EditRules.CanEdit(profile, AccessBuilder.Cell(period: PeriodState.Scheduled)).Reason,
            EditRules.CanEdit(profile, AccessBuilder.Cell(period: PeriodState.Closed)).Reason,
            EditRules.CanEdit(profile, AccessBuilder.Cell(outOfWindow: true)).Reason,
            EditRules.CanEdit(profile, AccessBuilder.Cell(sheet: DocumentStatus.Submitted)).Reason,
            EditRules.CanEdit(profile, AccessBuilder.Cell(sheet: DocumentStatus.Approved)).Reason,
            EditRules.CanEdit(profile, AccessBuilder.Cell(computed: true)).Reason,
            EditRules.CanEdit(profile, AccessBuilder.Cell(columnReadOnly: true)).Reason,
            EditRules.CanEdit(profile, AccessBuilder.Cell(rowReadOnly: true)).Reason,
            EditRules.CanEdit(stranger, AccessBuilder.Cell()).Reason,

            // BusinessRule приходить не з доступу, а з валідації: подання з
            // незакритими помилками (ФВ-5.19).
            EditRules.CanSubmit(profile, AccessBuilder.Cell(), hasBlockingErrors: true).Reason,
        };

        var unreachable = Enum.GetValues<EditDenyReason>()
            .Except(produced)
            .Select(r => r.ToString())
            .ToList();

        Assert.Empty(unreachable);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Кожен_ендпоінт_із_таблиці_бюджету_має_метрику()
    {
        // Правило контракту (`02-contracts.md` §11): операція з таблиці
        // бюджету зобов'язана мати метрику. Без вимірювання «вкладаємося»
        // означає «здається швидким тому, хто це писав».
        var operations = BudgetOperations();
        Assert.NotEmpty(operations);

        var mapped = typeof(Ecr.Api.Observability.BudgetMetrics)
            .GetProperty(nameof(Ecr.Api.Observability.BudgetMetrics.ByOperation))!
            .GetValue(null) as IReadOnlyDictionary<string, string>;

        Assert.NotNull(mapped);

        var unmeasured = operations
            .Where(op => !mapped!.Keys.Any(key => op.StartsWith(key, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unmeasured);

        // ⛔ І зворотний бік: метрика, названа в зіставленні, мусить існувати
        // в таблиці метрик контракту. Інакше зіставлення посилалося б на
        // лічильник, якого ніхто не створює, і перевірка була б формальністю.
        var declared = DeclaredMetrics();
        Assert.NotEmpty(declared);

        Assert.Empty(mapped!.Values.Distinct().Except(declared, StringComparer.Ordinal));
    }

    /// <summary>Операції з таблиць бюджету `tz/08` §8.2 і §8.3.</summary>
    private static List<string> BudgetOperations()
    {
        var path = Path.Combine(SourceTree.Root, "docs", "tz", "08-nfr.md");
        var text = File.ReadAllText(path);

        var section = text[text.IndexOf("## 8.2", StringComparison.Ordinal)..
                           text.IndexOf("## 8.4", StringComparison.Ordinal)];

        return Regex.Matches(section, @"^\|\s*(?:\*\*)?([^|*][^|]*?)(?:\*\*)?\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(op => op.Length > 0
                         && !op.StartsWith("Операція", StringComparison.Ordinal)
                         && !op.StartsWith("---", StringComparison.Ordinal))
            .Select(op => op.Replace("ПРД-13. ", string.Empty, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Метрики, оголошені в таблиці `02-contracts.md` §11.</summary>
    private static HashSet<string> DeclaredMetrics()
    {
        var path = Path.Combine(SourceTree.Root, "docs", "build", "02-contracts.md");

        return Regex.Matches(File.ReadAllText(path), @"^\|\s*`(ecr\.[a-z_.]+)`\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Публічні_типи_мають_XML_документацію()
    {
        // XML-doc тут не формальність: `GenerateDocumentationFile` увімкнений,
        // і без коментаря збірка дає CS1591. Тест фіксує, що послаблення
        // NoWarn не розповзлося на продуктивні проєкти.
        var projects = Directory.EnumerateFiles(
            Path.Combine(SourceTree.Root, "src"), "*.csproj", SearchOption.AllDirectories);

        var offenders = new List<string>();
        foreach (var project in projects)
        {
            var text = File.ReadAllText(project);
            if (text.Contains("CS1591", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(project));
            }
        }

        Assert.Empty(offenders);
    }
}
