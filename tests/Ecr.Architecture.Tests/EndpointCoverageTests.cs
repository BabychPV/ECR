// tests/Ecr.Architecture.Tests/EndpointCoverageTests.cs
using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожен ендпоінт, оголошений у контракті, існує і не є заглушкою.
/// </summary>
/// <remarks>
/// ⚠ Тест з'явився після аудиту, який знайшов **17 із 20** ендпоінтів Етапу 1
/// у стані `NotImplementedException` — через рік після того, як етап
/// вважався завершеним. Причина проста: заглушки перевірялися тестами, а
/// ендпоінт без тестової заглушки не перевірявся нічим.
///
/// Таблиця `02-contracts.md` §9 і є специфікацією; цей тест звіряє з нею
/// реалізацію, а не навпаки.
/// </remarks>
public sealed partial class EndpointCoverageTests
{
    /// <summary>Етапи, ендпоінти яких ще не реалізовані.</summary>
    /// <remarks>
    /// Список має ЗМЕНШУВАТИСЯ. Етап, який уже зробили, але забули прибрати
    /// звідси, знову робить пропуск невидимим.
    /// </remarks>
    // ⚠ Список порожній: відкладених етапів більше немає. Кожен ендпоінт
    // контракту має реалізацію, і кожне оголошене право десь перевіряється.
    private static readonly int[] DeferredStages = [];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_ендпоінт_контракту_реалізований_або_належить_майбутньому_етапу()
    {
        var declared = Declared();
        Assert.NotEmpty(declared);

        var implemented = Implemented();

        var missing = declared
            .Where(d => !DeferredStages.Contains(d.Stage))
            .Where(d => !implemented.TryGetValue((d.Method, d.Path), out var done) || !done)
            .Select(d => $"{d.Method} {d.Path} (Етап {d.Stage})")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жоден_контролер_не_оголошує_маршруту_поза_контрактом()
    {
        var declared = Declared().Select(d => (d.Method, d.Path)).ToHashSet();

        // ⚠ Зворотний бік тієї самої перевірки. Маршрут, якого немає в
        // контракті, ніхто не описав клієнту — і зміниться він без
        // попередження, бо жоден документ його не тримає.
        var extra = Implemented().Keys
            .Where(k => !declared.Contains(k))
            .Select(k => $"{k.Method} {k.Path}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(extra);
    }

    // ⚠ Третій сторож того самого класу дефектів — «робота, якої ніхто не
    // робить». Перші два: контейнер мусить СТВОРИТИ кожен контролер
    // (`ContainerTests`) і кожен ендпоінт контракту мусить мати неспорожнілу
    // реалізацію (вище). Цей ловить третій різновид: ендпоінт існує, працює —
    // і не перевіряє права, яке контракт для нього оголосив.
    //
    // Саме так Етап 4 закрився першим проходом: усі дев'ять ендпоінтів
    // довідників, одиниць і методологій мали лише `[Authorize]`, тобто
    // будь-який автентифікований користувач міг редагувати довідники і
    // публікувати методології. Тести проходили: вони перевіряли правила
    // предметної області, а не доступ.

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_право_з_таблиці_ендпоінтів_десь_перевіряється()
    {
        var declared = DeclaredPermissions();
        Assert.NotEmpty(declared);

        // Права перевіряються В ОБРОБНИКАХ, а не атрибутом контролера
        // (архітектурне правило 7): доступ у ECR залежить від ресурсу, а
        // атрибут бачить лише ім'я політики. Тому шукаємо саме в застосунку.
        var application = SourceOf("Ecr.Application");

        var unchecked_ = declared
            .Where(permission => !application.Contains(
                $"\"{permission}\"", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unchecked_);
    }

    // ⚠ П'ятий сторож. Ловить те, чого не бачить ніхто: клієнт викликає
    // адресу, якої на сервері немає. Серверні тести про TypeScript не знають,
    // клієнтські ходять у замокнений fetch — і обидва зелені, поки екран у
    // браузері показує помилку на кожне відкриття.
    //
    // Саме так жила `A7-03`: п'ять екранів били в неіснуючі маршрути
    // (`/api/v1/cells` замість `/api/v1/documents/{id}/cells`,
    // `/api/v1/auth/login` замість `/api/v1/login/local`, `GET /api/v1/sources`,
    // якого не існувало взагалі).

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_адреса_яку_викликає_клієнт_існує_на_сервері()
    {
        var web = Path.Combine(SolutionRoot(), "src", "Ecr.Web", "src");
        Assert.True(Directory.Exists(web), $"Немає {web}.");

        var server = Implemented().Keys
            .Select(k => Placeholders(k.Path))
            .ToHashSet(StringComparer.Ordinal);

        // Здоров'я віддає не контролер, а конвеєр — у таблиці ендпоінтів його
        // немає за побудовою (`02-contracts.md` §12).
        string[] health = ["/health/live", "/health/ready", "/health/db"];

        var missing = Directory
            .EnumerateFiles(web, "*.ts*", SearchOption.AllDirectories)

            // Згенерована схема містить усі серверні шляхи за визначенням:
            // звіряти її саму з собою немає сенсу.
            .Where(f => !f.EndsWith("schema.d.ts", StringComparison.Ordinal))

            // ⚠ Тести клієнта ходять у ЗАМОКНЕНИЙ fetch: адреса там — довільний
            // рядок, і вимагати від неї існування на сервері означало б
            // забороняти перевіряти обробку помилок на вигаданому шляху.
            .Where(f => !f.Contains("__tests__", StringComparison.Ordinal))
            .SelectMany(f => ApiPathRegex.Matches(WithoutComments(File.ReadAllText(f)))
                .Select(m => new { File = Path.GetFileName(f), Path = Placeholders(m.Groups[1].Value) }))
            .Where(c => !server.Contains(c.Path) && !health.Contains(c.Path))
            .Select(c => $"{c.Path} ({c.File})")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>Прибирає коментарі перед пошуком адрес.</summary>
    /// <remarks>
    /// ⚠ Інакше сторож ловить власні пояснення: коментар «до аудиту клієнт бив
    /// у `/api/v1/cells`» виглядає для регулярного виразу так само, як виклик.
    /// Тест, який падає на розповіді про вже виправлений дефект, навчають
    /// ігнорувати.
    /// </remarks>
    private static string WithoutComments(string source)
        => BlockCommentRegex.Replace(LineCommentRegex.Replace(source, string.Empty), string.Empty);

    /// <summary>Зводить шаблон і інтерполяцію до однакового заповнювача.</summary>
    /// <remarks>
    /// Сервер пише <c>{documentId}</c>, клієнт — <c>${documentId}</c> або
    /// <c>${encodeURIComponent(code)}</c>. Порівнювати їх дослівно означало б
    /// оголосити розбіжністю кожен шлях із параметром.
    /// </remarks>
    private static string Placeholders(string path)
    {
        var withoutQuery = path.Split('?')[0].TrimEnd('/');
        var withoutInterpolation = InterpolationRegex.Replace(withoutQuery, "{p}");

        return BraceRegex.Replace(withoutInterpolation, "{p}");
    }

    [GeneratedRegex(@"//[^
]*")]
    private static partial Regex LineCommentRegex { get; }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex { get; }

    [GeneratedRegex(@"['""`](/(?:api/v1|health)/[^'""`\s]*)['""`]")]
    private static partial Regex ApiPathRegex { get; }

    [GeneratedRegex(@"\$\{(?:[^{}]|\{[^{}]*\})*\}")]
    private static partial Regex InterpolationRegex { get; }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex BraceRegex { get; }

    // ⚠ Четвертий сторож того самого класу дефектів. Три попередні дивляться
    // всередину сервера; цей — на межу «сервер → клієнт», де тести обох боків
    // сліпі за побудовою: серверні не знають про TypeScript, клієнтські
    // підставляють власні рядки замість серверних.
    //
    // Саме так до аудиту Етапу 7 жила `A7-02`: клієнт знав п'ять причин
    // заборони з тринадцяти, і три з них були написані інакше, ніж на сервері
    // (`ReadOnlyColumn` проти `ColumnReadOnly`). Тому кожна сіра комірка
    // пояснювалася користувачеві однаково — «немає права», — навіть коли
    // причина була в закритому періоді.

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_причина_заборони_має_підказку_на_клієнті()
    {
        var client = Path.Combine(
            SolutionRoot(), "src", "Ecr.Web", "src", "features", "grid", "permissions.ts");

        Assert.True(File.Exists(client), $"Немає {client}: клієнт не читає причин заборони.");

        var text = File.ReadAllText(client);

        // `None` — це дозвіл, а не причина: підказки він не потребує.
        var missing = Enum.GetNames<Ecr.Domain.Enums.EditDenyReason>()
            .Where(name => !string.Equals(name, "None", StringComparison.Ordinal))
            .Where(name => !text.Contains($"{name}:", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// Права, названі в таблиці ендпоінтів контракту, крім відкладених етапів.
    /// </summary>
    /// <remarks>
    /// Етап відсіюється тим самим списком <see cref="DeferredStages"/>, що й
    /// сама наявність ендпоінта: право не може перевірятися там, де ендпоінта
    /// ще немає. Список зменшується разом із етапами.
    /// </remarks>
    private static HashSet<string> DeclaredPermissions()
    {
        var path = Path.Combine(SolutionRoot(), "docs", "build", "02-contracts.md");

        return PermissionCell.Matches(File.ReadAllText(path))
            .Where(m => !DeferredStages.Contains(
                int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .Select(m => m.Groups[1].Value)
            .Where(p => p.Contains('.', StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Увесь текст вихідних файлів проєкту.</summary>
    private static string SourceOf(string project)
    {
        var root = Path.Combine(SolutionRoot(), "src", project);

        return string.Join(
            '\n',
            Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                        StringComparison.Ordinal)
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                           StringComparison.Ordinal))
                .Select(File.ReadAllText));
    }

    [GeneratedRegex(
        @"^\|\s*`(?:GET|POST|PUT|PATCH|DELETE)`\s*\|\s*`[^`]+`\s*\|\s*`([^`]+)`\s*\|\s*(\d)\s*\|",
        RegexOptions.Multiline)]
    private static partial Regex PermissionCell { get; }

    /// <summary>Рядок таблиці ендпоінтів контракту.</summary>
    private sealed record Endpoint(string Method, string Path, int Stage);

    [GeneratedRegex(@"^\|\s*`(GET|POST|PUT|PATCH|DELETE)`\s*\|\s*`([^`]+)`\s*\|[^|]*\|\s*(\d)\s*\|",
                    RegexOptions.Multiline)]
    private static partial Regex ContractRow { get; }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter { get; }

    private static List<Endpoint> Declared()
    {
        var path = Path.Combine(SolutionRoot(), "docs", "build", "02-contracts.md");
        return [.. ContractRow.Matches(File.ReadAllText(path))
            .Select(m => new Endpoint(
                m.Groups[1].Value,
                Normalize(m.Groups[2].Value.Split('?')[0].TrimEnd('/')),
                int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)))];
    }

    /// <summary>Маршрути контролерів: чи є дія і чи не заглушка вона.</summary>
    /// <remarks>
    /// ⚠ Читається ВИХІДНИЙ КОД, а не рефлексія. Рефлексія бачить, що метод
    /// існує, але не бачить, що його тіло — це
    /// <c>throw new NotImplementedException</c>. Саме така сліпота і дозволила
    /// сімнадцяти ендпоінтам Етапу 1 роками рахуватися готовими.
    /// </remarks>
    private static Dictionary<(string Method, string Path), bool> Implemented()
    {
        var result = new Dictionary<(string, string), bool>();
        var directory = Path.Combine(SolutionRoot(), "src", "Ecr.Api", "Controllers");

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var source = File.ReadAllText(file);
            var route = RouteAttributeRegex.Match(source);
            var baseRoute = route.Success ? route.Groups[1].Value : string.Empty;

            foreach (Match action in ActionRegex.Matches(source))
            {
                var suffix = action.Groups[2].Value;
                var full = "/" + string.Join('/', new[] { baseRoute, suffix }.Where(p => p.Length > 0));

                // Заглушка впізнається за тілом одразу після сигнатури: у
                // реалізованій дії там код, у нереалізованій — throw.
                var implemented = !action.Groups["body"].Value.Contains(
                    "NotImplementedException", StringComparison.Ordinal);

                result[(action.Groups[1].Value.ToUpperInvariant(), Normalize(full))] = implemented;
            }
        }

        return result;
    }

    [GeneratedRegex(@"\[Route\(""([^""]+)""\)\]")]
    private static partial Regex RouteAttributeRegex { get; }

    [GeneratedRegex(
        @"\[Http(Get|Post|Put|Patch|Delete)(?:\(""([^""]*)""\))?\]"
        + @".*?public\s+(?:async\s+)?(?:Task<[^(]*?>|Task|IActionResult)\s+\w+\s*\("
        + @"[^)]*\)(?=(?<body>.{0,400}))",
        RegexOptions.Singleline)]
    private static partial Regex ActionRegex { get; }

    /// <summary>Прибирає імена і обмеження параметрів: <c>{id:long}</c> → <c>{}</c>.</summary>
    private static string Normalize(string route) => RouteParameter.Replace(route, "{}");

    /// <summary>Корінь рішення — від каталогу збірки вгору до <c>Ecr.sln</c>.</summary>
    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
    }
}
