using Ecr.Infrastructure.Persistence;
using Ecr.Application.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Seed: ідемпотентність і повнота.</summary>
[Collection("SqlServer")]
public sealed class SeedTests(SqlServerFixture sql)
{
    /// <summary>Каталог прав із <c>02a-db-schema.md</c> §17.</summary>
    /// <remarks>
    /// Числа зашиті навмисно: якщо хтось додасть право в seed і не додасть
    /// сюди, тест впаде — і це правильно. Право, якого немає в цьому списку,
    /// ніхто не перевіряв.
    /// </remarks>
    private const int ExpectedPermissions = 42;

    private const int ExpectedDangerous = 11;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторний_запуск_не_створює_дублікатів()
    {
        var before = await CountsAsync();

        // Seed уже виконано фікстурою; запускаємо ВДРУГЕ на тій самій базі.
        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        var after = await CountsAsync();

        // MERGE … WHEN NOT MATCHED — не оптимізація, а умова перезапуску:
        // розгортання переграється, і другий прогін не має подвоїти каталог.
        Assert.Equal(before, after);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9")]
    [Trait("Requirement", "ФВ-2.2")]
    public async Task Створюються_три_мови_і_рівно_одна_за_замовчуванням()
    {
        Assert.Equal(3, await ScalarAsync("SELECT COUNT(*) FROM sys_ecr.Language"));

        // «Рівно одна» тримається фільтрованим індексом UX_Language_Default,
        // а не домовленістю: дві мови за замовчуванням зробили б підміну
        // за ФВ-14.9 недетермінованою.
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM sys_ecr.Language WHERE IsDefault = 1"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9")]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Створюються_усі_права_з_каталогу()
    {
        Assert.Equal(ExpectedPermissions, await ScalarAsync("SELECT COUNT(*) FROM sec.Permission"));
        Assert.Equal(ExpectedDangerous,
            await ScalarAsync("SELECT COUNT(*) FROM sec.Permission WHERE IsDangerous = 1"));

        // Права оголошує код, і саме він їх перевіряє: право, якого немає в
        // каталозі, не можна ні видати, ні перевірити.
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Period.Reopen' AND IsDangerous = 1"));

        // ⛔ `Report.EditDefinition` — НЕБЕЗПЕЧНЕ, і саме це число тут
        // важливе. Вбудований `Approver` має шаблон `Report.%`, і право,
        // позначене безпечним, дісталося б кожному погоджувачу мовчки —
        // авторство державної форми (`ФВ-10.4`) роздалося б правкою одного
        // рядка каталогу.
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Report.EditDefinition' AND IsDangerous = 1"));

        // ⛔ `Report.ViewCampaign` (`BE-22`) — те саме, і тут це ЄДИНИЙ спосіб
        // виконати рішення людини на `Q15-07`: «окреме право, видається явно».
        // Позначка `IsDangerous = 1` — не оцінка ризику втратити дані, а
        // механізм: фільтр `IsDangerous = 0` у MERGE роздач тримає право поза
        // шаблоном `Report.%` вбудованого `Approver`. Позначене безпечним, воно
        // мовчки відкрило б кожному погоджувачу коди й назви ВСІХ проєктів
        // системи разом із лічильниками їхніх документів.
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Report.ViewCampaign' AND IsDangerous = 1"));

        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.RolePermission WHERE PermissionCode = N'Report.ViewCampaign'"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Право_на_сповіщення_є_в_каталозі_небезпечне_і_нікому_не_роздане()
    {
        // `BE-32`: носій вирішує, куди сервер шле повідомлення, і замінює
        // секрети каналів — той самий клас, що `Integration.Manage`.
        Assert.Equal(1, await ScalarAsync("""
            SELECT COUNT(*) FROM sec.Permission
            WHERE Code = N'System.ManageNotifications' AND [Group] = N'System' AND IsDangerous = 1
            """));

        // ⛔ Шаблон `%` системного адміністратора його не видає: фільтр
        // `IsDangerous = 0` стоїть у самому MERGE роздач.
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.RolePermission WHERE PermissionCode = N'System.ManageNotifications'"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Seed_прибирає_зняте_право_Template_Migrate_разом_із_роздачами()
    {
        // Чиста база: права немає взагалі (директива №15, рішення 4).
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Template.Migrate'"));

        // ⚠ Стара база: MERGE лише додає, тож без явного DELETE право й роздача
        // пережили б оновлення. Роздача — і вбудованій ролі, і власній: шаблон
        // `Template.%` колись видав його обом шляхам.
        await ExecuteAsync("""
            INSERT sec.Permission (Code, [Group], NameL10n, IsDangerous)
            VALUES (N'Template.Migrate', N'Template', N'{"en":"Template.Migrate"}', 0);
            INSERT sec.RolePermission (RoleId, PermissionCode)
            SELECT Id, N'Template.Migrate' FROM sec.Role WHERE Code IN (N'TemplateAdministrator', N'Viewer');
            """);

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.RolePermission WHERE PermissionCode = N'Template.Migrate'"));
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Template.Migrate'"));
        Assert.Equal(ExpectedPermissions, await ScalarAsync("SELECT COUNT(*) FROM sec.Permission"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Seed_прибирає_зняті_права_EditScript_і_MarkSubmitted_разом_із_роздачами()
    {
        // Рішення людини 2026-09-21: обидва права нічого не відкривали.
        Assert.Equal(0, await ScalarAsync("""
            SELECT COUNT(*) FROM sec.Permission
            WHERE Code IN (N'Calculation.EditScript', N'Report.MarkSubmitted')
            """));

        // ⚠ Стара база: обидва права є, і роздані — `Report.MarkSubmitted` колись
        // приходив погоджувачу шаблоном `Report.%`, EditScript — власній ролі вручну.
        await ExecuteAsync("""
            INSERT sec.Permission (Code, [Group], NameL10n, IsDangerous)
            VALUES (N'Calculation.EditScript', N'Calculation', N'{"en":"Calculation.EditScript"}', 1),
                   (N'Report.MarkSubmitted',   N'Report',      N'{"en":"Report.MarkSubmitted"}',   0);
            INSERT sec.RolePermission (RoleId, PermissionCode)
            SELECT r.Id, p.Code FROM sec.Role AS r
            CROSS JOIN (VALUES (N'Calculation.EditScript'), (N'Report.MarkSubmitted')) AS p (Code)
            WHERE r.Code IN (N'Approver', N'Viewer');
            """);

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        foreach (var code in (string[])["Calculation.EditScript", "Report.MarkSubmitted"])
        {
            Assert.Equal(0, await ScalarAsync(
                $"SELECT COUNT(*) FROM sec.RolePermission WHERE PermissionCode = N'{code}'"));
            Assert.Equal(0, await ScalarAsync(
                $"SELECT COUNT(*) FROM sec.Permission WHERE Code = N'{code}'"));
        }

        Assert.Equal(ExpectedPermissions, await ScalarAsync("SELECT COUNT(*) FROM sec.Permission"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Вміст_регуляторного_зрізу_має_лише_переглядач_звітів_аудитор_погоджувач_і_адміністратор()
    {
        // ⛔ Рішення людини 2026-09-29 («ні, додай роль»): вміст зрізу —
        // окреме право `Report.ViewSnapshot`, безпечне (шаблони його беруть).
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Report.ViewSnapshot' AND IsDangerous = 0"));

        // Роль «Переглядач звітів» — вбудована, і рівно з тим, що треба для
        // перегляду: сторінка й перелік, вміст, книга.
        Assert.Equal(
            "Report.Export,Report.ViewRegulatory,Report.ViewSnapshot",
            await StringAsync("""
                SELECT STRING_AGG(rp.PermissionCode, N',') WITHIN GROUP (ORDER BY rp.PermissionCode)
                FROM sec.RolePermission AS rp
                JOIN sec.Role AS r ON r.Id = rp.RoleId
                WHERE r.Code = N'ReportViewer' AND r.IsBuiltIn = 1
                """));

        // ⛔ Хто будує й погоджує звіти — має (шаблони `Report.%` і `%`); хто
        // лише читає чи вводить дані — НІ, хоч `Report.ViewRegulatory` /
        // `Report.Export` у них лишились. ✎ Аудитор — має (рішення людини
        // 2026-09-29, 15:29: «бачить вміст за замовчуванням — так»), але
        // книги (`Report.Export`) йому не додано.
        Assert.Equal(
            "Approver,Auditor,ReportViewer,SystemAdministrator",
            await StringAsync("""
                SELECT STRING_AGG(r.Code, N',') WITHIN GROUP (ORDER BY r.Code)
                FROM sec.RolePermission AS rp
                JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
                WHERE rp.PermissionCode = N'Report.ViewSnapshot'
                """));
        Assert.Equal(0, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'Auditor' AND rp.PermissionCode = N'Report.Export'
            """));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Розгорнута_база_без_права_на_вміст_зрізу_отримує_його_повторним_seed()
    {
        // ⚠ Стара база: ні права, ні ролі, ні роздач. `SeedRunner` на наступному
        // старті мусить завести все сам — без окремого скрипта оновлення.
        await ExecuteAsync("""
            DELETE FROM sec.RolePermission WHERE PermissionCode = N'Report.ViewSnapshot';
            DELETE rp FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId WHERE r.Code = N'ReportViewer';
            DELETE FROM sec.Permission WHERE Code = N'Report.ViewSnapshot';
            """);

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Report.ViewSnapshot'"));
        Assert.Equal(4, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE rp.PermissionCode = N'Report.ViewSnapshot'
              AND r.Code IN (N'Approver', N'SystemAdministrator', N'ReportViewer', N'Auditor')
            """));
        Assert.Equal(3, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'ReportViewer'
            """));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]

    // ⚠ Трейта `ФВ-10.7` тут навмисно НЕМАЄ, хоча посіяний `IEC` узятий саме
    // з її каталогу. Вимога звільнена (`contracts/trace-exempt.md`): каталог
    // державних форм — перелік того, ЩО має бути, а не поведінка системи, і
    // один посіяний рядок із семи її не покриває. Трейт зробив би вимогу
    // «покритою і звільненою водночас» — суперечність, яку ловить
    // `RequirementCensusTests`.
    public async Task Seed_заводить_один_опис_звіту_з_опублікованою_версією()
    {
        // ⛔ Без цього рядка звітність існує і не працює: побудова зрізу
        // резолвить версію ЗА КОДОМ, а `rpt.ReportDef` не створювало ніщо —
        // ні код, ні seed, ні тести. Чиста база відмовляла `ECR-RPT-0404` на
        // будь-який код, який можна було ввести (директива №09 `W7`).
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM rpt.ReportDef WHERE Code = N'IEC' AND IsActive = 1 AND IsRegulatory = 1"));

        // ⚠ Версія саме ОПУБЛІКОВАНА (Status = 1). Чернетка дала б рівно те,
        // від чого seed і рятує: опис, за яким побудова однаково відмовляє.
        Assert.Equal(1, await ScalarAsync("""
            SELECT COUNT(*)
            FROM rpt.ReportVersion AS v
            JOIN rpt.ReportDef     AS d ON d.Id = v.ReportDefId
            WHERE d.Code = N'IEC' AND v.Status = 1
            """));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Seed_заводить_три_описи_форм_230_з_опублікованою_версією_з_восьми_колонок()
    {
        // AN-14: ECR230_A1/B1/B4 — окрема секція `COLL:an14-ecr230`; кількість описів у сіді 1 → 4 (IEC + три).
        Assert.Equal(3, await ScalarAsync(
            "SELECT COUNT(*) FROM rpt.ReportDef WHERE Code IN (N'ECR230_A1', N'ECR230_B1', N'ECR230_B4') AND IsActive = 1 AND IsRegulatory = 1"));
        Assert.Equal(3, await ScalarAsync("""
            SELECT COUNT(*)
            FROM rpt.ReportVersion AS v
            JOIN rpt.ReportDef     AS d ON d.Id = v.ReportDefId
            WHERE d.Code IN (N'ECR230_A1', N'ECR230_B1', N'ECR230_B4') AND v.Status = 1
              AND (SELECT COUNT(*) FROM OPENJSON(v.ColumnsJson)) = 8
            """));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Небезпечні_права_не_потрапляють_у_вбудовані_ролі_автоматично()
    {
        // ⛔ Жодна СКЛАДЕНА вбудована роль не отримує небезпечного права з
        // seed — навіть SystemAdministrator (ФВ-6.12, D-40). Право на
        // симуляцію або відкриття періоду, видане розгортанням, не має автора
        // в аудиті — а саме автор й потрібен, коли потім з'ясовують, звідки
        // взялася можливість.
        //
        // ✎ 2026-09-28: другий поіменний виняток — `Approver` ×
        // `Report.EditDefinition` (рішення людини на Q-153, `D-203`). Він
        // виданий окремим MERGE, а не послабленням фільтра, і тест тримає
        // саме це: крім цієї пари небезпечного в складених ролях НЕМАЄ.
        Assert.Equal(0, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role       AS r ON r.Id   = rp.RoleId AND r.IsBuiltIn = 1
            JOIN sec.Permission AS p ON p.Code = rp.PermissionCode
            WHERE p.IsDangerous = 1
              AND r.Code <> N'{BootstrapAdmin.RoleCode}'
              AND NOT (r.Code = N'Approver' AND p.Code = N'Report.EditDefinition')
            """));

        // ⚠ Виняток рівно один і названий. Він не послаблення правила, а його
        // умова: у щойно розгорнутій системі небезпечних прав не має ніхто,
        // тому без цієї ролі їх ніхто й ніколи не видасть уперше (`A7-17`).
        // Носій — bootstrap-запис: один на систему, з обов'язковою зміною
        // пароля, вимикається появою доменного адміністратора (ФВ-6.18).
        Assert.Equal(2, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'{BootstrapAdmin.RoleCode}'
            """));

        // ⛔ І рівно ДВА — керування користувачами й ролями. Роль первинного
        // налаштування передає систему людям; вона не рахує, не публікує і не
        // дивиться чужими очима.
        Assert.Equal(0, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'{BootstrapAdmin.RoleCode}'
              AND rp.PermissionCode NOT IN (N'Security.ManageUsers', N'Security.ManageRoles')
            """));

        // ⚠ І водночас ролі НЕ порожні: роль без жодного права виглядає
        // як робоча конфігурація і мовчки не працює — це той самий клас
        // дефекту, що й «робота, якої ніхто не робить».
        // ✎ 2026-09-29: дев'ята — `ReportViewer` (рішення людини, секція `SEC:RPT`).
        Assert.Equal(9, await ScalarAsync("""
            SELECT COUNT(DISTINCT rp.RoleId)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            """));

        // Симуляція — найпоказовіший випадок: вона дає чужі очі, а отже, чужі
        // дані, і видаватися має поіменно. Її не має НІХТО, включно з роллю
        // первинного налаштування.
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.RolePermission WHERE PermissionCode = N'Security.Simulate'"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Погоджувач_отримує_Report_EditDefinition_явно_а_шаблони_його_не_роздають()
    {
        // ✎ Q-153, рішення людини 2026-09-28 (`D-203`): «Чи може погоджувач
        // (Approver) редагувати описи державних звітів — ТАК».
        Assert.Equal(1, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'Approver' AND rp.PermissionCode = N'Report.EditDefinition'
            """));

        // ⛔ Право лишається НЕБЕЗПЕЧНИМ. Зняти позначку — найкоротший шлях
        // «видати погоджувачу», але тоді шаблон `%` SystemAdministrator і будь-
        // який майбутній `Report.%` роздали б авторство державної форми мовчки.
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Report.EditDefinition' AND IsDangerous = 1"));

        // ⛔ Жодна інша вбудована роль його не має — зокрема SystemAdministrator
        // (шаблон `%`), DataEntry і Viewer.
        Assert.Equal(0, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE rp.PermissionCode = N'Report.EditDefinition' AND r.Code <> N'Approver'
            """));

        // ⚠ Шаблон `Report.%` погоджувача й далі НЕ бере небезпечного: сусіднє
        // `Report.ViewCampaign` (`Q15-07`) до нього не приїхало.
        Assert.Equal(0, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role       AS r ON r.Id   = rp.RoleId
            JOIN sec.Permission AS p ON p.Code = rp.PermissionCode
            WHERE r.Code = N'Approver' AND p.IsDangerous = 1
              AND p.Code <> N'Report.EditDefinition'
            """));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Розгорнута_база_без_права_погоджувача_отримує_його_повторним_seed()
    {
        // ⚠ Стара база: розгорнута до рішення Q-153, пари немає. MERGE роздач
        // лише додає, і саме це тут і перевіряється — наявна роль отримує
        // відсутнє призначення на наступному старті (`SeedRunner`), без
        // окремого скрипта оновлення.
        await ExecuteAsync("""
            DELETE rp
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'Approver' AND rp.PermissionCode = N'Report.EditDefinition';
            """);

        Assert.Equal(0, await ApproverEditDefinitionAsync());

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.Equal(1, await ApproverEditDefinitionAsync());

        Task<int> ApproverEditDefinitionAsync() => ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'Approver' AND rp.PermissionCode = N'Report.EditDefinition'
            """);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Роль_DataEntry_має_право_бачити_шаблони()
    {
        // ⛔ `A7-54`. Створення документа починається з вибору шаблону і
        // версії, а обидва переліки вимагають `Template.View`. Без нього
        // роль, уся суть якої — заповнювати документи, не могла створити
        // ЖОДНОГО: діалог відкривався і показував порожні списки, бо сервер
        // відповідав 403 на кожен із трьох запитів.
        //
        // ⚠ Порожній список і «немає прав» виглядають однаково — саме тому
        // дефект прожив увесь `A7` і знайшовся лише тоді, коли ендпоінт
        // структури почав перевіряти оголошене контрактом право.
        Assert.Equal(1, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'DataEntry' AND rp.PermissionCode = N'Template.View'
            """));

        // ⚠ І тільки перегляд: право правити шаблони роль не отримує.
        Assert.Equal(0, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'DataEntry'
              AND rp.PermissionCode IN (N'Template.Edit', N'Template.Publish')
            """));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.2")]
    public async Task Кожна_розмірність_має_рівно_одну_базову_одиницю()
    {
        // Первинні розмірності (1..7; 12, 14, 15 — HSE301 F1: StdVolume,
        // Velocity, Area) мають базову одиницю; похідні (8..11, 13, 16..19) —
        // ні, бо складаються з чисельника і знаменника.
        Assert.Equal(10, await ScalarAsync("SELECT COUNT(*) FROM uom.Unit WHERE IsBase = 1"));
        Assert.Equal(10, await ScalarAsync("SELECT COUNT(*) FROM uom.Dimension WHERE BaseUnitId IS NOT NULL"));

        // Двох базових в одній розмірності бути не може — це тримає
        // фільтрований унікальний індекс UX_Unit_BasePerDimension, тобто
        // два різні «нулі» для перетворень фізично неможливі (ФВ-16.2).
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM (SELECT DimensionId FROM uom.Unit WHERE IsBase = 1 " +
            "GROUP BY DimensionId HAVING COUNT(*) > 1) AS d"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.2")]
    public async Task Похідні_одиниці_посилаються_на_чисельник_і_знаменник()
    {
        // Складаються ПОСИЛАННЯМИ, а не розбором рядка «g_per_s» (ФВ-16.2):
        // розбір коду означав би, що перейменування одиниці ламає конверсію.
        // 14 (ФВ-16.2 і HSE301:F1) + mg_per_Sm3 і Sm3_per_day (UNITS:ecr-derived)
        // + Nm3_per_s/_h/_day і mg_per_Nm3 (HSE301:NM3).
        Assert.Equal(20, await ScalarAsync(
            "SELECT COUNT(*) FROM uom.Unit WHERE NumeratorUnitId IS NOT NULL AND DenominatorUnitId IS NOT NULL"));

        // UNITS:ecr-derived: чисельник і знаменник — саме ті, що в коді, а не
        // здогадка з рядка; множник — метричний / 1/86400 без умов приведення.
        Assert.Equal(2, await ScalarAsync(
            "SELECT COUNT(*) FROM uom.Unit u " +
            "JOIN uom.Unit n ON n.Id = u.NumeratorUnitId JOIN uom.Unit d ON d.Id = u.DenominatorUnitId " +
            "JOIN uom.Dimension m ON m.Id = u.DimensionId " +
            "WHERE (u.Code = N'mg_per_Sm3' AND n.Code = N'mg' AND d.Code = N'Sm3' " +
            "       AND m.Code = N'MassPerStdVolume' AND u.FactorToBase = 0.000001) " +
            "   OR (u.Code = N'Sm3_per_day' AND n.Code = N'Sm3' AND d.Code = N'day' " +
            "       AND m.Code = N'StdVolumeFlow' AND u.FactorToBase = 0.000011574074074074)"));

        // Половина посилання — це не похідна одиниця, а зіпсований запис.
        // (У T-SQL булеве не є значенням, тому порівнюємо через CASE.)
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM uom.Unit " +
            "WHERE CASE WHEN NumeratorUnitId IS NULL THEN 1 ELSE 0 END " +
            "   <> CASE WHEN DenominatorUnitId IS NULL THEN 1 ELSE 0 END"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.2")]
    public async Task Множники_базових_одиниць_відповідають_фікстурі()
    {
        // ⚠ FactorToBase наявних одиниць МІНЯТИ ЗАБОРОНЕНО: на них спираються
        // фікстури і тести конверсій. Тест саме про це і стоїть.
        Assert.Equal(1000m, await DecimalAsync("SELECT FactorToBase FROM uom.Unit WHERE Code = N't'"));
        Assert.Equal(0.001m, await DecimalAsync("SELECT FactorToBase FROM uom.Unit WHERE Code = N'g'"));
        Assert.Equal(3600m, await DecimalAsync("SELECT FactorToBase FROM uom.Unit WHERE Code = N'h'"));
        Assert.Equal(31536000m, await DecimalAsync("SELECT FactorToBase FROM uom.Unit WHERE Code = N'year'"));

        // Градус Цельсія — єдина одиниця зі зсувом: перетворення температури
        // не є множенням, і саме тому OffsetToBase взагалі існує.
        Assert.Equal(273.15m, await DecimalAsync("SELECT OffsetToBase FROM uom.Unit WHERE Code = N'degC'"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9c")]
    public async Task Повторний_запуск_після_відсутнього_рядка_піднімає_Revision()
    {
        // ⛔ Мутаційний доказ (D-134): без інкременту в `09-seed.sql` (одразу
        // після MERGE `sys_ecr.UiString`) рядок нижче повертається в таблицю,
        // але Revision і ETag лишаються тими самими — клієнт із чинним
        // кешем НІКОЛИ не побачить нового перекладу. Живий випадок: методичні
        // ключі `methodologies.requiredInputs` та інші (Q-306) у базі є, а на
        // екрані досі `⟦methodologies.requiredInputs⟧`, доки хтось не
        // збереже той самий ключ вручну через `/admin/ui-strings` — а це і є
        // єдиний ІНШИЙ шлях, що інкрементує Revision (`SetUiStringHandler`,
        // `UiStringRevisionTests`).
        const string key = "methodologies.requiredInputs";

        var before = await ScalarAsync("SELECT Revision FROM sys_ecr.UiStringRevision WHERE Id = 1");

        // Вдаємо «ключ, якого щойно змержений PR додав, а стара база не
        // бачила» — видаляємо наявний рядок і даємо seed повернути його.
        await ExecuteAsync($"DELETE FROM sys_ecr.UiString WHERE [Key] = N'{key}' AND LanguageCode = N'en'");

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        var after = await ScalarAsync("SELECT Revision FROM sys_ecr.UiStringRevision WHERE Id = 1");

        Assert.True(after > before, $"Revision не змінився: було {before}, стало {after}.");
        Assert.Equal(1, await ScalarAsync(
            $"SELECT COUNT(*) FROM sys_ecr.UiString WHERE [Key] = N'{key}' AND LanguageCode = N'en'"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9c")]
    public async Task Повторний_запуск_без_нових_рядків_не_піднімає_Revision()
    {
        // ⚠ Протилежний бік того самого доказу: `SeedRunner` виконується на
        // КОЖНОМУ старті застосунку, і безумовний інкремент означав би зайвий
        // round-trip для кожного клієнта на кожному рестарті — навіть коли
        // жодного нового рядка не додалося.
        var before = await ScalarAsync("SELECT Revision FROM sys_ecr.UiStringRevision WHERE Id = 1");
        var rowsBefore = await ScalarAsync("SELECT COUNT(*) FROM sys_ecr.UiString");

        // ⚠ Двічі на ОДНОМУ контексті, тобто на одному з'єднанні: переклади
        // йдуть через тимчасову `#I18N`, яка живе, доки живе з'єднання. Другий
        // прогін без її прибирання впав би на «There is already an object
        // named '#I18N'».
        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        var after = await ScalarAsync("SELECT Revision FROM sys_ecr.UiStringRevision WHERE Id = 1");

        Assert.Equal(before, after);
        Assert.Equal(rowsBefore, await ScalarAsync("SELECT COUNT(*) FROM sys_ecr.UiString"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9")]
    public async Task Сід_заводить_базові_переклади_ru_і_kz_з_областю_англійського_рядка()
    {
        // ✎ Рішення людини 2026-09-29: «переклади робить людина, але базові
        // тексти, які вже є, — зробити зараз, і при встановленні вони мають бути
        // в БД». Мова — не лише рядок у `sys_ecr.Language`: без рядків каталогу
        // вибір ru/kz у перемикачі показував би англійський інтерфейс.
        Assert.Equal("Сохранить", await StringAsync(
            "SELECT Value FROM sys_ecr.UiString WHERE [Key] = N'common.save' AND LanguageCode = N'ru'"));
        Assert.Equal("Сақтау", await StringAsync(
            "SELECT Value FROM sys_ecr.UiString WHERE [Key] = N'common.save' AND LanguageCode = N'kz'"));

        // ⚠ Область перекладу — та сама, що в англійського рядка: публічний
        // `common.save` (0) видно до входу, приватний `profile.language` (1) — ні.
        // Переклад із іншою областю або витік би анонімно, або зник би зі
        // сторінки входу.
        Assert.Equal(0, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sys_ecr.UiString AS t
            JOIN sys_ecr.UiString AS e ON e.[Key] = t.[Key] AND e.LanguageCode = N'en'
            WHERE t.LanguageCode IN (N'ru', N'kz')
              AND t.[Key] IN (N'common.save', N'profile.language', N'login.submit', N'err.ECR-AUTH-0423')
              AND t.Scope <> e.Scope
            """));
        Assert.Equal(8, await ScalarAsync("""
            SELECT COUNT(*) FROM sys_ecr.UiString
            WHERE LanguageCode IN (N'ru', N'kz')
              AND [Key] IN (N'common.save', N'profile.language', N'login.submit', N'err.ECR-AUTH-0423')
            """));

        // ⛔ Переклад без англійського оригіналу сід не заводить (JOIN у блоці `I18N:`).
        Assert.Equal(0, await ScalarAsync("""
            SELECT COUNT(*) FROM sys_ecr.UiString AS t
            WHERE t.LanguageCode IN (N'ru', N'kz')
              AND t.ModifiedByUserId IS NULL
              AND NOT EXISTS (SELECT 1 FROM sys_ecr.UiString AS e WHERE e.[Key] = t.[Key] AND e.LanguageCode = N'en')
            """));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9")]
    public async Task Розгорнута_база_без_перекладу_отримує_його_а_переклад_людини_сід_не_затирає()
    {
        // ⚠ Стара база: розгорнута до 2026-09-29, перекладу `common.save` немає, а
        // `common.cancel` людина вже переклала по-своєму через `/admin/ui-strings`.
        var seeded = await StringAsync(
            "SELECT Value FROM sys_ecr.UiString WHERE [Key] = N'common.cancel' AND LanguageCode = N'ru'");
        await ExecuteAsync("""
            DELETE FROM sys_ecr.UiString WHERE [Key] = N'common.save' AND LanguageCode = N'ru';
            UPDATE sys_ecr.UiString SET Value = N'Отменить действие'
            WHERE [Key] = N'common.cancel' AND LanguageCode = N'ru';
            """);
        var before = await ScalarAsync("SELECT Revision FROM sys_ecr.UiStringRevision WHERE Id = 1");

        try
        {
            await using (var db = CreateContext())
            {
                await new SeedRunner(db).RunAsync(CancellationToken.None);
            }

            Assert.Equal("Сохранить", await StringAsync(
                "SELECT Value FROM sys_ecr.UiString WHERE [Key] = N'common.save' AND LanguageCode = N'ru'"));
            Assert.Equal("Отменить действие", await StringAsync(
                "SELECT Value FROM sys_ecr.UiString WHERE [Key] = N'common.cancel' AND LanguageCode = N'ru'"));

            // ⛔ Без інкременту клієнт із чинним ETag не побачив би нового перекладу.
            var after = await ScalarAsync("SELECT Revision FROM sys_ecr.UiStringRevision WHERE Id = 1");
            Assert.True(after > before, $"Revision не змінився: було {before}, стало {after}.");
        }
        finally
        {
            await ExecuteAsync($"""
                UPDATE sys_ecr.UiString SET Value = N'{seeded!.Replace("'", "''", StringComparison.Ordinal)}'
                WHERE [Key] = N'common.cancel' AND LanguageCode = N'ru';
                """);
        }
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);

    private async Task<string> CountsAsync()
    {
        var parts = new List<string>();
        foreach (var table in (string[])
                 ["sys_ecr.Language", "sec.Permission", "sec.Role", "sec.PasswordPolicy",
                  "uom.Dimension", "uom.Unit", "doc.PeriodPolicy", "sys_ecr.UiString"])
        {
            parts.Add($"{table}={await ScalarAsync($"SELECT COUNT(*) FROM {table}")}");
        }

        return string.Join(", ", parts);
    }

    private async Task<int> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<decimal> DecimalAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (decimal)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<string?> StringAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return await command.ExecuteScalarAsync().ConfigureAwait(false) as string;
    }

    private async Task ExecuteAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
