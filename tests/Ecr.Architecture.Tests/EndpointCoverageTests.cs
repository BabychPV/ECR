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
        // ⛔ Три сліпі плями попередньої версії сторожа, кожна — з реальним
        // місцем у клієнті, яке через неї не перевірялося:
        //   1. умовний вираз: `t(source === null ? 'sources.created' : 'sources.saved')`
        //      — регулярка брала лише літерал ОДРАЗУ після дужки;
        //   2. множина: `formatCount(n, 'sources.testEntities')` просить
        //      `sources.testEntities.one`/`.other`, а в тексті є лише основа;
        //   3. ключ у змінній: `t(problem.key, …)` — ключі `schedule.cron*`
        //      живуть у `cronFormat.ts`, за три файли від виклику.
        // Тепер (1) і (2) розбираються з тексту, а (3) — явним переліком
        // `DynamicKeySites` із власним храповиком (два тести нижче).
        var seeded = SeedCatalogKeys();
        var scan = ClientKeyScan.Of(WebRoot());

        var missing = new List<string>();

        missing.AddRange(scan.Literals
            .Where(u => !seeded.Contains(u.Key))
            .Select(u => $"{u.Key} ({u.File}:{u.Line})"));

        // ⚠ `formatCount` бере категорію з `Intl.PluralRules` і НЕ падає на
        // `.other`, коли бракує потрібної форми (`plural.ts`). `one` і `other`
        // — мінімум, який існує в кожній мові каталогу.
        missing.AddRange(scan.PluralBases
            .SelectMany(u => PluralForms.Select(form => (Key: $"{u.Key}.{form}", u.File, u.Line)))
            .Where(u => !seeded.Contains(u.Key))
            .Select(u => $"{u.Key} (множина, {u.File}:{u.Line})"));

        missing.AddRange(DynamicKeySites
            .SelectMany(site => site.Keys.Select(key => (Key: key, Site: site)))
            .Where(u => !seeded.Contains(u.Key))
            .Select(u => $"{u.Key} (через змінну: {u.Site.File} ← {u.Site.Producer})"));

        var report = missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            report.Count == 0,
            "Рядки, яких клієнт просить, а в 09-seed.sql їх немає:"
            + Environment.NewLine + string.Join(Environment.NewLine, report));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_ключ_який_клієнт_передає_змінною_названий_у_переліку()
    {
        // ⛔ Храповик. Виклик `t(<не літерал>)` — це ключ, якого сторож вище
        // не бачить із тексту. Кожне таке місце або стоїть у `DynamicKeySites`
        // разом із ключами, які туди доходять, або цей тест червоний. Інакше
        // нова динаміка прослизала б мовчки — рівно так, як жили
        // `schedule.cron*` до цього сторожа.
        var scan = ClientKeyScan.Of(WebRoot());

        var listed = DynamicKeySites
            .GroupBy(s => (s.File, s.Expression))
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Count));

        var unlisted = scan.Dynamic
            .GroupBy(d => (d.File, d.Expression))
            .Where(g => g.Count() > listed.GetValueOrDefault(g.Key))
            .SelectMany(g => g.Skip(listed.GetValueOrDefault(g.Key)))
            .Select(d => $"  {d.File}:{d.Line}: t({d.Expression})")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unlisted.Count == 0,
            "Ключ каталогу передано змінною, і сторож не знає, які рядки туди доходять. "
            + "Або перепиши виклик літералом (умовний вираз над літералами — теж літерал), "
            + "або допиши місце в EndpointCoverageTests.DynamicKeySites разом із ключами:"
            + Environment.NewLine + string.Join(Environment.NewLine, unlisted));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перелік_ключів_через_змінну_не_застарів()
    {
        // ⛔ Другий бік храповика (той самий прийом, що `UncheckedPermissionTests`):
        // перелік, який лише росте, перестає бути заміром. Червоне, якщо
        //   • місця зі списку в клієнті більше немає (виклик став літералом —
        //     тоді ключі перевіряє вже основний сторож, а рядок тут зайвий);
        //   • ключа зі списку більше не породжує названий файл;
        //   • файл-джерело породжує ключ, якого в списку немає, — тобто
        //     перелік тихо відстав від коду.
        var web = WebRoot();
        var scan = ClientKeyScan.Of(web);
        var stale = new List<string>();

        var found = scan.Dynamic
            .GroupBy(d => (d.File, d.Expression))
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var group in DynamicKeySites.GroupBy(s => (s.File, s.Expression)))
        {
            var expected = group.Sum(s => s.Count);
            var actual = found.GetValueOrDefault(group.Key);
            if (actual < expected)
            {
                stale.Add(
                    $"  {group.Key.File}: t({group.Key.Expression}) — у клієнті {actual} із {expected}; "
                    + $"прибери зайве з DynamicKeySites разом із ключами "
                    + $"[{string.Join(", ", group.SelectMany(s => s.Keys))}].");
            }
        }

        var requested = scan.Literals.Select(l => l.Key)
            .Concat(DynamicKeySites.SelectMany(s => s.Keys))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var producer in DynamicKeySites.Where(s => s.Producer is not null).GroupBy(s => s.Producer!))
        {
            var path = Path.Combine(web, producer.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                stale.Add($"  {producer.Key}: файла немає — прибери або виправ Producer у DynamicKeySites.");
                continue;
            }

            var strings = TypeScriptSource.Parse(File.ReadAllText(path)).Strings;

            foreach (var key in producer.SelectMany(s => s.Keys).Distinct(StringComparer.Ordinal))
            {
                var produced = strings.Any(s => s.Interpolated
                    ? s.Head.Length > 0 && key.StartsWith(s.Head, StringComparison.Ordinal)
                    : string.Equals(s.Text, key, StringComparison.Ordinal));

                if (!produced)
                {
                    stale.Add($"  {key}: {producer.Key} його більше не породжує — прибери з DynamicKeySites.");
                }
            }

            foreach (var literal in strings.Where(s => !s.Interpolated && CatalogKeyShape().IsMatch(s.Text)))
            {
                if (!requested.Contains(literal.Text))
                {
                    stale.Add($"  {literal.Text}: {producer.Key} його породжує, а в DynamicKeySites його немає — допиши.");
                }
            }
        }

        Assert.True(
            stale.Count == 0,
            "DynamicKeySites розійшовся з клієнтом:" + Environment.NewLine + string.Join(Environment.NewLine, stale.Distinct(StringComparer.Ordinal)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сканер_ключів_бачить_код_і_пропускає_коментарі()
    {
        // ⛔ Самоперевірка. Сторож по тексту джерел падає на двох речах: на
        // коментарі (бере згадку за виклик) і на переносі рядка (не бачить
        // виклику, розірваного форматером). Обидві — тут, на зразку, а не
        // «колись у клієнті».
        const string sample = """
            // t('comment.line')
            /* t('comment.block') */
            const url = 'http://example/'; // t('comment.tail')
            const a = t('plain.key');
            const b = t(
              flag
                ? 'wrapped.yes' // t('comment.inside')
                : 'wrapped.no',
            );
            const c = t(x === 'saving' ? 'grid.saving' : 'grid.saved');
            const d = t(problem.key, problem.params);
            const e = t(`prefix.${kind}`);
            const f = t(statusKey('sheet', 'Draft'));
            const g = `${t(key)}-${guid}`;
            const h = <Text>Don't worry</Text>;
            const h2 = <Text>{t('jsx.key')}</Text>;
            const i = value.replace(/'/g, '');
            const j = formatCount(n, 'plural.base');
            const k = i18n.t('method.call');
            export function t(key: string) {}
            // коментар, у якому щось з'явилося
            const l = t('x.afterLineComment');
            /* блок, у якому щось з'явилося */
            const m = t('x.afterBlockComment');
            const n = <Box>{/* JSX-коментар: з'явився */}{t('x.afterJsxComment')}</Box>;
            const o = <Text>з'явився</Text>;
            const p = t('x.afterJsxText');
            const q = <Text>з'явився {t('x.sameLineAfterJsxText')}</Text>;
            const r = <Text>Don't {t('x.betweenApostrophes')} — it's</Text>;
            const s = <Text>'{t('x.quotedJsxText')}'</Text>;
            """;

        var scan = ClientKeyScan.Scan("sample.tsx", sample);

        Assert.Equal(
            [
                "grid.saved", "grid.saving", "jsx.key", "plain.key", "status.sheet.Draft", "wrapped.no", "wrapped.yes",
                "x.afterBlockComment", "x.afterJsxComment", "x.afterJsxText", "x.afterLineComment",
                "x.betweenApostrophes", "x.quotedJsxText", "x.sameLineAfterJsxText",
            ],
            scan.Literals.Select(l => l.Key).Order(StringComparer.Ordinal));

        Assert.Equal(
            ["`prefix.${kind}`", "key", "problem.key"],
            scan.Dynamic.Select(d => d.Expression).Order(StringComparer.Ordinal));

        Assert.Equal(["plural.base"], scan.PluralBases.Select(p => p.Key));

        // Число літералом: сканер, що перестав бачити виклики, дав би порожній
        // клієнт — і всі три сторожі вище позеленіли б мовчки.
        var real = ClientKeyScan.Of(WebRoot());
        Assert.True(real.Literals.Count >= 1000, $"Розібрано підозріло мало ключів: {real.Literals.Count}.");
        Assert.Contains(real.Literals, l => l.Key == "sources.saved");
        Assert.Contains(real.PluralBases, p => p.Key == "sources.testEntities");
    }

    /// <summary>Форми множини, які вимагаються для кожної основи <c>formatCount</c>.</summary>
    private static readonly string[] PluralForms = ["one", "other"];

    /// <summary>
    /// Місця, де клієнт передає ключ каталогу ЗМІННОЮ, і ключі, які туди
    /// доходять.
    /// </summary>
    /// <remarks>
    /// ⛔ Храповик у два боки (<c>Кожен_ключ_який_клієнт_передає_змінною_названий_у_переліку</c>,
    /// <c>Перелік_ключів_через_змінну_не_застарів</c>). <c>Expression</c> — текст
    /// першого аргументу без коментарів, пробіли зведені до одного.
    ///
    /// ⚠ <c>Producer = null</c> — ключ приходить ІЗ СЕРВЕРА (<c>messageKey</c>,
    /// <c>title</c> відповіді). Клієнт їх не знає за побудовою; наявність
    /// <c>err.*</c>-рядків для серверних кодів стережуть <c>ErrorTitleCatalogTests</c>
    /// і <c>MessageKeyRatchetTests</c>, не цей перелік.
    ///
    /// ⚠ Ключі з шаблону над серверним переліком (<c>notifications.event.${…}</c>)
    /// перелічені станом на 2026-09-21 — тими значеннями переліку сервера, які
    /// є зараз. Новий член переліку без рядка тут цей сторож НЕ побачить.
    /// </remarks>
    private static readonly DynamicKeySite[] DynamicKeySites =
    [
        new("app/AppLayout.tsx", "route.handle.labelKey", 1, "app/routes.ts", RouteLabelKeys,
            "Пункт меню: labelKey маршруту."),
        new("app/Breadcrumbs.tsx", "handle.labelKey", 1, "app/routes.ts", RouteLabelKeys,
            "Крихта: labelKey маршруту."),

        new("features/grid/permissions.ts", "Hints[reason]", 1, "features/grid/permissions.ts",
            [
                "deny.NoGrant", "deny.PeriodNotOpenYet", "deny.PeriodClosed", "deny.OutOfAccessWindow",
                "deny.DocumentSubmitted", "deny.DocumentApproved", "deny.ColumnReadOnly", "deny.RowReadOnly",
                "deny.CalculatedCell", "deny.ProjectArchived", "deny.ArchivingInProgress", "deny.BusinessRule",
                "deny.SimulationReadOnly", "deny.OutsidePermitWindow", "deny.InsufficientGrantLevel",
            ],
            "Підказка сірої комірки за причиною заборони."),

        new("features/workflow/jobLabel.ts", "key", 2, "features/workflow/jobLabel.ts",
            [
                "jobs.kind.recalculation", "jobs.kind.formulaRecalculation", "jobs.kind.excelExport",
                "jobs.kind.excelImport", "jobs.kind.materializeCollectedData", "jobs.kind.reportSnapshot",
                "jobs.kind.collection",
            ],
            "Назва типу фонової задачі (KindKeys)."),

        new("pages/admin/HealthPage.tsx", "translationKey", 1, "pages/admin/HealthPage.tsx",
            [
                "health.database.edition", "health.database.effectiveMode", "health.database.majorVersion",
                "health.database.rcsi", "health.database.archiveBatchSize", "health.database.filegroups",
                "health.database.missingFilegroups", "health.database.partitionsAhead",
                "health.database.limitations",
            ],
            "Підпис поля /health/db (FieldLabelKeys)."),

        // ⚠ Запасний варіант — сам ідентифікатор (`hasText(key) ? t(key) : name`),
        // тож перевірка без рядка в сіді не дає `⟦…⟧`; але три відомі — названі тут.
        new("pages/admin/HealthPage.tsx", "key", 1, "pages/admin/HealthPage.tsx",
            ["health.check.db", "health.check.jobs", "health.check.sources"],
            "Назва картки перевірки стану (checkLabel, U-14): ім'я з AddCheck<…> у Program.cs."),

        // ⚠ Ключі — увесь каталог sec.Permission із 09-seed.sql станом на 2026-09-23.
        // Нове право без рядка тут цей сторож НЕ побачить (на екрані лишиться сам
        // код, не `⟦…⟧`); повноту стереже permissionLabel.test.ts, що звіряє сід сам із собою.
        new("features/security/permissionLabel.ts", "key", 1, "features/security/permissionLabel.ts",
            PermissionLabelKeys, "Назва права в матриці /admin/security (U-11)."),

        new("features/projects/CreateProjectModal.tsx", "ProjectFieldLabelKey[field]", 1,
            "features/projects/CreateProjectModal.tsx",
            ["periods.code", "periods.name", "periods.timeZone", "periods.templateVersion", "periods.policy", "periods.customCount"],
            "Перелік бракуючих полів форми проєкту."),

        // U-18: той самий рядок «Still needed» у діалозі нового довідника.
        new("features/registries/CreateRegistryModal.tsx", "RegistryFieldLabelKey[field]", 1,
            "features/registries/CreateRegistryModal.tsx",
            ["registries.code", "registries.name"],
            "Перелік бракуючих полів форми нового довідника."),

        // ⚠ Опис функції в підказці редактора виразів (`hasText(key) ? t(key) : —`):
        // функцію без рядка підказка показує самою сигнатурою, тож перелік — лише
        // ті, що вже мають текст у сіді (діалекти Template і Methodology, `02b` §7–§8).
        new("features/expressions/describe.ts", "key", 1, "features/expressions/describe.ts",
            [
                "expressions.fn.abs", "expressions.fn.average", "expressions.fn.convert",
                "expressions.fn.count", "expressions.fn.if", "expressions.fn.iferror", "expressions.fn.max",
                "expressions.fn.min", "expressions.fn.product", "expressions.fn.regfield",
                "expressions.fn.round", "expressions.fn.sum", "expressions.fn.sumif", "expressions.fn.acos",
                "expressions.fn.asin", "expressions.fn.atan", "expressions.fn.ceiling", "expressions.fn.cos",
                "expressions.fn.exp", "expressions.fn.floor", "expressions.fn.ieeeremainder",
                "expressions.fn.ln", "expressions.fn.log", "expressions.fn.log10", "expressions.fn.pow",
                "expressions.fn.sign", "expressions.fn.sin", "expressions.fn.sqrt", "expressions.fn.tan",
                "expressions.fn.truncate", "expressions.fn.substance", "expressions.fn.ifs",
                "expressions.fn.in",
            ],
            "Короткий опис функції в переліку доповнення й при наведенні."),

        new("pages/admin/SnapshotsPage.tsx", "blockedReason", 1, "pages/admin/SnapshotsPage.tsx",
            ["snapshots.parametersUnknown", "snapshots.parametersBlocked"],
            "Причина, чому зріз не можна замовити."),
        new("features/reports/SnapshotFormatBadge.tsx", "look.label", 1, "features/reports/SnapshotFormatBadge.tsx",
            ["snapshots.formatLegacy", "snapshots.formatUnknown"],
            "Позначка формату чисел зрізу (legacy/unknown; current не позначається)."),
        new("features/reports/SnapshotFormatBadge.tsx", "look.hint", 1, "features/reports/SnapshotFormatBadge.tsx",
            ["snapshots.formatLegacyHint", "snapshots.formatUnknownHint"],
            "Підказка до позначки формату чисел зрізу."),

        new("features/integration/CollectionScheduleTab.tsx", "problem.key", 1, "features/integration/cronFormat.ts",
            ["schedule.cronEmpty", "schedule.cronTooLong", "schedule.cronFieldCount", "schedule.cronDayQuestion", "schedule.cronField"],
            "Помилка cron-виразу (cronProblem)."),

        new("features/integration/DataSourceFormModal.tsx", "key", 1, "features/integration/DataSourceFormModal.tsx",
            ["err.ECR-REQ-0422.dataSourceEndpointCarriesSecret", "err.ECR-REQ-0422.dataSourceCodeTaken"],
            "messageKey сервера, але лише з FieldOfKey — інші сюди не доходять."),

        new("features/notifications/ChannelsPanel.tsx", "`notifications.kind.${channel.kind}`", 1,
            "features/notifications/ChannelsPanel.tsx",
            ["notifications.kind.Smtp", "notifications.kind.TeamsWebhook"],
            "NotificationChannelKind сервера."),
        new("features/notifications/DeliveriesPanel.tsx", "`notifications.event.${row.eventKind}`", 1,
            "features/notifications/DeliveriesPanel.tsx", NotificationEventKeys,
            "NotificationEventKind сервера."),
        new("features/notifications/RulesMatrixPanel.tsx", "`notifications.event.${eventKind}`", 1,
            "features/notifications/RulesMatrixPanel.tsx", NotificationEventKeys,
            "NotificationEventKind сервера."),
        new("pages/admin/ExpressionsPage.tsx", "`expressions.check.${check}`", 1, "pages/admin/ExpressionsPage.tsx",
            ["expressions.check.Cycle", "expressions.check.References", "expressions.check.Types", "expressions.check.Units"],
            "SkippedChecks перевірки виразу."),
        new("pages/admin/RegistriesPage.tsx", "`registries.referenceKind.${kind}`", 1,
            "pages/admin/RegistriesPage.tsx",
            [
                "registries.referenceKind.cells", "registries.referenceKind.headerValues",
                "registries.referenceKind.registryValues", "registries.referenceKind.childEntries",
                "registries.referenceKind.links", "registries.referenceKind.methodologyConstants",
                "registries.referenceKind.methodologySubstances",
            ],
            "V-08: види посилань на запис довідника (RegistryEntryReferences.ByKind)."),

        new("shared/ui/StatusBadge.tsx", "statusKey(kind, state)", 1, "shared/ui/StatusBadge.tsx",
            [.. StatusKeys("sheet"), .. StatusKeys("period"), .. StatusKeys("job"), .. StatusKeys("version"),
             .. StatusKeys("project"), .. StatusKeys("health"), .. StatusKeys("severity"),
             .. StatusKeys("collectionRun"), .. StatusKeys("snapshot"), .. StatusKeys("notificationDelivery")],
            "Бейдж статусу: уся таблиця statusTable."),
        new("pages/admin/ExpressionsPage.tsx", "statusKey('version', v.status)", 1, "shared/ui/StatusBadge.tsx",
            StatusKeys("version"), "Статус версії виразу."),
        new("pages/admin/MethodologyVersionsPage.tsx", "statusKey('version', version.status)", 1,
            "shared/ui/StatusBadge.tsx", StatusKeys("version"), "Статус версії методології."),
        new("pages/admin/PeriodsPage.tsx", "statusKey('project', p.status)", 1, "shared/ui/StatusBadge.tsx",
            StatusKeys("project"), "Статус проєкту."),
        new("features/notifications/RulesMatrixPanel.tsx", "statusKey('severity', value)", 1,
            "shared/ui/StatusBadge.tsx", StatusKeys("severity"), "Серйозність правила сповіщення."),

        // Основа множини — у формах `.one`/`.other`, їх перевіряє основний сторож.
        new("shared/format/plural.ts", "`${keyBase}.${category}`", 1, null, [],
            "formatCount: основу бере з другого аргументу виклику."),

        // Ключі із сервера.
        new("features/expressions/markers.ts", "diagnostic.messageKey", 1, null, [], "messageKey діагностики виразу."),
        new("shared/ui/problemText.ts", "problem.title", 1, null, [], "title problem+json, коли він — ключ каталогу."),
        new("features/notifications/ChannelsPanel.tsx", "key", 1, null, [], "messageKey проби каналу."),
        new("features/integration/TestDataSourceModal.tsx", "key", 1, null, [], "messageKey проби джерела."),
        new("features/jobs/JobFacts.tsx", "errorKey(errorCode)", 1, null, [], "errorCode провалу фонової задачі — код каталогу помилок сервера."),
        new("shared/ui/problemText.ts", "key", 1, null, [],
            "errorCodeText (X-04): errorCode провалу задачі — код каталогу помилок сервера; без рядка — запасний текст викликача."),
        new("features/registries/RegistryImportPanel.tsx", "error.messageKey", 1, null, [],
            "messageKey рядка звіту імпорту записів довідника (BE-24, RegistryEntryImportError) — "
            + "реюзить відкритий набір ключів валідації UpsertRegistryEntryHandler, клієнт його не перелічує."),

        // F-15/B-12 (четвертий раунд UX): перелік проблем публікації методології —
        // закритий набір, що його породжує сервер (MethodologyPublishChecks).
        new("features/methodologies/publishError.ts", "key", 1, "features/methodologies/publishError.ts",
            [
                "publish.problem.constantNotNumber", "publish.problem.constantNoText",
                "publish.problem.categoryLabelInExpression", "publish.problem.textConstantInArithmetic",
                "publish.problem.numberReturnsText", "publish.problem.textReturnsNumber",
                "publish.problem.textOutput", "publish.problem.importNoVersion",
                "publish.problem.libraryHasRules", "publish.problem.ambiguousReference",
            ],
            "Пункт переліку проблем публікації (PublishProblemKeys)."),
    ];

    // ⚠ Властивості, а не поля: `DynamicKeySites` вище ініціалізується раніше
    // за поля, оголошені нижче, і побачив би в них `null`.
    private static string[] RouteLabelKeys =>
    [
        "nav.documents", "password.title", "nav.templates", "version.title", "tables.relationsTitle",
        "nav.registries", "registries.constructor", "nav.methodologies", "methodologies.versionsTitle",
        "nav.expressions", "nav.units", "nav.security", "nav.periods", "nav.sources", "nav.mapping",
        "nav.jobs", "nav.snapshots", "nav.campaign", "nav.audit", "nav.consistency", "nav.uiStrings",
        "nav.notifications", "nav.health", "nav.myGroups", "documents.title",
    ];

    /// <summary>Назви прав <c>permission.&lt;Code&gt;</c> — 41 право каталогу <c>sec.Permission</c>.</summary>
    private static string[] PermissionLabelKeys =>
    [
        "permission.Template.View",
        "permission.Template.Edit",
        "permission.Template.Publish",
        "permission.Registry.View",
        "permission.Registry.EditData",
        "permission.Registry.EditDefinition",
        "permission.Registry.Publish",
        "permission.Document.View",
        "permission.Document.Create",
        "permission.Document.Delete",
        "permission.Document.Import",
        "permission.Document.Export",
        "permission.Document.Reopen",
        "permission.Document.ChangeKey",
        "permission.Project.Manage",
        "permission.Period.Configure",
        "permission.Period.Reopen",
        "permission.Calculation.View",
        "permission.Calculation.EditFormula",
        "permission.Calculation.EditConstant",
        "permission.Calculation.EditRule",
        "permission.Calculation.Publish",
        "permission.Calculation.Recalculate",
        "permission.Calculation.ManageRequiredInputs",
        "permission.Report.ViewRegulatory",
        "permission.Report.BuildSnapshot",
        "permission.Report.Export",
        "permission.Report.EditDefinition",
        "permission.Report.ViewCampaign",
        "permission.Integration.View",
        "permission.Integration.Manage",
        "permission.Integration.EditSchedule",
        "permission.Uom.EditCatalog",
        "permission.Security.ManageUsers",
        "permission.Security.ManageRoles",
        "permission.Security.ViewAudit",
        "permission.Security.Simulate",
        "permission.System.ViewHealth",
        "permission.System.RunJob",
        "permission.System.ManageLocalization",
        "permission.System.ManageNotifications",
    ];

    private static string[] NotificationEventKeys =>
    [
        "notifications.event.JobFailed", "notifications.event.ConsistencyIssuesFound",
        "notifications.event.PartitionsRunningOut", "notifications.event.CollectionFailed",
        "notifications.event.ExportFailed",
    ];

    /// <summary>Стани <c>statusTable</c> у <c>StatusBadge.tsx</c> станом на 2026-09-21.</summary>
    private static string[] StatusKeys(string kind) => kind switch
    {
        "sheet" => Status(kind, "Draft", "Submitted", "Approved", "Rejected"),
        "period" => Status(kind, "Scheduled", "Open", "Grace", "Closed"),
        "job" => Status(kind, "Queued", "Running", "Succeeded", "Failed", "Cancelled", "Unknown", "Unavailable"),
        "version" => Status(kind, "Draft", "Published", "Deprecated"),
        "project" => Status(kind, "Draft", "Active", "Archived"),
        "health" => Status(kind, "Healthy", "Degraded", "Unhealthy"),
        "severity" => Status(kind, "Info", "Warning", "Error"),
        "collectionRun" => Status(kind, "Succeeded", "Degraded", "Failed"),
        "snapshot" => Status(kind, "Draft", "Approved", "Submitted"),
        "notificationDelivery" => Status(kind, "Sent", "Failed", "Suppressed"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Невідомий різновид статусу."),
    };

    private static string[] Status(string kind, params string[] states)
        => [.. states.Select(state => $"status.{kind}.{state}")];

    /// <summary>Місце, де ключ каталогу передано змінною.</summary>
    /// <param name="File">Файл виклику відносно <c>src/Ecr.Web/src</c>.</param>
    /// <param name="Expression">Перший аргумент <c>t(…)</c>, нормалізований.</param>
    /// <param name="Count">Скільки таких викликів у файлі.</param>
    /// <param name="Producer">Файл, де ці ключі написані; <c>null</c> — сервер.</param>
    /// <param name="Keys">Ключі, які можуть дійти до виклику.</param>
    /// <param name="Why">Що це за місце.</param>
    private sealed record DynamicKeySite(
        string File, string Expression, int Count, string? Producer, string[] Keys, string Why);

    private static string WebRoot()
    {
        var web = Path.Combine(SolutionRoot(), "src", "Ecr.Web", "src");
        Assert.True(Directory.Exists(web), $"Немає {web}.");
        return web;
    }

    private static HashSet<string> SeedCatalogKeys()
    {
        var seed = File.ReadAllText(
            Path.Combine(SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));

        var seeded = SeedKeyRegex.Matches(seed)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(seeded);
        return seeded;
    }

    /// <summary>Ключі, які клієнт просить у каталогу, — розібрані з тексту.</summary>
    /// <param name="Literals">Ключі-літерали першого аргументу <c>t(…)</c>, усі гілки умовного виразу.</param>
    /// <param name="PluralBases">Основи множини з другого аргументу <c>formatCount(…)</c>.</param>
    /// <param name="Dynamic">Виклики, ключ яких із тексту не видно.</param>
    private sealed record ClientKeyScan(
        List<ClientKeyUse> Literals, List<ClientKeyUse> PluralBases, List<ClientDynamicUse> Dynamic)
    {
        public static ClientKeyScan Of(string web)
        {
            var result = new ClientKeyScan([], [], []);

            var files = Directory
                .EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".tsx", StringComparison.Ordinal))
                .Where(f => !f.EndsWith(".d.ts", StringComparison.Ordinal))

                // Тести клієнта підставляють власні рядки — вимагати їх у
                // каталозі означало б забороняти перевіряти обробку промаху.
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}__tests__{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(f => !f.Contains(".test.", StringComparison.Ordinal) && !f.Contains(".spec.", StringComparison.Ordinal));

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(web, file).Replace('\\', '/');
                result.Add(relative, File.ReadAllText(file));
            }

            return result;
        }

        public static ClientKeyScan Scan(string file, string text)
        {
            var result = new ClientKeyScan([], [], []);
            result.Add(file, text);
            return result;
        }

        private void Add(string file, string text)
        {
            var source = TypeScriptSource.Parse(text);

            foreach (var call in source.Calls("t"))
            {
                if (call.Arguments.Count == 0)
                {
                    continue;
                }

                foreach (var branch in source.Branches(call.Arguments[0], ResolveStatusKey))
                {
                    if (branch.Literal is not null)
                    {
                        Literals.Add(new ClientKeyUse(branch.Literal, file, source.LineOf(call.Index)));
                    }
                    else
                    {
                        Dynamic.Add(new ClientDynamicUse(file, branch.Dynamic!, source.LineOf(call.Index)));
                    }
                }
            }

            foreach (var call in source.Calls("formatCount"))
            {
                if (call.Arguments.Count < 2)
                {
                    continue;
                }

                foreach (var branch in source.Branches(call.Arguments[1]))
                {
                    if (branch.Literal is not null)
                    {
                        PluralBases.Add(new ClientKeyUse(branch.Literal, file, source.LineOf(call.Index)));
                    }
                    else
                    {
                        Dynamic.Add(new ClientDynamicUse(file, "formatCount:" + branch.Dynamic, source.LineOf(call.Index)));
                    }
                }
            }
        }

        /// <summary>
        /// <c>statusKey('sheet', 'Draft')</c> → <c>status.sheet.Draft</c>: будівник
        /// ключа з двома літералами — той самий літерал, лише записаний інакше.
        /// </summary>
        /// <remarks>
        /// ⚠ Правило дзеркалить <c>statusKey</c> у <c>StatusBadge.tsx</c>. Якщо
        /// форма ключа там зміниться, розбіжність покаже
        /// <c>Перелік_ключів_через_змінну_не_застарів</c>: шаблон <c>status.</c>
        /// зникне з файла-джерела.
        /// </remarks>
        private static string? ResolveStatusKey(string expression)
            => StatusKeyCall().Match(expression) is { Success: true } m
                ? $"status.{m.Groups[1].Value}.{m.Groups[2].Value}"
                : null;
    }

    private sealed record ClientKeyUse(string Key, string File, int Line);

    private sealed record ClientDynamicUse(string File, string Expression, int Line);

    [GeneratedRegex(@"^statusKey\(\s*'(\w+)'\s*,\s*'(\w+)'\s*\)$")]
    private static partial Regex StatusKeyCall();

    /// <summary>Рядок, схожий на ключ каталогу: <c>група.ім'я</c>, починається з малої.</summary>
    [GeneratedRegex(@"^[a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9-]+)+$")]
    private static partial Regex CatalogKeyShape();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-5.17")]
    public void Seed_не_створює_жодного_маршруту_погодження()
    {
        // ⛔ Критерій `4.8` директиви №04. Маршрут у seed зробив би
        // багатоетапне затвердження поведінкою за замовчуванням для КОЖНОЇ
        // щойно розгорнутої системи — тобто змінив би те, що працює, без
        // жодного рішення людини.
        //
        // ⚠ Перевіряється ФАЙЛ, а не база: тестова база спільна, і сусідній
        // тест, який заводить маршрут для власного проєкту, зробив би цю
        // перевірку то зеленою, то червоною залежно від порядку прогону.
        var seed = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));

        Assert.DoesNotContain("wf.ApprovalRoute", seed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wf.ApprovalStep", seed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жоден_ключ_каталогу_не_повторюється_в_seed()
    {
        // ⛔ Seed наповнює каталог одним `MERGE … USING (VALUES …)`. Два
        // рядки з однаковим ключем валять його в рантаймі: «The MERGE
        // statement attempted to UPDATE or DELETE the same row more than
        // once». Не при складанні, не в тестах — при СТАРТІ ЗАСТОСУНКУ,
        // бо seed виконує він (`02-contracts.md` §14).
        //
        // ⚠ Сторож написаний після реального дубля: додаючи підказку
        // `deny.OutsidePermitWindow`, я вставив її двома скриптами в два
        // місця. Помітив випадково — жодна перевірка цього не ловила, а
        // ціна помилки максимальна: чиста база не піднімається взагалі.
        var seed = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));

        // ⚠ Лише блок MERGE: секція змінених текстів над ним законно називає
        // ті самі ключі вдруге (старе → нове), і MERGE вона не валить.
        var start = seed.IndexOf("MERGE sys_ecr.UiString AS t", StringComparison.Ordinal);
        var end = start < 0 ? -1 : seed.IndexOf("WHEN NOT MATCHED", start, StringComparison.Ordinal);
        Assert.True(end > start, "У 09-seed.sql немає блоку MERGE sys_ecr.UiString AS t — сторож дивиться не туди.");

        var keys = SeedKeyRegex.Matches(seed[start..end]).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(keys);

        var duplicates = keys
            .GroupBy(k => k, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} — {g.Count()} рази")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "Ключі каталогу, вставлені двічі — seed упаде на старті:"
            + Environment.NewLine + string.Join(Environment.NewLine, duplicates));
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
    public void Кожна_дія_сервера_має_споживача_в_інтерфейсі()
    {
        // ⛔ Тринадцятий сторож, і він дивиться у ЗВОРОТНИЙ бік від дванадцяти
        // попередніх. Ті перевіряють, що клієнт не кличе неіснуючого; цей — що
        // сервер не вміє того, чого користувач не може зробити.
        //
        // ⚠ Знайдено аудитом, і знахідка виявилася найдорожчою за весь проєкт:
        // із сорока дій запису дев'ятнадцять не мали в інтерфейсі жодної
        // кнопки. Серед них `POST /documents/{id}/approve` — тобто **робочий
        // процес обривався на поданні**: документ можна було подати і не можна
        // затвердити, а без `Approved` дані не стають дійсними (`ФВ-5.14`) і не
        // потрапляють у звіти для регулятора (`ФВ-10.11`).
        //
        // ⚠ Жоден наявний сторож цього не бачив за побудовою: усі вони йдуть
        // від клієнта до сервера. Тести API були зелені — вони перевіряють,
        // що ендпоінт працює, а не що до нього можна дійти.
        // ⛔ Тепер і ЧИТАННЯ теж. Спершу сторож дивився лише на дії
        // запису — і не бачив, що журнал аудиту, порівняння версій і
        // позначення одиниць не мали в інтерфейсі жодного споживача.
        // Право `Security.ViewAudit` видавалося ролям, ендпоінт працював,
        // а подивитися журнал було ніде: відповідь на питання «хто змінив
        // це число» існувала і була недосяжна.
        var declared = Declared()
            .Select(d => (d.Method, Path: Placeholders(d.Path)))
            .ToHashSet();

        // ⛔ Порівнюється МЕТОД РАЗОМ ІЗ ШЛЯХОМ. Спершу сторож дивився на самі
        // шляхи — і через це вважав досяжним `POST /registries/{code}/entries`
        // лише тому, що клієнт ЧИТАЄ ту саму адресу. Тобто екран, який уміє
        // показати довідник і не вміє завести в ньому запис, для сторожа
        // виглядав повним. Читання не є споживачем запису.
        var used = ClientCalls();
        var exempt = ServerOnlyActions();

        var unreachable = declared
            .Where(d => !used.Typed.Contains((d.Method, d.Path)) && !used.Unknown.Contains(d.Path))
            .Where(d => !exempt.Contains($"{d.Method} {d.Path}"))
            .Select(d => $"{d.Method} {d.Path}")
            .Order(StringComparer.Ordinal)
            .ToList();

        // ⚠ Перелік у повідомленні ПОВНІСТЮ. `Assert.Empty` обрізає колекцію
        // трьома крапками після п'ятого елемента, і побачити решту можна лише
        // запустивши сторожа окремо з-під зневаджувача. Тут якраз той випадок,
        // коли важливий саме весь список: він і є планом робіт.
        Assert.True(
            unreachable.Count == 0,
            $"Дій сервера без споживача в інтерфейсі: {unreachable.Count}."
            + Environment.NewLine
            + string.Join(Environment.NewLine, unreachable));
    }

    /// <summary>
    /// Виклики API в коді клієнта: метод разом зі шляхом.
    /// </summary>
    /// <remarks>
    /// ⚠ Метод не оголошений окремим полем — він живе в об'єкті налаштувань
    /// одразу після адреси (<c>{ method: 'POST' }</c>), а за замовчуванням
    /// <c>fetch</c> робить <c>GET</c>. Тому читається вікно ПІСЛЯ адреси, і
    /// вікно обрізається на наступній адресі: інакше сусідній запит на
    /// відстані рядка приписав би свій метод попередньому.
    ///
    /// ⚠ <c>apiEnqueue</c> — завжди <c>POST</c>: він ставить довгу операцію в
    /// чергу і не приймає методу. Впізнається за назвою ПЕРЕД адресою.
    /// </remarks>
    private static ClientCallSet ClientCalls()
    {
        var web = Path.Combine(SolutionRoot(), "src", "Ecr.Web", "src");

        var typed = new HashSet<(string, string)>();
        var unknown = new HashSet<string>(StringComparer.Ordinal);

        var files = Directory
            .EnumerateFiles(web, "*.ts", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(web, "*.tsx", SearchOption.AllDirectories))
            .Where(f => !f.Contains("schema.d.ts", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var text = WithoutComments(File.ReadAllText(file));

            foreach (Match match in ApiPathRegex.Matches(text))
            {
                var path = Placeholders(match.Groups[1].Value);
                var method = MethodOf(text, match);

                if (method is null)
                {
                    unknown.Add(path);
                }
                else
                {
                    typed.Add((method, path));
                }
            }
        }

        return new ClientCallSet(typed, unknown);
    }

    /// <summary>Виклики клієнта: з відомим методом і без нього.</summary>
    /// <param name="Typed">Пари «метод + шлях», у яких метод видно з коду.</param>
    /// <param name="Unknown">
    /// Шляхи, які передані кудись далі — метод визначає та функція. Такий
    /// шлях зараховується будь-якому методу: інакше сторож оголосив би
    /// недосяжним те, до чого кнопка є, і його перестали б читати.
    /// </param>
    private sealed record ClientCallSet(
        HashSet<(string Method, string Path)> Typed,
        HashSet<string> Unknown);

    /// <summary>
    /// Метод HTTP, яким клієнт кличе цю адресу; <c>null</c> — з коду не видно.
    /// </summary>
    private static string? MethodOf(string text, Match address)
    {
        var start = address.Index + address.Length;

        // Вікно до наступної адреси або 400 символів — що ближче: сусідній
        // запит за рядок нижче інакше приписав би свій метод цьому.
        var limit = Math.Min(start + 400, text.Length);
        var next = text.IndexOf("/api/v1", start, StringComparison.Ordinal);
        if (next >= 0 && next < limit)
        {
            limit = next;
        }

        var declared = MethodOptionRegex.Match(text[start..limit]);
        if (declared.Success)
        {
            return declared.Groups[1].Value.ToUpperInvariant();
        }

        // Метод не названий — значення має те, ЩО саме кличуть.
        var callee = CalleeRegex.Match(text[Math.Max(0, address.Index - 80)..address.Index]);
        if (!callee.Success)
        {
            return null;
        }

        return callee.Groups[1].Value switch
        {
            // Ставить довгу операцію в чергу; методу не приймає взагалі.
            "apiEnqueue" => "POST",

            // Базовий клієнт без налаштувань — це `GET` за визначенням `fetch`.
            "apiFetch" or "apiFetchIfChanged" or "apiFetchRaw" or "fetch" => "GET",

            // Адреса пішла у власну функцію (форма входу віддає її своєму
            // `submit`). Метод вирішує вона, і вгадувати його тут — гірше,
            // ніж чесно сказати «не видно».
            _ => null,
        };
    }

    /// <summary>
    /// Дії, до яких інтерфейсу не треба — і чому саме.
    /// </summary>
    /// <remarks>
    /// ⛔ Перелік короткий навмисно. Кожен рядок тут — це твердження «людині
    /// це робити не потрібно», і воно має бути правдою: вигідніше додати
    /// кнопку, ніж пояснювати, чому її немає.
    /// </remarks>
    private static HashSet<string> ServerOnlyActions() => new(StringComparer.Ordinal)
    {
        // Викликається клієнтом опосередковано, всередині форми входу.
        "POST /api/v1/login/windows",

        // ⚠ Тут стояла ще й `POST /api/v1/units/convert` з поясненням
        // «числа конвертуються там, де їх вводять». Пояснення було
        // НЕПРАВДОЮ: жодне місце клієнта нічого не конвертувало.
        // Звільнення, яке стверджує неіснуючу поведінку, гірше за
        // відсутню кнопку — воно закриває питання замість відповіді.
    };

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
                "Ecr.Calculations", "Ecr.Expressions", "Ecr.Adapters.PiAf", "Ecr.Adapters.Sql",
                "Ecr.Adapters.Excel")
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
        // ⛔ Порядок значущий. Інтерполяція згортається ПЕРШОЮ, і лише потім
        // відрізається рядок запиту: інакше `Split('?')` ріже не тільки
        // query, а й оператор `??` усередині виразу, і адреса
        // `/api/v1/jobs/${encodeURIComponent(jobId ?? '')}` перетворюється на
        // `/api/v1/jobs/${encodeURIComponent(jobId ` — шлях, якого на сервері
        // немає і бути не може.
        //
        // ⚠ Знайдено аудитом. Сторож при цьому не «пропускав» помилку — він
        // її ВИГАДУВАВ би, якби взагалі бачив ці адреси; попередній вираз
        // захоплення обривався на тому самому пробілі, тож обидві вади разом
        // давали тишу.
        var withoutInterpolation = InterpolationRegex.Replace(path, "{p}");

        return BraceRegex.Replace(withoutInterpolation.Split('?')[0].TrimEnd('/'), "{p}");
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

    /// <summary>Кого кличуть з цією адресою — назва функції перед дужкою.</summary>
    [GeneratedRegex(@"(\w+)\s*(?:<[^<>]*>)?\s*\(\s*$")]
    private static partial Regex CalleeRegex { get; }

    /// <summary>Метод у налаштуваннях запиту: <c>method: 'POST'</c>.</summary>
    [GeneratedRegex(@"method:\s*'(get|post|put|patch|delete)'", RegexOptions.IgnoreCase)]
    private static partial Regex MethodOptionRegex { get; }

    /// <summary>Адреса API в клієнтському коді.</summary>
    /// <remarks>
    /// ⛔ Пробіли ВСЕРЕДИНІ <c>${…}</c> дозволені явно. Попередній вираз
    /// обривався на першому пробілі, і адреса виду
    /// <c>`/api/v1/roles/${roleId ?? 0}/grants`</c> для сторожа просто не
    /// існувала — тобто сторож «кожна адреса клієнта існує на сервері» мовчки
    /// пропускав кожен виклик із виразом у шляху.
    ///
    /// ⚠ Знайдено аудитом: інший підрахунок дав 21 «невикористаний» ендпоінт
    /// замість 19, і різниця виявилася саме цією сліпотою.
    /// </remarks>
    [GeneratedRegex(@"['""`](/(?:api/v1|health)/(?:\$\{[^}]*\}|[^'""`\s])*)['""`]")]
    private static partial Regex ApiPathRegex { get; }

    [GeneratedRegex(@"\$\{(?:[^{}]|\{[^{}]*\})*\}")]
    private static partial Regex InterpolationRegex { get; }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex BraceRegex { get; }

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
    /// <summary>
    /// Кожен ендпоінт перевіряє САМЕ ТЕ право, яке оголошує контракт.
    /// </summary>
    /// <remarks>
    /// ⛔ Сторож вище (<c>Кожне_право_з_таблиці_ендпоінтів_десь_перевіряється</c>)
    /// питає лише, чи згадується право хоч десь у застосунку. Це майже нічого
    /// не доводить: варто одному обробнику перевірити <c>Template.Edit</c>, і
    /// «перевіреними» вважаються ВСІ п'ять ендпоінтів, що його оголошують.
    ///
    /// ⛔ Так і сталося (<c>A7-53</c>): клон версії, патч презентації,
    /// створення версії, diff і структура не перевіряли нічого, крім
    /// <c>[Authorize]</c>. Тобто будь-який автентифікований користувач —
    /// оператор введення, погоджувач, аудитор — міг клонувати версію шаблону
    /// і правити підписи колонок. Обидва попередні сторожі були зелені.
    ///
    /// ⚠ Перевіряється ТІЛО ОБРОБНИКА, яким користується дія, а не файл
    /// цілком: у спільному файлі (<c>TemplateQueryHandlers.cs</c>) сусідній
    /// клас перевіряє інше право, і пошук по файлу знову доводив би не те.
    ///
    /// ⛔ <b>СТАТИЧНА перевірка, а не поведінкова.</b> Сторож читає ТЕКСТ
    /// вихідних файлів регулярними виразами і не виконує жодного рядка
    /// застосунку. Він доводить рівно одне: у тілі обробника ЗГАДАНО рядок
    /// із назвою права, оголошеного контрактом.
    ///
    /// ⛔ Що він НЕ доводить і не може довести за побудовою:
    /// <list type="bullet">
    /// <item>що перевірка права справді викликається, а не лежить у мертвій
    /// гілці, у закоментованому фрагменті чи в <c>nameof</c>;</item>
    /// <item>що вона викликається ДО дії, а не після неї;</item>
    /// <item>що при відмові обробник зупиняється;</item>
    /// <item>що <c>IAccessDecisionService</c> ухвалює правильне рішення.</item>
    /// </list>
    /// Тобто він лишається зеленим під будь-якою поведінковою мутацією, яка
    /// зберігає текст. Зелений результат тут НЕ є доказом того, що доступ
    /// працює; доказ дають наскрізні сценарії доступу
    /// (<c>tests/Ecr.Scenarios.Tests</c>) на живій базі й живому HTTP.
    ///
    /// ⚠ Сторож усе одно корисний: він ловить розходження контракту з кодом —
    /// ендпоінт, який ЗАБУЛИ прив'язати до оголошеного права (<c>A7-53</c>).
    /// Це структурна властивість дерева, і для неї читання тексту доречне.
    /// Позначка тут стоїть, щоб зелений результат не рахували за
    /// підтвердження рантайму (директива №09 §8.2).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    public void Кожен_ендпоінт_перевіряє_саме_своє_право_СТАТИЧНО()
    {
        var handlers = HandlerBodies();
        Assert.NotEmpty(handlers);

        var actions = ActionHandlers();
        Assert.NotEmpty(actions);

        var offenders = new List<string>();

        foreach (var (key, permission) in EndpointPermissions())
        {
            if (!actions.TryGetValue(key, out var used))
            {
                // Відсутність реалізації ловить перший сторож; тут вона не
                // має перетворюватися на другу скаргу про те саме.
                continue;
            }

            var checks = used
                .Where(handlers.ContainsKey)
                .Any(h => handlers[h].Contains(permission));

            if (!checks)
            {
                offenders.Add(
                    $"{key.Method} {key.Path} оголошує {permission}, "
                    + $"але жоден із обробників [{string.Join(", ", used)}] його не перевіряє");
            }
        }

        // ⚠ Перелік у повідомленні, а не `Assert.Empty`: сторож або мовчить,
        // або має назвати КОЖЕН ендпоінт. Обрізаний xUnit'ом список змушує
        // шукати решту вручну, і саме тоді половину «полагодять потім».
        Assert.True(
            offenders.Count == 0,
            "Ендпоінти, які не перевіряють оголошеного права:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Маршрут → оголошене право; рядки без права пропущені.</summary>
    private static Dictionary<(string Method, string Path), string> EndpointPermissions()
    {
        var path = Path.Combine(SolutionRoot(), "docs", "build", "02-contracts.md");
        var result = new Dictionary<(string, string), string>();

        foreach (Match row in ContractRowWithPermission.Matches(File.ReadAllText(path)))
        {
            var stage = int.Parse(row.Groups[4].Value, CultureInfo.InvariantCulture);
            if (DeferredStages.Contains(stage))
            {
                continue;
            }

            var permission = row.Groups[3].Value;
            if (!permission.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            result[(row.Groups[1].Value.ToUpperInvariant(),
                    Normalize(row.Groups[2].Value.Split('?')[0].TrimEnd('/')))] = permission;
        }

        return result;
    }

    /// <summary>Маршрут → типи обробників, якими користується дія контролера.</summary>
    /// <remarks>
    /// ⚠ Ім'я в тілі дії зіставляється з параметром первинного конструктора
    /// контролера: саме він називає тип. Без цього кроку залишалося б лише
    /// ім'я змінної, і сторож не знав би, чий код читати.
    /// </remarks>
    private static Dictionary<(string Method, string Path), List<string>> ActionHandlers()
    {
        var result = new Dictionary<(string, string), List<string>>();
        var directory = Path.Combine(SolutionRoot(), "src", "Ecr.Api", "Controllers");

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var source = File.ReadAllText(file);
            var route = RouteAttributeRegex.Match(source);
            var baseRoute = route.Success ? route.Groups[1].Value : string.Empty;

            // ⚠ Параметри беруться зі СПИСКУ первинного конструктора, а не
            // рядками файла: половина контролерів оголошує його в один рядок,
            // і прив'язка до відступу пропускала їх мовчки — сторож доповідав
            // «жодного обробника» там, де обробник є.
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            var ctor = ControllerCtorRegex.Match(source);

            if (ctor.Success)
            {
                foreach (Match parameter in CtorParameterRegex.Matches(ctor.Groups[1].Value))
                {
                    types[parameter.Groups[2].Value] = parameter.Groups[1].Value.Split('.')[^1];
                }
            }

            // ⚠ Вікно тіла ширше, ніж у `Implemented()`: там достатньо
            // побачити `throw` одразу після сигнатури, а тут виклик обробника
            // може стояти після перевірки сторінки і трьох рядків розбору.
            // З вузьким вікном сторож доповідав про вісім дій «жодного
            // обробника» — тобто мовчав про них.
            foreach (Match action in ActionBodyRegex.Matches(source))
            {
                var suffix = action.Groups[2].Value;
                var full = "/" + string.Join('/', new[] { baseRoute, suffix }.Where(p => p.Length > 0));

                // ⚠ …але обрізане наступним атрибутом маршруту. Інакше в дію
                // потрапляє сусідня, і перевірка сусіда зараховується цій —
                // сторож ставав би тим самим «десь перевіряється», який він
                // і покликаний замінити.
                var body = action.Groups["body"].Value;
                var next = body.IndexOf("[Http", StringComparison.Ordinal);
                if (next >= 0)
                {
                    body = body[..next];
                }

                var used = CallRegex.Matches(body)
                    .Select(m => m.Groups[1].Value)
                    .Where(types.ContainsKey)
                    .Select(name => types[name])
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                result[(action.Groups[1].Value.ToUpperInvariant(), Normalize(full))] = used;
            }
        }

        return result;
    }

    /// <summary>Тип обробника → права, які він справді перевіряє.</summary>
    /// <remarks>
    /// ⚠ Клас береться ЗРІЗОМ файла, а не файлом цілком: у спільному файлі
    /// (<c>TemplateQueryHandlers.cs</c>, <c>RoleAndUserHandlers.cs</c>) сусідній
    /// клас перевіряє інше право, і пошук по файлу доводив би не те.
    ///
    /// ⚠ Право найчастіше не літерал, а константа сусіда:
    /// <c>ListTemplatesHandler.Permission</c>. Посилання тому
    /// <b>розв'язуються</b> — інакше сторож скаржився б на п'ять обробників,
    /// кожен із яких право перевіряє, і його вимкнули б за шум.
    /// </remarks>
    private static Dictionary<string, HashSet<string>> HandlerBodies()
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        var directory = Path.Combine(SolutionRoot(), "src", "Ecr.Application");

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            var starts = ClassRegex.Matches(source).ToList();

            for (var i = 0; i < starts.Count; i++)
            {
                var from = starts[i].Index;
                var to = i + 1 < starts.Count ? starts[i + 1].Index : source.Length;
                bodies[starts[i].Groups[1].Value] = source[from..to];
            }
        }

        // Константа права, оголошена класом: `public const string Permission = "…"`.
        var constants = bodies.ToDictionary(
            pair => pair.Key,
            pair => PermissionConstRegex.Match(pair.Value) is { Success: true } m
                ? m.Groups[1].Value
                : null,
            StringComparer.Ordinal);

        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (name, body) in bodies)
        {
            var permissions = new HashSet<string>(StringComparer.Ordinal);

            // Літерал у самому тілі.
            foreach (Match literal in PermissionLiteralRegex.Matches(body))
            {
                permissions.Add(literal.Groups[1].Value);
            }

            // Власна константа: клас, який її оголошує, нею й користується.
            if (constants.TryGetValue(name, out var own) && own is not null)
            {
                permissions.Add(own);
            }

            // Константа або допоміжна перевірка сусіда: `Інший.Permission`,
            // `Інший.RequireAsync(…)`, `Інший.ProfileAsync(…)`.
            foreach (Match reference in QualifiedPermissionRegex.Matches(body))
            {
                if (constants.TryGetValue(reference.Groups[1].Value, out var borrowed)
                    && borrowed is not null)
                {
                    permissions.Add(borrowed);
                }
            }

            result[name] = permissions;
        }

        return result;
    }

    /// <summary>Дія контролера з широким вікном тіла — до наступного атрибута.</summary>
    [GeneratedRegex(
        @"\[Http(Get|Post|Put|Patch|Delete)(?:\(""([^""]*)""\))?\]"
        + @".*?public\s+(?:async\s+)?(?:Task<[^(]*?>|Task|IActionResult)\s+\w+\s*\("
        + @"[^)]*\)(?=(?<body>.{0,2000}))",
        RegexOptions.Singleline)]
    private static partial Regex ActionBodyRegex { get; }

    /// <summary>Константа права, оголошена класом.</summary>
    [GeneratedRegex(@"const\s+string\s+Permission\s*=\s*""([^""]+)""")]
    private static partial Regex PermissionConstRegex { get; }

    /// <summary>Літерал права: <c>"Область.Дія"</c>.</summary>
    [GeneratedRegex(@"""([A-Z]\w+\.[A-Z]\w+)""")]
    private static partial Regex PermissionLiteralRegex { get; }

    /// <summary>Позичена константа або допоміжна перевірка сусіда.</summary>
    [GeneratedRegex(@"\b(\w+Handler)\s*\.\s*(?:Permission\b|RequireAsync\(|ProfileAsync\()")]
    private static partial Regex QualifiedPermissionRegex { get; }

    [GeneratedRegex(@"^\|\s*`(GET|POST|PUT|PATCH|DELETE)`\s*\|\s*`([^`]+)`\s*\|\s*`?([^|`]*)`?\s*\|\s*(\d)\s*\|",
                    RegexOptions.Multiline)]
    private static partial Regex ContractRowWithPermission { get; }

    /// <summary>Список параметрів первинного конструктора контролера.</summary>
    [GeneratedRegex(@"class\s+\w+Controller\s*\(([\s\S]*?)\)\s*:\s*ControllerBase")]
    private static partial Regex ControllerCtorRegex { get; }

    /// <summary>Параметр первинного конструктора: тип і ім'я.</summary>
    [GeneratedRegex(@"([A-Z][\w.<>]*)\s+([a-z]\w*)\s*(?=,|$)", RegexOptions.Multiline)]
    private static partial Regex CtorParameterRegex { get; }

    /// <summary>Виклик методу на змінній: <c>handler.DoAsync(</c>.</summary>
    [GeneratedRegex(@"\b([a-z]\w*)\s*\n?\s*\.\s*\w+\s*\(")]
    private static partial Regex CallRegex { get; }

    /// <summary>Оголошення класу верхнього рівня.</summary>
    [GeneratedRegex(@"^public\s+(?:sealed\s+)?(?:partial\s+)?class\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex ClassRegex { get; }

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
