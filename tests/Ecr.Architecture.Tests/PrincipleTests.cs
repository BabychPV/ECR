using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Наскрізні принципи, які легко порушити ненавмисно.
/// </summary>
/// <remarks>
/// ⚠ Тести з'явилися після аудиту: матриця трасування (<c>D-135</c>) показала,
/// що ці вимоги не мали доказу. Вони не про поведінку окремої функції, а про
/// те, чого в системі **не буває** — і саме такі вимоги тихо порушуються при
/// найближчій правці, бо нічого не падає.
/// </remarks>
public sealed partial class PrincipleTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-0.3")]
    [Trait("Requirement", "ФВ-10.1")]
    public void База_не_рахує_обчислення_живуть_у_сервісі()
    {
        // ⛔ Причина не в чистоті архітектури. Обчислення в БД — це те, від чого
        // система тікає: у чинному рішенні логіка розмазана по 127 процедурах
        // і CLR-складанню, і саме тому її неможливо ані відтворити, ані
        // перевірити. Повторити це в новій системі означало б не переписати
        // її, а переписати той самий клубок іншою мовою.
        var scripts = Directory.EnumerateFiles(
            Path.Combine(SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql"),
            "*.sql");

        var offenders = new List<string>();

        foreach (var file in scripts)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // CLR: `CREATE ASSEMBLY`, `EXTERNAL NAME`, вмикання `clr enabled`.
            if (ClrRegex.IsMatch(text))
            {
                offenders.Add($"{name}: CLR");
            }

            // Індексована в'юха з обчисленням: `WITH SCHEMABINDING` разом із
            // агрегатом. Проєкція без агрегатів дозволена — вона не рахує.
            if (text.Contains("SCHEMABINDING", StringComparison.OrdinalIgnoreCase)
                && AggregateRegex.IsMatch(text))
            {
                offenders.Add($"{name}: індексована вʼюха з агрегатом");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-12.6")]
    public void Обслуговування_бази_не_використовує_shrink()
    {
        // ⛔ `SHRINK` фрагментує індекси до непридатності і повертає файлу той
        // самий розмір за тиждень. Це операція, яку виконують «щоб звільнити
        // місце», а розплачуються за неї місяцями повільних запитів.
        //
        // ⚠ Перевіряються і скрипти розгортання, і регламентні задачі Agent:
        // саме туди його додають «тимчасово».
        var roots = new[]
        {
            Path.Combine(SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql"),
            Path.Combine(SolutionRoot(), "tools"),
        };

        var offenders = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, "*.ps1", SearchOption.AllDirectories)))
            .Where(file => ShrinkRegex.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-1.15")]
    public void Проєкти_і_періоди_не_видаляються_ні_кодом_ні_скриптом()
    {
        // ⛔ На проєкт і період посилаються дані, аудит, зрізи і результати
        // розрахунку (`D-25`). Видалення тут — не звільнення місця, а розрив
        // доказової бази: подана форма перестає мати період, за який її подано.
        //
        // ⚠ Життєвий цикл закінчується станом `Archived`, і саме тому в коді
        // немає жодного `Remove` для цих сутностей.
        var offenders = new List<string>();

        foreach (var file in SourceTree.Production("Ecr.Application", "Ecr.Infrastructure", "Ecr.Api"))
        {
            var text = WithoutComments(file.Text);

            foreach (Match match in RemoveProjectRegex.Matches(text))
            {
                offenders.Add($"{Path.GetFileName(file.Path)}: {match.Value.Trim()}");
            }
        }

        var scripts = Path.Combine(SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql");
        foreach (var file in Directory.EnumerateFiles(scripts, "*.sql"))
        {
            foreach (Match match in DeleteProjectRegex.Matches(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetFileName(file)}: {match.Value.Trim()}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-2.14")]
    public void Налаштування_живуть_у_базі_а_не_в_конфігураційних_файлах()
    {
        // ⛔ `appsettings.json` містить лише те, що потрібно, аби ПІДКЛЮЧИТИСЯ
        // і піднятися: рядок підключення береться з оточення (`D-11`), решта —
        // таймаути, режими старту, імена секретів. Джерела, розклади, політики
        // і мапінги — дані (`ФВ-2.14`), бо їх міняє адміністратор без релізу.
        //
        // ⚠ Порушення тут виглядає нешкідливо: «додам розклад у конфіг, поки
        // немає екрана». Після цього зміна розкладу вимагає деплою назавжди.
        var forbidden = new[] { "Schedule", "Cron", "Mapping", "Policy", "SourceEntity", "Registry" };

        var offenders = Directory
            .EnumerateFiles(Path.Combine(SolutionRoot(), "src", "Ecr.Api"), "appsettings*.json")
            .SelectMany(file =>
            {
                var text = File.ReadAllText(file);

                return forbidden
                    .Where(key => text.Contains($"\"{key}", StringComparison.Ordinal))
                    .Select(key => $"{Path.GetFileName(file)}: {key}");
            })
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.10")]
    [Trait("Requirement", "ФВ-10.3")]
    public void Клієнт_не_містить_бізнес_правил_яких_немає_на_сервері()
    {
        // ⛔ Клієнтська валідація дублює серверну заради швидкості відгуку;
        // авторитетна — серверна. Правило, яке існує ЛИШЕ на клієнті, — це
        // правило, яке можна обійти запитом, і про яке сервер не знає.
        //
        // ⚠ Перевіряється не «немає перевірок», а відсутність СВОГО рішення
        // про доступ: рішення приходять зі `AccessProfile` і `cellPermissions`,
        // клієнт їх лише показує. Спроба порахувати доступ на клієнті дала б
        // другу реалізацію RBAC, і вона розійшлася б із першою.
        var web = Path.Combine(SolutionRoot(), "src", "Ecr.Web", "src");

        var offenders = Directory
            .EnumerateFiles(web, "*.ts", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(web, "*.tsx", SearchOption.AllDirectories))
            .Where(f => !f.Contains("__tests__", StringComparison.Ordinal))
            .SelectMany(file => ClientRoleRegex
                .Matches(File.ReadAllText(file))
                .Select(m => $"{Path.GetFileName(file)}: {m.Value.Trim()}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-5.16")]
    public void Клієнтські_переходи_збігаються_з_доменом()
    {
        // ⛔ Клієнт вирішує, ЯКУ КНОПКУ показати, за таблицею станів
        // (`features/workflow/transitions.ts`). Це неминуче: кнопка
        // «Затвердити» на чернетці — обіцянка, якої стан аркуша не виконує.
        // Але таблиця на клієнті — це копія машини станів домену, а копія
        // розходиться з оригіналом мовчки: додали стан у `ApprovalState`,
        // клієнт про нього не дізнався, і дія просто не має кнопки.
        //
        // ⚠ Саме так і виник `A7-39`, тільки в найгрубішому вигляді: кнопок
        // не було зовсім. Тому сторож читає ОБИДВА файли і порівнює множини.
        var domain = DomainTransitions();
        var client = ClientTransitions();

        Assert.Equal(domain.Keys.Order(StringComparer.Ordinal), client.Keys.Order(StringComparer.Ordinal));

        var divergent = domain
            .Where(pair => !pair.Value.SetEquals(client[pair.Key]))
            .Select(pair =>
                $"{pair.Key}: домен [{string.Join(", ", pair.Value.Order(StringComparer.Ordinal))}], "
                + $"клієнт [{string.Join(", ", client[pair.Key].Order(StringComparer.Ordinal))}]")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(divergent);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-7.2")]
    public void Презентаційні_поля_мають_куди_записатися()
    {
        // ⛔ Два переліки того самого поняття в різних формах: домен вирішує,
        // ЧИ дозволена зміна поля (`ChangeClassifier.PresentationFields`),
        // сховище знає, В ЯКУ КОЛОНКУ її записати. Поле, дозволене доменом і
        // невідоме сховищу, дає відмову `ECR-TMPL-0422` на правку, яку
        // система вважає законною; зайве поле в сховищі — мертвий код, який
        // виглядає як можливість.
        //
        // ⚠ Сторож існує тому, що перекіс тут уже коштував дорого: сховище
        // не вміло записати НІЧОГО, і патч презентації мовчки не робив
        // нічого — при п'яти зелених тестах (`A7-43`).
        var allowed = Ecr.Domain.Services.ChangeClassifier.PresentationFields;

        var store = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "TemplateVersionStore.cs"));

        var writable = PresentationEntryRegex
            .Matches(store)
            .Select(m => m.Groups[2].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(writable);

        var unwritable = allowed.Where(f => !writable.Contains(f)).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            unwritable.Count == 0,
            "Домен дозволяє змінити ці поля, а сховищу нікуди їх записати: "
            + string.Join(", ", unwritable));

        var undeclared = writable.Where(f => !allowed.Contains(f)).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            undeclared.Count == 0,
            "Сховище вміє записати поля, яких домен презентаційними не вважає: "
            + string.Join(", ", undeclared));
    }

    /// <summary>
    /// Дозволені стани кожного переходу за <c>ApprovalState</c>.
    /// </summary>
    /// <remarks>
    /// Читається вартовий вираз на початку методу — саме він вирішує, чи
    /// відмовити <c>ECR-DOC-0409</c>. Дві форми: <c>Status is not (A or B)</c>
    /// і <c>Status != DocumentStatus.A</c>.
    /// </remarks>
    private static Dictionary<string, HashSet<string>> DomainTransitions()
    {
        var path = Path.Combine(
            SolutionRoot(), "src", "Ecr.Domain", "Entities", "Workflow", "ApprovalState.cs");

        Assert.True(File.Exists(path), $"Немає {path}: машину станів шукати ніде.");

        var source = WithoutComments(File.ReadAllText(path));
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (Match method in TransitionMethodRegex.Matches(source))
        {
            var body = method.Groups["body"].Value;
            var allowed = new HashSet<string>(StringComparer.Ordinal);

            var negated = StatusIsNotRegex.Match(body);
            if (negated.Success)
            {
                foreach (Match status in StatusValueRegex.Matches(negated.Groups[1].Value))
                {
                    allowed.Add(status.Groups[1].Value);
                }
            }
            else
            {
                var single = StatusNotEqualRegex.Match(body);
                Assert.True(
                    single.Success,
                    $"{method.Groups[1].Value}: не видно вартового виразу за станом — "
                    + "або його немає, або форма змінилася і сторож осліп.");

                allowed.Add(single.Groups[1].Value);
            }

            result[method.Groups[1].Value.ToLowerInvariant()] = allowed;
        }

        Assert.NotEmpty(result);

        return result;
    }

    /// <summary>Дозволені стани кожного переходу за таблицею клієнта.</summary>
    private static Dictionary<string, HashSet<string>> ClientTransitions()
    {
        var path = Path.Combine(
            SolutionRoot(), "src", "Ecr.Web", "src", "features", "workflow", "transitions.ts");

        Assert.True(File.Exists(path), $"Немає {path}: клієнт не має таблиці переходів.");

        var source = WithoutComments(File.ReadAllText(path));
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (Match entry in ClientTransitionRegex.Matches(source))
        {
            result[entry.Groups[1].Value] =
            [
                .. StateLiteralRegex.Matches(entry.Groups[2].Value).Select(m => m.Groups[1].Value)
            ];
        }

        return result;
    }

    private static string WithoutComments(string source)
        => Regex.Replace(Regex.Replace(source, @"//[^\n]*", string.Empty), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }

    [GeneratedRegex(@"CREATE\s+ASSEMBLY|EXTERNAL\s+NAME|'clr[\s_]?enabled'", RegexOptions.IgnoreCase)]
    private static partial Regex ClrRegex { get; }

    [GeneratedRegex(@"\b(SUM|AVG|COUNT|MIN|MAX)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex AggregateRegex { get; }

    [GeneratedRegex(@"SHRINKDATABASE|SHRINKFILE|DBCC\s+SHRINK", RegexOptions.IgnoreCase)]
    private static partial Regex ShrinkRegex { get; }

    /// <summary>Видалення проєкту або періоду через EF.</summary>
    [GeneratedRegex(@"\.Remove\s*\(\s*\w*(?:[Pp]roject|[Pp]eriod)\w*\s*\)|RemoveRange\s*\(\s*\w*(?:[Pp]roject|[Pp]eriod)")]
    private static partial Regex RemoveProjectRegex { get; }

    /// <summary>Видалення проєкту або періоду в SQL.</summary>
    [GeneratedRegex(@"DELETE\s+(?:FROM\s+)?\[?doc\]?\.\[?(?:Project|Period)\]?", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteProjectRegex { get; }

    /// <summary>Рядок білого списку сховища: <c>[("ColumnDef", "Ordinal")] = …</c>.</summary>
    [GeneratedRegex("""\[\(\s*"(\w+)"\s*,\s*"(\w+)"\s*\)\]\s*=""")]
    private static partial Regex PresentationEntryRegex { get; }

    /// <summary>Метод переходу разом із тілом до наступного оголошення.</summary>
    /// <remarks>
    /// ⚠ Тіло береться ДО наступного оголошення, а не збалансованими дужками.
    /// Балансування тут не працює: у повідомленнях відмов стоїть інтерполяція
    /// <c>$"…{Status}"</c>, тобто фігурні дужки всередині рядка всередині
    /// вкладеного блоку. Вираз із балансуванням на глибину два не збігався
    /// НІ З ЧИМ — і сторож мовчав би, якби не перевірка на порожній результат.
    /// </remarks>
    [GeneratedRegex(
        @"public void (Submit|Approve|Reject|Reopen)\([^)]*\)(?<body>.*?)(?=
    public |\z)",
        RegexOptions.Singleline)]
    private static partial Regex TransitionMethodRegex { get; }

    /// <summary>Вартовий вираз форми <c>Status is not (A or B)</c>.</summary>
    [GeneratedRegex(@"Status\s+is\s+not\s*\(([^)]*)\)")]
    private static partial Regex StatusIsNotRegex { get; }

    /// <summary>Вартовий вираз форми <c>Status != DocumentStatus.A</c>.</summary>
    [GeneratedRegex(@"Status\s*!=\s*DocumentStatus\.(\w+)")]
    private static partial Regex StatusNotEqualRegex { get; }

    /// <summary>Значення переліку станів усередині вартового виразу.</summary>
    [GeneratedRegex(@"DocumentStatus\.(\w+)")]
    private static partial Regex StatusValueRegex { get; }

    /// <summary>Рядок таблиці переходів клієнта: <c>submit: ['Draft', …],</c>.</summary>
    [GeneratedRegex(@"^\s*(submit|approve|reject|reopen):\s*\[([^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex ClientTransitionRegex { get; }

    /// <summary>Назва стану в масиві клієнта.</summary>
    [GeneratedRegex(@"'(\w+)'")]
    private static partial Regex StateLiteralRegex { get; }

    /// <summary>Власне рішення клієнта про роль або право.</summary>
    /// <remarks>
    /// Ловить порівняння з іменем ролі — саме так починається друга
    /// реалізація RBAC: `if (role === 'Administrator')`.
    /// </remarks>
    [GeneratedRegex(@"role\s*===\s*['""][A-Z]\w+['""]|roles\.includes\(\s*['""][A-Z]")]
    private static partial Regex ClientRoleRegex { get; }
}
