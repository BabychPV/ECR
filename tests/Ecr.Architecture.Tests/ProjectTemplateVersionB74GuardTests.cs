// tests/Ecr.Architecture.Tests/ProjectTemplateVersionB74GuardTests.cs
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Documents;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B74 P0 (ADR <c>docs/build/ADR-B74-PROJECT-TEMPLATES.md</c>): проєкт отримує кілька шаблонів, версія стає властивістю
/// пари (проєкт, шаблон) / документа, а <c>Project.TemplateVersionId</c> зникає в P6. До того жодного НОВОГО читання поля
/// не з'являється: ні в C#, ні в сирому SQL. Кожне наявне читання - у переліку з фазою, у якій воно переїде.
/// </summary>
/// <remarks>
/// ⚠ Чому не текст джерел для C#: старий сторож <see cref="ProjectTemplateVersionReadGuardTests"/> рахує регулярний
/// вираз по тексту з коментарями і за іменем змінної (<c>project|p|pr…</c>) - тож рахує коментар у
/// <c>ResourceNameResolver</c> і не бачить <c>source.TemplateVersionId</c> у <c>CloneProjectHandler</c>. Тут C# читається
/// по IL (Mono.Cecil, як <see cref="ExternalRegistryManualEditGuardTests"/>): виклик/<c>ldtoken</c> гетера
/// <c>Project.get_TemplateVersionId</c> - і прямий доступ, і LINQ-вираз EF; коментарі й імена змінних не впливають;
/// замикання й машини станів згортаються до типу верхнього рівня.
/// Сирий SQL (рядки в C# і <c>*.sql</c> під <c>src/</c>, без <c>Migrations/</c>) - текстом після вирізання коментарів
/// <c>//</c>, <c>/* */</c>, <c>--</c>: читанням вважається <c>&lt;псевдонім doc.Project&gt;.TemplateVersionId</c> або голий
/// <c>TemplateVersionId</c> в операторі (між <c>;</c>), де <c>doc.Project</c> без псевдоніма. Самоперевірки нижче доводять
/// обидва сканери на синтетиці: без них «зелено» могло б означати «сканер нічого не бачить».
/// Храповик: новий тип/файл - червоно; лічильник вище за перелік - червоно; нижче - червоно (зменш перелік).
/// </remarks>
public sealed partial class ProjectTemplateVersionB74GuardTests
{
    private const string ProjectType = "Ecr.Domain.Entities.Documents.Project";
    private const string Getter = "get_TemplateVersionId";

    /// <summary>C#: тип верхнього рівня → кількість IL-посилань на гетер (станом на origin/main e8a5c9ffc).</summary>
    private static readonly Dictionary<string, int> CodeReaders = new(StringComparer.Ordinal)
    {
        // P2 (ядро): єдина точка «яка версія» → (проєкт, шаблон) / документ.
        ["Ecr.Infrastructure.Persistence.ProjectVersionTimelineService"] = 1,          // P2: переїде на ProjectTemplate
        ["Ecr.Infrastructure.Persistence.DocumentStore"] = 9,                          // P2: версія документа з Document.TemplateId
        ["Ecr.Infrastructure.Persistence.MonthColumnRouter"] = 2,                      // P2: версія документа, не проєкту
        ["Ecr.Application.Documents.GetDocumentTemplateHandler"] = 1,                  // P2: версія документа

        // P2 (безпека; з диференційними Deny-тестами на реальному шляху).
        ["Ecr.Infrastructure.Security.AccessDecisionService"] = 8,                     // P2
        ["Ecr.Infrastructure.Security.EffectiveAccessStore"] = 1,                      // P2
        ["Ecr.Infrastructure.Security.ResourceNameResolver"] = 1,                      // P2

        // P2 (черги погодження, періоди, задачі).
        ["Ecr.Infrastructure.Persistence.ApprovalQueueStore"] = 3,                     // P2
        ["Ecr.Application.Periods.ReopenPeriodHandler"] = 1,                           // P2: мітка версії в журналі
        ["Ecr.Application.Periods.SetCurrentPeriodHandler"] = 1,                       // P2: мітка версії в журналі
        ["Ecr.Application.Periods.PeriodStateCatchUp"] = 1,                            // P2: мітка версії в журналі
        ["Ecr.Infrastructure.Jobs.PeriodStateJob"] = 1,                                // P2: мітка версії в журналі
        ["Ecr.Infrastructure.Persistence.CalculationResultStore"] = 1,                 // P2: зріз без періоду
        ["Ecr.Infrastructure.Persistence.CollectionStore"] = 1,                        // P2: збір пише в версію шаблону
        ["Ecr.Infrastructure.Persistence.SourceEventMapStore"] = 1,                    // P2
        ["Ecr.Infrastructure.Jobs.ConsistencyCheckJob"] = 1,                           // P2 (P5 - нові перевірки)
        ["Ecr.Infrastructure.Startup.MetadataWarmup"] = 1,                             // P2: прогрів усіх CurrentTemplateVersionId
        ["Ecr.Infrastructure.Persistence.TemplateVersionStore"] = 5,                   // P2: лічильники «проєкти на версії»

        // P3 (шляхи запису): клон проєкту, перенос версії - переїдуть на (проєкт, шаблон).
        ["Ecr.Application.Projects.CloneProjectHandler"] = 2,                          // P3
        ["Ecr.Application.Documents.VersionMigration.MigrateDocumentVersionHandler"] = 3, // P3
        ["Ecr.Infrastructure.Persistence.DocumentVersionMigrationStore"] = 2,          // P3

        // P6: мапінг EF (FK_Project_TV) зникає разом із колонкою.
        ["Ecr.Infrastructure.Persistence.Configurations.ProjectConfiguration"] = 1,    // P6
    };

