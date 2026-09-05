// tests/Ecr.Architecture.Tests/EndpointCoverageTests.cs
using System.Globalization;
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_рядок_якого_просить_клієнт_є_в_каталозі()
    {
        var web = Path.Combine(SolutionRoot(), "src", "Ecr.Web", "src");
        Assert.True(Directory.Exists(web), $"Немає {web}.");

        var seed = File.ReadAllText(
            Path.Combine(SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));

        var seeded = SeedKeyRegex.Matches(seed)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(seeded);

        var missing = Directory
            .EnumerateFiles(web, "*.ts*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("__tests__", StringComparison.Ordinal))
            .SelectMany(f => UiKeyRegex.Matches(WithoutComments(File.ReadAllText(f)))
                .Select(m => new { File = Path.GetFileName(f), Key = m.Groups[1].Value }))
            .Where(u => !seeded.Contains(u.Key))
            .Select(u => $"{u.Key} ({u.File})")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Роль_і_право_bootstrap_адміністратора_існують_у_seed()
    {
        var seed = File.ReadAllText(
            Path.Combine(SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));

        // ⛔ Тест дивиться на SEED, а не на фікстуру. До `A7-13` обробник
        // видавав роль `Administrator`, якої seed не створює; фікстура
        // створювала її сама, тести були зелені, а старт розгорнутої системи
        // падав із «Роль відсутня» — тобто перевірялося рівно те, що ніде не
        // виконується.
        Assert.Contains($"N'{Ecr.Application.Security.BootstrapAdmin.RoleCode}'", seed, StringComparison.Ordinal);

        // Право, за яким система вважає, що доменний адміністратор з'явився:
        // без нього bootstrap-запис не вимкнеться ніколи.
        Assert.Contains(
            $"N'{Ecr.Application.Security.BootstrapAdmin.AdminPermission}'", seed, StringComparison.Ordinal);

        // ⚠ Мало оголосити роль — вона має отримати саме це право. Роль без
        // прав виглядає як робоча конфігурація і мовчки нічого не дозволяє.
        //
        // Береться БЛОК цілком, а не текст після назви ролі: усередині одного
        // MERGE перелік прав стоїть перед умовою на роль, і зріз «уперед від
        // назви» бачив би порожнечу там, де все на місці.
        var grant = Blocks(seed, "MERGE sec.RolePermission")
            .SingleOrDefault(b => b.Contains(
                $"N'{Ecr.Application.Security.BootstrapAdmin.RoleCode}'", StringComparison.Ordinal));

        Assert.NotNull(grant);
        Assert.Contains(Ecr.Application.Security.BootstrapAdmin.AdminPermission, grant, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_дозволений_шлях_воріт_зміни_пароля_існує_на_сервері()
    {
        var server = Implemented().Keys
            .Select(k => Placeholders(k.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(server);

        // ⛔ Ворота порівнюють шлях запиту з переліком ПРЕФІКСІВ. Префікс, що
        // не веде на жоден маршрут, нічого не дозволяє — і мовчки: `A7-14`
        // саме так закрив систему для власника разового пароля, бо всі чотири
        // записи вказували на `/api/v1/auth/...`, якого немає.
        var missing = Ecr.Application.Security.PasswordChangeGate.AllowedPaths
            .Where(allowed => !server.Any(
                path => path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_право_яке_вимагає_обробник_існує_в_каталозі()
    {
        var seed = File.ReadAllText(
            Path.Combine(SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));

        var catalogue = Blocks(seed, "MERGE sec.Permission").Single();

        // ⛔ Право, оголошене константою, але відсутнє в каталозі, не можна
        // видати НІКОМУ: `sec.RolePermission` посилається на `sec.Permission`
        // зовнішнім ключем. Ендпоінт із таким правом закритий назавжди, і
        // жоден тест цього не бачить — обробник просто відмовляє, як і має.
        //
        // Саме так жила `Calculation.RecalculateClosed` (`A7-20`): константа
        // була, каталогу — ні, а правило трималося зовсім на іншому.
        var missing = PermissionConstant
            .Matches(SourceOf("Ecr.Application"))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(code => !catalogue.Contains($"N'{code}'", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_сутність_яку_система_створює_має_чим_її_заповнити()
    {
        // ⛔ Сторож проти цілого класу дефектів: сутність існує в схемі й у
        // домені, її ЧИТАЮТЬ — і не створює її ніщо. Система при цьому
        // збирається, тести зелені, а працювати вона не може.
        //
        // Так жили одразу три: `doc.TableInstance` (`A7-30`) — документ,
        // створений через API, не мав жодної таблиці; `sec.ResourceGrant`
        // (`A7-22`) — користувач із усіма правами бачив порожній перелік
        // проєктів; `ProjectStatus.Active` (`A7-25`) — проєкт лишався
        // чернеткою назавжди, тому періоди не відкривалися ніколи.
        //
        // ⚠ Перевіряється саме наявність КОНСТРУЮВАННЯ поза тестами: `new T(`
        // у `src`. Це груба ознака, і навмисно: тонша (граф викликів) ловила б
        // те саме, але падала б на кожному рефакторингу.
        string[] mustBeCreated =
        [
            "TableInstance",
            "ResourceGrant",
            "DocumentSheet",
            "TableRow",
            "Period",
            "NotificationOutboxItem",
        ];

        var application = SourceOf("Ecr.Application");
        var infrastructure = SourceOf("Ecr.Infrastructure");
        var domain = SourceOf("Ecr.Domain");
        var all = string.Join('\n', application, infrastructure, domain);

        var never = mustBeCreated
            .Where(type => !all.Contains($"new {type}(", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(never);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_успішна_відповідь_має_оголошений_тип()
    {
        // ⛔ Восьмий сторож. Ловить корінь `A7-16` і `A7-32`: дія повертає
        // `200`, не називаючи ЧОГО. У схемі OpenAPI лишається порожня
        // відповідь, згенерувати клієнтський тип ні з чого — і клієнт описує
        // її рукописним інтерфейсом. Помилка в назві поля при цьому нічого не
        // ламає: поле просто `undefined`, а компілятор обіцяв рядок.
        //
        // ⚠ Це підважує головний захід проти всього класу межових дефектів:
        // «зробити межу компільовною». Одинадцять нетипізованих відповідей
        // означали, що третина API лишається поза цим захистом.
        var directory = Path.Combine(SolutionRoot(), "src", "Ecr.Api", "Controllers");

        var offenders = Directory
            .EnumerateFiles(directory, "*.cs")
            .SelectMany(f => UntypedOkRegex
                .Matches(WithoutComments(File.ReadAllText(f)))
                .Select(_ => Path.GetFileName(f)))
            .GroupBy(f => f, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count()}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жодна_заглушка_не_пережила_свого_етапу()
    {
        // ⛔ Одинадцятий сторож. Заглушка має вмирати, коли етап закривається,
        // а не коли хтось випадково запустить систему.
        //
        // ⚠ `A7-37` прожила два етапи: перевірки `jobs` і `sources`
        // відповідали «з'явиться на Етапі 5» **незалежно ні від чого**, і
        // `/health/ready` був жовтим ЗАВЖДИ — при семи працюючих задачах і
        // живому зборі. Постійно жовтий індикатор гірший за відсутній: він
        // навчає не помічати себе, і в день, коли пожовтіє по справі, ніхто
        // не подивиться.
        //
        // ⚠ Поріг — НОМЕР ЕТАПУ, а не «будь-яка згадка». Заглушка з посиланням
        // на майбутній етап законна: вона чесно каже, чого ще немає.
        var offenders = SourceTree
            .Production(
                "Ecr.Domain", "Ecr.Application", "Ecr.Infrastructure", "Ecr.Api",
                "Ecr.Calculations", "Ecr.Expressions", "Ecr.Adapters.PiAf", "Ecr.Adapters.Excel")
            .SelectMany(file => StaleStubRegex
                .Matches(WithoutComments(file.Text))
                .Where(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) <= CurrentStage)
                .Select(match => $"{Path.GetFileName(file.Path)}: {match.Value.Trim()}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жодна_перевірка_здоровя_не_відповідає_константою()
    {
        // ⛔ Перевірка звіряється з реальним станом або її немає в переліку
        // (`D-139`). Заглушка з фіксованим результатом заборонена: немає
        // підсистеми — перевірки НЕМАЄ, а не «є і жовта».
        //
        // ⚠ Ознака константи проста і надійна: тіло перевірки не звертається
        // до жодної залежності. Перевірка, яка нічого не питає, не може
        // відповісти нічого, крім заздалегідь відомого.
        var directory = Path.Combine(SolutionRoot(), "src", "Ecr.Api", "Health");

        var offenders = Directory
            .EnumerateFiles(directory, "*HealthCheck.cs")
            .Where(file =>
            {
                var text = WithoutComments(File.ReadAllText(file));

                return !text.Contains("await", StringComparison.Ordinal)
                    && !text.Contains("null", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_відповідь_без_тіла_оголошена_своїм_кодом()
    {
        // ⛔ Десятий сторож. Дія повертає `NoContent()` (204) або `Accepted()`
        // (202), але оголошує лише те, що вивів генератор за замовчуванням, —
        // `200`. Схема OpenAPI при цьому виглядає цілком здорово, і саме тому
        // розбіжність не видно: клієнт бачить «200 без тіла» і робить
        // `response.json()` над порожньою відповіддю.
        //
        // ⚠ Знайдено не міркуванням, а спробою вийти з системи: `POST
        // /api/v1/logout` повертав 204, а `schema.d.ts` обіцяв 200. Runtime
        // рятувала окрема гілка `status === 204` в `apiFetch` — тобто клієнт
        // уже не вірив власному контракту.
        //
        // ⚠ Розбір ПОМЕТОДНИЙ, а не підрахунком по файлу: контролер із двома
        // діями, де одна оголошує 204 двічі, а друга не оголошує зовсім, дав
        // би однакові суми — і сторож був би зеленим на дефекті.
        var directory = Path.Combine(SolutionRoot(), "src", "Ecr.Api", "Controllers");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var text = WithoutComments(File.ReadAllText(file));

            foreach (var action in ActionBlockRegex.Split(text).Skip(1))
            {
                var name = MethodNameRegex.Match(action) is { Success: true } m
                    ? m.Groups[1].Value
                    : "?";

                if (action.Contains("return NoContent(", StringComparison.Ordinal)
                    && !action.Contains("Status204NoContent", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}.{name}: 204 не оголошено");
                }

                if (action.Contains("return Accepted(", StringComparison.Ordinal)
                    && !action.Contains("Status202Accepted", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}.{name}: 202 не оголошено");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_порт_застосунку_названий_у_контракті()
    {
        // ⛔ Дев'ятий сторож. Контракт — це те, що читатиме той, хто прийде
        // після мене; порт, якого в ньому немає, для нього не існує. За шість
        // етапів перелік відстав на ДВАДЦЯТЬ ШІСТЬ портів, і помітити це можна
        // було лише звіркою вручну — тобто ніколи.
        //
        // ⚠ Перевіряється НАЗВА, а не текст оголошення. Вимагати дослівного
        // збігу означало б завести два джерела правди: сигнатура живе в коді,
        // а документ каже, що такий порт є і навіщо.
        var directory = Path.Combine(SolutionRoot(), "src", "Ecr.Application", "Ports");
        Assert.True(Directory.Exists(directory), $"Немає {directory}.");

        var contract = File.ReadAllText(
            Path.Combine(SolutionRoot(), "docs", "build", "02-contracts.md"));

        var missing = Directory
            .EnumerateFiles(directory, "I*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Where(name => !contract.Contains(name!, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>Розбиває скрипт на пакети, що починаються заданим текстом.</summary>
    /// <param name="script">Текст скрипта.</param>
    /// <param name="start">Початок пакета, наприклад <c>MERGE sec.RolePermission</c>.</param>
    private static IEnumerable<string> Blocks(string script, string start)
        => script
            .Split(start, StringSplitOptions.None)
            .Skip(1)
            .Select(part => part.Split("\nGO", StringSplitOptions.None)[0]);

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

    /// <summary>Початок дії контролера — атрибут маршруту.</summary>
    /// <remarks>
    /// Використовується для РОЗБИТТЯ файлу на дії: усе від одного
    /// <c>[HttpGet]</c> до наступного належить одній дії разом із її
    /// атрибутами. Простіший поділ по <c>public</c> розірвав би дію навпіл —
    /// атрибути лишилися б у попередньому блоці.
    /// </remarks>
    [GeneratedRegex(@"(?=\n\s*\[Http(?:Get|Post|Put|Delete|Patch))")]
    private static partial Regex ActionBlockRegex { get; }

    /// <summary>Назва методу дії — щоб у звіті було видно, де саме дефект.</summary>
    [GeneratedRegex(@"public\s+(?:async\s+)?[\w<>\[\], ?]+\s+(\w+)\s*\(")]
    private static partial Regex MethodNameRegex { get; }

    /// <summary>Поточний етап проєкту.</summary>
    /// <remarks>
    /// ⚠ Число, а не «останній етап у документі»: заглушка з посиланням на
    /// МАЙБУТНІЙ етап законна — вона чесно каже, чого ще немає. Незаконна саме
    /// та, чий етап уже закрито.
    /// </remarks>
    private const int CurrentStage = 7;

    /// <summary>Заглушка з посиланням на номер етапу.</summary>
    /// <remarks>
    /// Ловить обидві форми, у яких вони писалися: «з'явиться на Етапі N» у
    /// тексті відповіді і <c>TODO: Етап N</c> у коді.
    /// </remarks>
    [GeneratedRegex(@"(?:з['’]явиться на Етапі|TODO:\s*Етап|Етап)\s*(\d+)")]
    private static partial Regex StaleStubRegex { get; }

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

    /// <summary>Виклик <c>t('ключ')</c> у клієнті.</summary>
    /// <remarks>
    /// ⚠ Символ перед <c>t</c> обов'язковий: без нього вираз ловить <c>it(</c>
    /// із тестів і оголошує назву тесту незнайденим ключем інтерфейсу.
    /// Крапка в ключі відсіює решту однобуквених функцій.
    /// </remarks>
    [GeneratedRegex(@"[^A-Za-z0-9_$]t\('([a-zA-Z][A-Za-z0-9]*\.[A-Za-z0-9-]+)'")]
    private static partial Regex UiKeyRegex { get; }

    /// <summary>Оголошення права константою в застосунку.</summary>
    /// <remarks>
    /// Береться саме КОНСТАНТА, а не будь-який рядок виду <c>A.B</c>: у коді
    /// повно назв типів і методів тієї самої форми, і сторож на них ловив би
    /// власний шум замість дефекту.
    /// </remarks>
    [GeneratedRegex(@"const string [A-Za-z]*Permission[A-Za-z]* = ""([A-Za-z]+\.[A-Za-z]+)""")]
    private static partial Regex PermissionConstant { get; }

    /// <summary>Оголошення <c>200</c> БЕЗ типу відповіді.</summary>
    /// <remarks>
    /// Узагальнена форма <c>[ProducesResponseType&lt;T&gt;(...)]</c> сюди не
    /// підпадає: у ній одразу після <c>ProducesResponseType</c> стоїть <c>&lt;</c>.
    /// </remarks>
    [GeneratedRegex(@"ProducesResponseType\(StatusCodes\.Status200OK\)")]
    private static partial Regex UntypedOkRegex { get; }

    /// <summary>Рядок каталогу в <c>09-seed.sql</c>.</summary>
    [GeneratedRegex(@"\(N'([^']+)',\s*N'[a-z]{2}'")]
    private static partial Regex SeedKeyRegex { get; }

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
