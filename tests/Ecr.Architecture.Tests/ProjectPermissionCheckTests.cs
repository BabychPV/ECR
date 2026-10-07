// tests/Ecr.Architecture.Tests/ProjectPermissionCheckTests.cs
using System.Reflection;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.TestKit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ФВ-6.14: проєктне функціональне право не перевіряється БЕЗ проєкту.
/// </summary>
/// <remarks>
/// ⛔ Предмет. Роль з областю дії дає функціональні права лише в проєктах
/// області (<see cref="AccessProfile.Has(string, int)"/>). Перевірка без
/// проєкту (<see cref="AccessProfile.Has(string)"/>,
/// <see cref="PermissionCheck.RequireAsync"/>) бачить лише ролі без області:
/// оператор «лише проєкту A» в такій точці отримує 403 там, де мав би
/// працювати. Перевірка «хоч у якомусь проєкті»
/// (<see cref="AccessProfile.HasInAnyProject"/>) без подальшої перевірки в
/// проєкті ресурсу — навпаки, дає йому діяти в чужому проєкті.
///
/// ⚠ Чому IL, а не текст джерел. Константу коду права (<c>const string
/// Permission = "…"</c>) компілятор ВБУДОВУЄ в місце виклику як <c>ldstr</c>,
/// тож у IL кожен аргумент-код лежить у зрізі аргументу виклику незалежно від
/// коментарів, переносів рядка й того, де оголошено константу. Зріз
/// аргументу рахується за стековою поведінкою інструкцій (без евристики
/// «найближчий рядок»). Сторожі по тексту в цьому репозиторії вже червоніли на
/// коментарі й переносі (<c>PartitionKeyQueryTests</c>).
///
/// Правила:
/// <list type="number">
/// <item>Перевірка БЕЗ проєкту (<see cref="CheckKind.Global"/>) проєктного коду
/// (усе поза <see cref="PermissionScopes.Global"/>) — лише як швидкий шлях у
/// парі з перевіркою в проєкті того самого коду в тому самому методі, або за
/// записом у <see cref="GlobalUseOfProjectCode"/> з причиною.</item>
/// <item>Вхідна перевірка «хоч у якомусь проєкті» (<see cref="CheckKind.Anywhere"/>)
/// — лише в парі з перевіркою В ПРОЄКТІ (<see cref="CheckKind.Project"/>) того
/// самого коду в тому самому методі.</item>
/// <item>Код, якого не видно в IL (параметр, поле), — лише в зареєстрованих
/// помічниках <see cref="ForwardingHelpers"/>.</item>
/// </list>
/// </remarks>
public sealed class ProjectPermissionCheckTests
{
    /// <summary>Вид перевірки права.</summary>
    public enum CheckKind
    {
        /// <summary>Без проєкту: лише ролі без області.</summary>
        Global,

        /// <summary>Вхід «хоч у якомусь проєкті».</summary>
        Anywhere,

        /// <summary>У конкретному проєкті.</summary>
        Project,
    }

    /// <summary>Будь-яка кількість параметрів (усі перевантаження помічника).</summary>
    private const int AnyArity = -1;

    /// <summary>Методи-перевірки: тип, ім'я, кількість параметрів, вид; рядковий параметр — код права.</summary>
    private static readonly (string Type, string Method, int Parameters, CheckKind Kind)[] Sinks =
    [
        ("Ecr.Application.Security.AccessProfile", "Has", 1, CheckKind.Global),
        ("Ecr.Application.Security.AccessProfile", "Has", 2, CheckKind.Project),
        ("Ecr.Application.Security.AccessProfile", "HasInAnyProject", 1, CheckKind.Anywhere),
        ("Ecr.Application.Security.PermissionCheck", "RequireAsync", 4, CheckKind.Global),
        ("Ecr.Application.Security.PermissionCheck", "RequireAnyAsync", 4, CheckKind.Global),
        ("Ecr.Application.Security.PermissionCheck", "RequireInAnyProjectAsync", 4, CheckKind.Anywhere),
        ("Ecr.Application.Security.PermissionCheck", "RequireIn", 3, CheckKind.Project),
        ("Ecr.Application.Security.PermissionCheck", "IsGrantedIn", 3, CheckKind.Project),
        ("Ecr.Application.Documents.ListDocumentsHandler", "ProfileAsync", 4, CheckKind.Anywhere),
        ("Ecr.Application.Documents.ListDocumentsHandler", "ReadableProjects", 2, CheckKind.Project),
        ("Ecr.Application.Documents.DocumentVisibility", "RequireVisibleAsync", 5, CheckKind.Project),
        ("Ecr.Application.Registries.RegistryAccess", "RequireAsync", AnyArity, CheckKind.Global),
        ("Ecr.Application.Security.UserAdministration", "ResolveAsync", AnyArity, CheckKind.Global),
        ("Ecr.Application.Templates.ListTemplatesHandler", "RequireAsync", AnyArity, CheckKind.Global),
        ("Ecr.Application.Calculations.MethodologyVersionScope", "RequireAsync", AnyArity, CheckKind.Global),
    ];