    /// <summary>Сирий SQL: файл → кількість читань/записів <c>doc.Project.TemplateVersionId</c>.</summary>
    private static readonly Dictionary<string, int> SqlReaders = new(StringComparer.Ordinal)
    {
        ["src/Ecr.Infrastructure/Persistence/DocumentStore.cs"] = 1,                   // P2: LockProjectTemplateVersionAsync
        ["src/Ecr.Infrastructure/Persistence/SheetEditGate.cs"] = 1,                   // P2: версія без періоду
        ["src/Ecr.Infrastructure/Persistence/ProjectStore.cs"] = 1,                    // P2: картка видалення проєкту
        ["src/Ecr.Infrastructure/Persistence/TemplateVersionDiscardStore.cs"] = 1,     // P2: «чи вживається версія»
        ["src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs"] = 3,   // P3: блок версії, FinishSql (читання + UPDATE)
        // P2/P5: ремонт сегментів за версією проєкту (11); P6: ремонт подвійного запису B74:project-templates (3).
        ["src/Ecr.Infrastructure/Persistence/Sql/seed/99-epilogue.sql"] = 14,
    };

    [Fact]
    [Trait("Requirement", "B74")]
    public void Нових_читань_Project_TemplateVersionId_у_коді_немає()
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var assembly in ProductionAssemblies())
        {
            foreach (var owner in GetterReferences(assembly.Location))
            {
                found[owner] = found.GetValueOrDefault(owner) + 1;
            }
        }

        AssertRatchet(found, CodeReaders, "типом",
            "Версію документа/періоду дає IProjectVersionTimeline / EcrDbContext.VersionOf, а після P2 - версія документа "
            + "(Document.TemplateId + шкала проєкт×шаблон). Project.TemplateVersionId зникає в P6 (B74).");
    }

    [Fact]
    [Trait("Requirement", "B74")]
    public void Нових_читань_doc_Project_TemplateVersionId_у_сирому_SQL_немає()
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in SourceTree.Walk(Path.Combine(SourceTree.Root, "src")))
        {
            var relative = Path.GetRelativePath(SourceTree.Root, path).Replace('\\', '/');
            if (!(relative.EndsWith(".cs", StringComparison.Ordinal) || relative.EndsWith(".sql", StringComparison.Ordinal))
                || relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            var hits = SqlReads(File.ReadAllText(path));
            if (hits.Count > 0)
            {
                found[relative] = hits.Count;
                lines[relative] = string.Join(",", hits);
            }
        }

        AssertRatchet(found, SqlReaders, "файлом",
            "Сирий SQL читає doc.Project.TemplateVersionId (рядки: "
            + string.Join("; ", found.Where(f => !SqlReaders.ContainsKey(f.Key) || f.Value > SqlReaders[f.Key])
                .Select(f => $"{f.Key}:{lines[f.Key]}"))
            + "). Версія за період - doc.fn_VersionOf; після P2 - версія документа (B74).");
    }

    [Fact]
    [Trait("Requirement", "B74")]
    public void Сканер_IL_бачить_прямий_доступ_і_вираз_EF()
    {
        // Самоперевірка: IlProbe нижче читає поле двічі - викликом і в Expression. Сканер мусить побачити обидва.
        var owners = GetterReferences(typeof(IlProbe).Assembly.Location).ToList();
        Assert.Equal(2, owners.Count(o => o == typeof(ProjectTemplateVersionB74GuardTests).FullName));
    }

    [Theory]
    [Trait("Requirement", "B74")]
    [InlineData("SELECT p.TemplateVersionId FROM doc.Project p WHERE p.Id = 1;", 1)]
    [InlineData("SELECT pr.[TemplateVersionId] FROM [doc].[Project] AS pr;", 1)]
    [InlineData("SELECT TemplateVersionId AS Value FROM doc.Project WITH (UPDLOCK) WHERE Id = 1", 1)]
    [InlineData("UPDATE doc.Project SET TemplateVersionId = @t WHERE Id = @p;", 1)]
    [InlineData("-- p.TemplateVersionId FROM doc.Project p\nSELECT 1;", 0)]
    [InlineData("/* SELECT p.TemplateVersionId FROM doc.Project p */ SELECT 1;", 0)]
    [InlineData("// var x = \"SELECT p.TemplateVersionId FROM doc.Project p\";\nvar y = 1;", 0)]
    [InlineData("SELECT s.TemplateVersionId FROM cfg.SheetDef s JOIN doc.Project p ON p.Id = 1;", 0)]
    [InlineData("SELECT t.TemplateVersionId FROM doc.ProjectTemplateVersion t;", 0)]
    [InlineData("SELECT TemplateVersionId FROM cfg.SheetDef; SELECT 1 FROM doc.Project WHERE Id = 1;", 0)]
    [InlineData("SELECT @TemplateVersionId FROM doc.Project WHERE Id = 1;", 0)]
    public void Сканер_SQL_бачить_читання_і_не_бачить_коментарів(string text, int expected)
        => Assert.Equal(expected, SqlReads(text).Count);

    private static IEnumerable<Assembly> ProductionAssemblies() =>
    [
        typeof(Project).Assembly,
        typeof(Ecr.Application.Ports.IProjectVersionTimeline).Assembly,
        typeof(Ecr.Infrastructure.Persistence.EcrDbContext).Assembly,
        typeof(Ecr.Api.Controllers.SecurityController).Assembly,
        typeof(Ecr.Adapters.Excel.ExcelImporter).Assembly,
        typeof(Ecr.Calculations.CalculationInputBuilder).Assembly,
        typeof(Ecr.Expressions.Binding.PredicateValidator).Assembly,
        typeof(Ecr.Adapters.PiAf.CollectionRunner).Assembly,
        typeof(Ecr.Adapters.Sql.SqlDataSource).Assembly,
    ];

    /// <summary>Тип верхнього рівня для кожного IL-посилання на <c>Project.get_TemplateVersionId</c>.</summary>
    private static List<string> GetterReferences(string assemblyPath)
    {
        var owners = new List<string>();
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Ldtoken or Code.Ldftn or Code.Ldvirtftn
                    && instruction.Operand is MethodReference target
                    && target.Name == Getter
                    && target.DeclaringType.FullName == ProjectType)
                {
                    owners.Add(Owner(method.DeclaringType));
                }
            }
        }

        return owners;
    }

    private static string Owner(TypeDefinition type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type.FullName;
    }

    /// <summary>Номери рядків читань <c>doc.Project.TemplateVersionId</c> у тексті (C# або SQL).</summary>
    internal static List<int> SqlReads(string text)
    {
        // Коментарі - пробілами тієї ж довжини, переноси рядків зберігаються: номери рядків лишаються точними.
        var clean = Comment().Replace(text, m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));
        var result = new List<int>();
        var offset = 0;
        foreach (var statement in clean.Split(';'))
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unaliased = false;
            foreach (Match binding in ProjectBinding().Matches(statement))
            {
                var alias = binding.Groups["alias"];
                if (alias.Success && !SqlKeywords.Contains(alias.Value))
                {
                    aliases.Add(alias.Value);
                }
                else
                {
                    unaliased = true;
                }
            }

            var hits = aliases
                .SelectMany(a => Regex.Matches(statement, $@"(?<![\w.\]]){Regex.Escape(a)}\s*\.\s*\[?TemplateVersionId\b",
                    RegexOptions.IgnoreCase).Cast<Match>())
                .Concat(unaliased ? BareColumn().Matches(statement) : Enumerable.Empty<Match>());

            result.AddRange(hits.Select(h => 1 + clean.AsSpan(0, offset + h.Index).Count('\n')));
            offset += statement.Length + 1;
        }

        result.Sort();
        return result;
    }

    private static void AssertRatchet(Dictionary<string, int> found, Dictionary<string, int> allowed, string unit, string hint)
    {
        var unlisted = found.Where(f => !allowed.ContainsKey(f.Key)).Select(f => $"{f.Key} ({f.Value})").ToList();
        Assert.True(unlisted.Count == 0,
            $"Нове читання Project.TemplateVersionId {unit} поза переліком B74:\n  {string.Join("\n  ", unlisted)}\n{hint}");

        var grown = found.Where(f => allowed.TryGetValue(f.Key, out var max) && f.Value > max)
            .Select(f => $"{f.Key}: {f.Value} > {allowed[f.Key]}").ToList();
        Assert.True(grown.Count == 0, $"Лічильник читань зріс (храповик лише вниз):\n  {string.Join("\n  ", grown)}\n{hint}");

        var stale = allowed.Where(a => found.GetValueOrDefault(a.Key) < a.Value)
            .Select(a => $"{a.Key}: дозволено {a.Value}, є {found.GetValueOrDefault(a.Key)}").ToList();
        Assert.True(stale.Count == 0,
            "Читань поменшало - зменш перелік (храповик; переїзд у P2/P3 закриває рядок):\n  " + string.Join("\n  ", stale));
    }

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "SET", "WITH", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "ON", "GROUP", "ORDER",
        "UNION", "EXCEPT", "INTERSECT", "AND", "OR", "SELECT", "FROM", "OUTPUT", "VALUES", "WHEN", "THEN", "ELSE",
        "END", "GO", "OPTION", "HAVING", "INSERT", "UPDATE", "DELETE", "MERGE", "USING", "IF", "BEGIN", "RETURN",
        "DECLARE", "EXEC", "EXECUTE", "PIVOT", "UNPIVOT", "TABLESAMPLE", "AS",
    };

    // `//` не після `:` / лапок / слова (не `http://`, не `"//"`), `/* */`, `--` з пробілом або кінцем рядка (не `i--`).
    [GeneratedRegex(@"(?<![:""'\w/])//[^\r\n]*|/\*[\s\S]*?\*/|(?<![\w)\]'""\-])--(?=[ \t\r\n]|$)[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex Comment();

    // doc.Project / [doc].[Project] (не ProjectTemplateVersion), необов'язково AS і псевдонім.
    [GeneratedRegex(@"\[?doc\]?\s*\.\s*\[?Project\]?(?!\w)(?:\s+(?:AS\s+)?\[?(?<alias>[A-Za-z_]\w*)\]?)?", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectBinding();

    [GeneratedRegex(@"(?<![\w.@\[])\[?TemplateVersionId\b", RegexOptions.IgnoreCase)]
    private static partial Regex BareColumn();

    /// <summary>Еталон для самоперевірки IL: два читання - пряме і в дереві виразу.</summary>
    private static class IlProbe
    {
        public static int Direct(Project project) => project.TemplateVersionId;

        public static Expression<Func<Project, int>> Tree() => p => p.TemplateVersionId;
    }
}
