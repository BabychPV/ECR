using System.Reflection;
using System.Text.RegularExpressions;
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
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Кожен_ендпоінт_із_таблиці_бюджету_має_метрику()
        => Assert.Fail("not implemented");

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