    /// <summary>
    /// Помічники, що передають код-параметр у перевірку. Їхні ВИКЛИКАЧІ
    /// перевіряються як звичайні точки (самі помічники — у <see cref="Sinks"/>).
    /// </summary>
    private static readonly HashSet<string> ForwardingHelpers = new(StringComparer.Ordinal)
    {
        "Ecr.Application.Security.PermissionCheck",
        "Ecr.Application.Documents.ListDocumentsHandler",
        "Ecr.Application.Documents.DocumentVisibility",
        "Ecr.Application.Registries.RegistryAccess",
        "Ecr.Application.Security.UserAdministration",
        "Ecr.Application.Templates.ListTemplatesHandler",
        "Ecr.Application.Calculations.MethodologyVersionScope",
    };

    /// <summary>
    /// Точки, де код права — не літерал (поле, вибір за умовою), з причиною.
    /// </summary>
    private static readonly Dictionary<string, string> DynamicCodeSites = new(StringComparer.Ordinal)
    {
        ["Ecr.Application.Integration.ListCollectionRunsHandler::HandleAsync"] =
            "RequireAnyAsync з переліком Integration.View/Integration.Manage — обидва глобальні.",
    };

    /// <summary>
    /// Перевірки БЕЗ проєкту проєктного коду — свідомо: у точці проєкту
    /// немає, і роль з областю тут не діє (безпечний бік).
    /// Ключ: <c>"ТипВерхньогоРівня::метод|код"</c>.
    /// </summary>
    private static readonly Dictionary<string, string> GlobalUseOfProjectCode = Allow(
        (
            "Calculation.View",
            "Методики, їхні версії, формули, правила й редактор виразів спільні для всіх проєктів — "
            + "перегляд каталогу методик не має проєкту; роль з областю його не дає.",
            [
                "Ecr.Api.Controllers.MethodologiesController::CategoryRule",
                "Ecr.Api.Controllers.MethodologiesController::Constants",
                "Ecr.Api.Controllers.MethodologiesController::Formulas",
                "Ecr.Api.Controllers.MethodologiesController::Outputs",
                "Ecr.Api.Controllers.MethodologiesController::RequiredInputs",
                "Ecr.Api.Controllers.MethodologiesController::Rules",
                "Ecr.Api.Controllers.MethodologiesController::Simulate",
                "Ecr.Api.Controllers.MethodologiesController::Tests",
                "Ecr.Application.Calculations.CompareMethodologyVersionsHandler::HandleAsync",
                "Ecr.Application.Calculations.ConstantUsageHandler::HandleAsync",
                "Ecr.Application.Calculations.GetMethodologyCategoryRuleHandler::HandleAsync",
                "Ecr.Application.Calculations.ListCalculationBindingsHandler::HandleAsync",
                "Ecr.Application.Calculations.ListMethodologiesHandler::HandleAsync",
                "Ecr.Application.Calculations.ListMethodologyConstantsHandler::HandleAsync",
                "Ecr.Application.Calculations.ListMethodologyFormulasHandler::HandleAsync",
                "Ecr.Application.Calculations.ListMethodologyOutputsHandler::HandleAsync",
                "Ecr.Application.Calculations.ListMethodologyPublicationsHandler::HandleAsync",
                "Ecr.Application.Calculations.ListMethodologyRequiredInputsHandler::HandleAsync",
                "Ecr.Application.Calculations.ListMethodologyRulesHandler::HandleAsync",
                "Ecr.Application.Calculations.ListMethodologyTestCasesHandler::HandleAsync",
                "Ecr.Application.Calculations.ListMethodologyVersionsHandler::HandleAsync",
                "Ecr.Application.Calculations.MethodologyCoverageHandler::HandleAsync",
                "Ecr.Application.Calculations.RuleCoverageHandler::HandleAsync",
                "Ecr.Application.Calculations.SimulateMethodologyHandler::HandleAsync",
                "Ecr.Application.Expressions.GetExpressionMetadataHandler::HandleAsync",
                "Ecr.Application.Expressions.ValidateExpressionHandler::HandleAsync",
            ]),
        (
            "Project.Manage",
            "Створення проєкту (і клон — це створення нового), політики періодів спільні для всіх "
            + "проєктів: дія поза будь-яким наявним проєктом, роль з областю її не дає.",
            [
                "Ecr.Application.Projects.CreateProjectHandler::HandleAsync",
                "Ecr.Application.Projects.CloneProjectHandler::HandleAsync",
                "Ecr.Application.Projects.CreatePeriodPolicyHandler::HandleAsync",
                "Ecr.Application.Projects.ListPeriodPoliciesHandler::HandleAsync",
                "Ecr.Application.Projects.UpdatePeriodPolicyHandler::HandleAsync",
            ]),
        (
            "Report.ViewRegulatory",
            "Перелік ОПИСІВ державних звітів спільний для всіх проєктів; зрізи — з проєктом.",
            ["Ecr.Application.Reporting.ListReportDefsHandler::HandleAsync"]),
        (
            "Document.Export",
            "Лише підказка-посилання в «Моїх задачах»; завантаження перевіряє право в проєкті "
            + "документа (DownloadExportHandler).",
            ["Ecr.Application.Integration.JobResultUrl::For"]));

    private static Dictionary<string, string> Allow(params (string Code, string Reason, string[] Sites)[] groups)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, reason, sites) in groups)
        {
            foreach (var site in sites)
            {
                result[$"{site}|{code}"] = reason;
            }
        }

        return result;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Проєктне_право_не_перевіряється_без_проєкту()
    {
        var (violations, _) = Scan(Checks().ToList());

        Assert.True(
            violations.Count == 0,
            "Перевірка проєктного права без проєкту (ФВ-6.14). Перейди на Has(code, projectId) / "
            + "RequireInAnyProjectAsync + RequireIn, або, якщо проєкту тут справді немає, допиши пару в "
            + "GlobalUseOfProjectCode з причиною:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations.Order(StringComparer.Ordinal)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Перелік_винятків_не_приховує_зробленого()
    {
        var (_, hits) = Scan(Checks().ToList());

        var stale = GlobalUseOfProjectCode.Keys.Concat(DynamicCodeSites.Keys)
            .Where(k => !hits.Contains(k))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0, "Винятки без відповідної перевірки — прибери: " + string.Join(", ", stale));
    }

    /// <summary>Сито бачить перевірки — інакше зламаний пошук дав би зелений тест.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Сито_бачить_перевірки_прав()
    {
        var all = Checks().ToList();

        Assert.Contains(all, c => c.Code == "Security.ManageUsers" && c.Kind == CheckKind.Global);
        Assert.Contains(all, c => c.Code == "Document.View" && c.Kind == CheckKind.Project);
        Assert.Contains(all, c => c.Code == "Document.View" && c.Kind == CheckKind.Anywhere);
        Assert.True(all.Count > 100, $"знайдено лише {all.Count} перевірок — сито зламане");
    }

    /// <summary>
    /// Правило ловить і пропущену перевірку в проєкті, і перевірку без проєкту.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутаційний доказ самого правила: ті самі дані сканування з
    /// вилученою перевіркою в проєкті (або з доданою глобальною) — червоні.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Правило_червоніє_на_пропущеній_перевірці_в_проєкті()
    {
        var all = Checks().ToList();

        var withoutProjectCheck = all
            .Where(c => !(c.Owner == "Ecr.Application.Documents.GetDocumentHandler" && c.Kind == CheckKind.Project))
            .ToList();
        Assert.Contains(
            Scan(withoutProjectCheck).Violations,
            v => v.Contains("GetDocumentHandler::HandleAsync|Document.View", StringComparison.Ordinal));

        var withGlobal = all.Append(new Check("Ecr.Application.Fake", "HandleAsync", "Document.Export", CheckKind.Global)).ToList();
        Assert.Contains(Scan(withGlobal).Violations, v => v.Contains("Ecr.Application.Fake::HandleAsync|Document.Export", StringComparison.Ordinal));
    }

    private static (List<string> Violations, HashSet<string> Hits) Scan(IReadOnlyList<Check> checks)
    {
        var violations = new List<string>();
        var hits = new HashSet<string>(StringComparer.Ordinal);

        var projectChecks = checks
            .Where(c => c.Kind == CheckKind.Project && c.Code is not null)
            .Select(c => $"{c.Owner}::{c.Method}|{c.Code}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var check in checks)
        {
            var site = $"{check.Owner}::{check.Method}";

            if (check.Code is null)
            {
                if (DynamicCodeSites.ContainsKey(site))
                {
                    hits.Add(site);
                }
                else if (!ForwardingHelpers.Contains(check.Owner))
                {
                    violations.Add($"  {site}: код права не літерал — зареєструй у DynamicCodeSites з причиною.");
                }

                continue;
            }

            if (PermissionScopes.IsGlobal(check.Code) || check.Kind == CheckKind.Project)
            {
                continue;
            }

            var key = $"{site}|{check.Code}";
            if (GlobalUseOfProjectCode.ContainsKey(key))
            {
                hits.Add(key);
                continue;
            }

            // ⚠ Глобальна перевірка в парі з проєктною — «швидкий шлях»: право з
            // ролі без області діє в усіх проєктах, тож вона лише вужча.
            if (projectChecks.Contains(key))
            {
                continue;
            }

            violations.Add(check.Kind == CheckKind.Anywhere
                ? $"  {key}: вхід «хоч у якомусь проєкті» без перевірки в проєкті ресурсу"
                : $"  {key}");
        }

        return (violations, hits);
    }

    /// <summary>Одна перевірка права в IL.</summary>
    /// <param name="Owner">Тип верхнього рівня (замикання й машини станів згорнуто).</param>
    /// <param name="Method">Вихідний метод (для машини станів і лямбди — той, де їх написано).</param>
    /// <param name="Code">Код права; <c>null</c> — не літерал.</param>
    /// <param name="Kind">Вид перевірки.</param>
    public sealed record Check(string Owner, string Method, string? Code, CheckKind Kind);

    private static IEnumerable<Check> Checks()
    {
        Assembly[] assemblies =
        [
            typeof(AccessProfile).Assembly,
            typeof(Ecr.Infrastructure.Persistence.EcrDbContext).Assembly,
            typeof(Ecr.Api.Controllers.SecurityController).Assembly,
            typeof(Ecr.Adapters.Excel.ExcelImporter).Assembly,
        ];

        foreach (var assembly in assemblies)
        {
            using var module = ModuleDefinition.ReadModule(assembly.Location);

            foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
            {
                foreach (var check in ChecksIn(method))
                {
                    yield return check;
                }
            }
        }
    }

    private static IEnumerable<Check> ChecksIn(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;

        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.Operand is not MethodReference target || SinkOf(target) is not { } kind)
            {
                continue;
            }

            var (owner, name) = Origin(method);

            // Метод-група (`permissions.Any(profile.Has)`) — код не видно.
            if (instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn)
            {
                yield return new Check(owner, name, null, kind);
                continue;
            }

            if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt))
            {
                continue;
            }

            var codes = new List<string>();
            for (var p = 0; p < target.Parameters.Count; p++)
            {
                var type = target.Parameters[p].ParameterType.FullName;
                if (type is not ("System.String" or "System.Collections.Generic.IReadOnlyList`1<System.String>"))
                {
                    continue;
                }

                // Позиція аргументу від вершини стеку: останній параметр — 0.
                var fromTop = target.Parameters.Count - 1 - p;
                if (ArgumentSlice(method, i, fromTop) is not var (start, end))
                {
                    continue;
                }

                for (var k = start; k <= end; k++)
                {
                    if (instructions[k].OpCode.Code == Code.Ldstr && instructions[k].Operand is string text)
                    {
                        codes.Add(text);
                    }
                }

                // Для одного рядка — лише якщо весь зріз і є цей літерал.
                if (type == "System.String" && !(start == end && instructions[start].OpCode.Code == Code.Ldstr))
                {
                    codes.Clear();
                }
            }

            if (codes.Count == 0)
            {
                yield return new Check(owner, name, null, kind);
                continue;
            }

            foreach (var code in codes)
            {
                yield return new Check(owner, name, code, kind);
            }
        }
    }

    private static CheckKind? SinkOf(MethodReference target)
    {
        foreach (var (type, name, parameters, kind) in Sinks)
        {
            if (target.DeclaringType.FullName == type && target.Name == name
                && (parameters == AnyArity || target.Parameters.Count == parameters))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>
    /// Інструкції, що обчислюють аргумент на глибині <paramref name="fromTop"/>
    /// перед викликом <paramref name="callIndex"/>, — за стековою поведінкою.
    /// </summary>
    private static (int Start, int End)? ArgumentSlice(MethodDefinition method, int callIndex, int fromTop)
    {
        var instructions = method.Body.Instructions;
        var depth = fromTop;
        var producer = -1;

        for (var i = callIndex - 1; i >= 0; i--)
        {
            var (pop, push) = Effect(instructions[i]);
            if (depth < push)
            {
                producer = i;
                break;
            }

            depth = depth - push + pop;
        }

        if (producer < 0)
        {
            return null;
        }

        var need = Effect(instructions[producer]).Pop;
        var start = producer;
        while (need > 0 && start > 0)
        {
            start--;
            var (pop, push) = Effect(instructions[start]);
            need = need - push + pop;
        }

        return (start, producer);
    }

    private static (int Pop, int Push) Effect(Instruction instruction)
    {
        var opcode = instruction.OpCode;

        if (opcode.Code is Code.Call or Code.Callvirt or Code.Newobj && instruction.Operand is MethodReference m)
        {
            var pops = m.Parameters.Count + (m.HasThis && opcode.Code != Code.Newobj ? 1 : 0);
            var pushes = opcode.Code == Code.Newobj || m.ReturnType.FullName != "System.Void" ? 1 : 0;
            return (pops, pushes);
        }

        var pop = opcode.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
                or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
                or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8
                or StackBehaviour.Popref_popi_popref => 3,
            _ => 0,
        };

        var push = opcode.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1_push1 => 2,
            _ => 1,
        };

        return (pop, push);
    }

    /// <summary>Тип верхнього рівня і вихідний метод (крізь машини станів і замикання).</summary>
    private static (string Owner, string Method) Origin(MethodDefinition method)
    {
        var type = method.DeclaringType;
        var name = SourceName(method.Name) ?? method.Name;

        while (type.DeclaringType is not null)
        {
            // Машина станів `<HandleAsync>d__3` → `HandleAsync`.
            if (SourceName(type.Name) is { Length: > 0 } fromType)
            {
                name = fromType;
            }

            type = type.DeclaringType;
        }

        return (type.FullName, name);
    }

    /// <summary><c>&lt;HandleAsync&gt;b__0</c> → <c>HandleAsync</c>; інакше <c>null</c>.</summary>
    private static string? SourceName(string name)
    {
        // Асинхронна лямбда: `<<HandleAsync>b__0>d` — ім'я вкладене двічі.
        var open = 0;
        while (open < name.Length && name[open] == '<')
        {
            open++;
        }

        var close = name.IndexOf('>', StringComparison.Ordinal);
        return open == 0 || close <= open ? null : name[open..close];
    }
}
