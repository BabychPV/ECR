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
    private const int ExpectedPermissions = 50; // ✎ 25.11: + Project.DeleteWithData (видалення проєкту з даними, глобальне). Зведення b12: 51 − 4 + 2. D-NEW 25.11: + GasComposition.View/Edit (сторінка «Склад газу», глобальні); P-1124-3: − Report.BuildSnapshot/ViewSnapshot/Export/EditDefinition (зрізи й конструктор звітів прибрано); B80 N5: + Calculation.RecalculateApprovedDependents (безпечне, глобальне; лише SystemAdministrator); B80 N1: + Calculation.ManageSourceBinding (безпечне, глобальне); B75: + Document.RowLock (безпечне, проєктне); D-461: + Document.OverrideFormula (безпечне, проєктне); TC: + Document.Discuss/DiscussModerate (безпечні, чат таблиці); D-285: + Document.Submit; PM-04: + Permit.EditType/PublishType (безпечні; джерело — MERGE sec.Permission у 09-seed.sql)

    private const int ExpectedDangerous = 10; // P-1124-3: − Report.EditDefinition

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

        // ⛔ `Report.ViewCampaign` (`BE-22`) — те саме, і тут це ЄДИНИЙ спосіб
        // виконати рішення людини на `Q15-07`: «окреме право, видається явно».
        // Позначка `IsDangerous = 1` — не оцінка ризику втратити дані, а
        // механізм: фільтр `IsDangerous = 0` у MERGE роздач тримає право поза
        // шаблоном `Report.%` вбудованого `Approver`. Позначене безпечним, воно
        // мовчки відкрило б кожному погоджувачу коди й назви ВСІХ проєктів
        // системи разом із лічильниками їхніх документів.
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Report.ViewCampaign' AND IsDangerous = 1"));

        // ✎ 2026-10-11 (FP:sysadmin-rights, рішення людини): шаблон `Report.%` погоджувача його й далі
        // не бере; єдиний носій — SystemAdministrator, окремим MERGE (секція `FP:sysadmin-rights`).
        Assert.Equal(
            "SystemAdministrator",
            await StringAsync("""
                SELECT STRING_AGG(r.Code, N',') WITHIN GROUP (ORDER BY r.Code)
                FROM sec.RolePermission AS rp
                JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
                WHERE rp.PermissionCode = N'Report.ViewCampaign'
                """));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Право_на_сповіщення_є_в_каталозі_небезпечне_і_видане_лише_системному_адміністратору()
    {
        // `BE-32`: носій вирішує, куди сервер шле повідомлення, і замінює
        // секрети каналів — той самий клас, що `Integration.Manage`.
        Assert.Equal(1, await ScalarAsync("""
            SELECT COUNT(*) FROM sec.Permission
            WHERE Code = N'System.ManageNotifications' AND [Group] = N'System' AND IsDangerous = 1
            """));

        // ⛔ Шаблон `%` системного адміністратора його не видає: фільтр
        // `IsDangerous = 0` стоїть у самому MERGE роздач. ✎ 2026-10-11: поіменний MERGE секції
        // `FP:sysadmin-rights` (рішення людини) видає його ЛИШЕ SystemAdministrator.
        Assert.Equal(
            "SystemAdministrator",
            await StringAsync("""
                SELECT STRING_AGG(r.Code, N',') WITHIN GROUP (ORDER BY r.Code)
                FROM sec.RolePermission AS rp
                JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
                WHERE rp.PermissionCode = N'System.ManageNotifications'
                """));
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
    public async Task Document_Discuss_видано_кожній_ролі_з_View_одноразово_і_повторний_сід_не_повертає_знятого()
    {
        // TC:discuss — кожна ВБУДОВАНА роль, що має Document.View, має й Document.Discuss.
        // ⛔ Лише IsBuiltIn: сід видає право ролям, що існували на момент сіду; ролі, які інші тести колекції створюють у спільній
        // базі пізніше (DR_*, SRV_*, DRA_*: View без Discuss), права не мають і мати не мусять («Роль, створена ПІСЛЯ першого сіду,
        // права не отримує»). Без цього відбору перевірка залежала від того, які класи встигли відпрацювати до неї: у повному
        // прогоні Infrastructure — 26 таких ролей, окремо Persistence — 0.
        Assert.Equal(0, await ScalarAsync("""
            SELECT COUNT(*) FROM sec.RolePermission AS v
            JOIN sec.Role AS vr ON vr.Id = v.RoleId AND vr.IsBuiltIn = 1
            WHERE v.PermissionCode = N'Document.View'
              AND NOT EXISTS (SELECT 1 FROM sec.RolePermission AS d WHERE d.RoleId = v.RoleId AND d.PermissionCode = N'Document.Discuss')
            """));

        // Адміністратор зняв право з ролі «Переглядач»: повторний сід його НЕ повертає (позначка — подія безпеки).
        try
        {
            await ExecuteAsync("""
                DELETE rp FROM sec.RolePermission AS rp JOIN sec.Role AS r ON r.Id = rp.RoleId
                WHERE r.Code = N'Viewer' AND rp.PermissionCode = N'Document.Discuss'
                """);

            await using (var db = CreateContext())
            {
                await new SeedRunner(db).RunAsync(CancellationToken.None);
            }

            Assert.Equal(0, await ScalarAsync("""
                SELECT COUNT(*) FROM sec.RolePermission AS rp JOIN sec.Role AS r ON r.Id = rp.RoleId
                WHERE r.Code = N'Viewer' AND rp.PermissionCode = N'Document.Discuss'
                """));
            Assert.True(await ScalarAsync("""
                SELECT COUNT(*) FROM aud.SecurityEvent
                WHERE EventType = N'RolePermissionsGranted' AND DetailsJson LIKE N'%"source":"seed TC:discuss"%'
                """) > 0);
        }
        finally
        {
            await ExecuteAsync("""
                INSERT sec.RolePermission (RoleId, PermissionCode)
                SELECT r.Id, N'Document.Discuss' FROM sec.Role AS r
                WHERE r.Code = N'Viewer'
                  AND NOT EXISTS (SELECT 1 FROM sec.RolePermission AS d WHERE d.RoleId = r.Id AND d.PermissionCode = N'Document.Discuss')
                """);
        }
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
                   (N'Report.MarkSubmitted',   N'Report',      N'{"en":"Report.MarkSubmitted"}',   0),
                   -- ✎ P-1124-3: права зрізів і конструктора звітів (стан бази до оновлення).
                   (N'Report.BuildSnapshot',   N'Report',      N'{"en":"Report.BuildSnapshot"}',   0),
                   (N'Report.ViewSnapshot',    N'Report',      N'{"en":"Report.ViewSnapshot"}',    0),
                   (N'Report.Export',          N'Report',      N'{"en":"Report.Export"}',          0),
                   (N'Report.EditDefinition',  N'Report',      N'{"en":"Report.EditDefinition"}',  1);
            INSERT sec.RolePermission (RoleId, PermissionCode)
            SELECT r.Id, p.Code FROM sec.Role AS r
            CROSS JOIN (VALUES (N'Calculation.EditScript'), (N'Report.MarkSubmitted'), (N'Report.BuildSnapshot'),
                               (N'Report.ViewSnapshot'), (N'Report.Export'), (N'Report.EditDefinition')) AS p (Code)
            WHERE r.Code IN (N'Approver', N'Viewer');
            """);

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        foreach (var code in (string[])["Calculation.EditScript", "Report.MarkSubmitted", "Report.BuildSnapshot", "Report.ViewSnapshot", "Report.Export", "Report.EditDefinition"])
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
    public async Task Переглядач_звітів_лишається_з_одним_правом_ViewRegulatory()
    {
        // ✎ P-1124-3 (D-NEW, людина 24.11): права зрізів (`Report.ViewSnapshot`, `Report.Export`, `Report.BuildSnapshot`,
        // `Report.EditDefinition`) прибрано з каталогу. Роль `ReportViewer` ЛИШАЄТЬСЯ (на live її мають 2 користувачі) з
        // одним правом — `Report.ViewRegulatory`: головна показує їй «звіти — у SSRS».
        Assert.Equal(
            "Report.ViewRegulatory",
            await StringAsync("""
                SELECT STRING_AGG(rp.PermissionCode, N',') WITHIN GROUP (ORDER BY rp.PermissionCode)
                FROM sec.RolePermission AS rp
                JOIN sec.Role AS r ON r.Id = rp.RoleId
                WHERE r.Code = N'ReportViewer' AND r.IsBuiltIn = 1
                """));

        // Повторний сід на базі без пар ролі заводить рівно одну пару (MERGE лише додає).
        await ExecuteAsync("""
            DELETE rp FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId WHERE r.Code = N'ReportViewer';
            """);

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.Equal(1, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'ReportViewer'
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
        // ✎ 2026-10-11 (`FP:sysadmin-rights`, рішення людини за проходом «від першого обличчя»):
        // третій поіменний виняток — SystemAdministrator отримує ВІСІМ адмін-прав керування
        // (`SysAdminDangerous` нижче), окремим MERGE; ✎ 2026-10-12 — ще `Period.Reopen` і
        // `Document.ChangeKey` (десять).
        Assert.Equal(0, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role       AS r ON r.Id   = rp.RoleId AND r.IsBuiltIn = 1
            JOIN sec.Permission AS p ON p.Code = rp.PermissionCode
            WHERE p.IsDangerous = 1
              AND r.Code <> N'{BootstrapAdmin.RoleCode}'
              AND NOT (r.Code = N'SystemAdministrator' AND p.Code IN ({SysAdminDangerousSql}))
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
        // дані, і видаватися має поіменно. Її не має ніхто, крім SystemAdministrator (рішення людини
        // 2026-10-11, «Security.*»); роль первинного налаштування її не має й далі.
        Assert.Equal(
            "SystemAdministrator",
            await StringAsync("""
                SELECT STRING_AGG(r.Code, N',') WITHIN GROUP (ORDER BY r.Code)
                FROM sec.RolePermission AS rp
                JOIN sec.Role AS r ON r.Id = rp.RoleId
                WHERE rp.PermissionCode = N'Security.Simulate'
                """));
    }

    /// <summary>
    /// S-2 (аудит 2026-11, рішення PO 11.11, <c>D-496</c>): скасувати чи повторити ЧУЖУ або системну фонову задачу
    /// може лише носій <c>System.RunJob</c>. Серед вбудованих ролей це право має рівно одна —
    /// <c>SystemAdministrator</c>: саме тому «або роль адміністратора» з рішення PO окремої перевірки ролі в коді
    /// не потребує. Якщо сід видасть право ще комусь (наприклад, «Аудитору»), звуження S-2 мовчки зникне.
    /// </summary>
    /// <remarks>Мутація: додати <c>System.RunJob</c> будь-якій іншій вбудованій ролі в сіді — червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S-2")]
    public async Task System_RunJob_серед_вбудованих_ролей_має_лише_SystemAdministrator()
    {
        Assert.Equal(
            "SystemAdministrator",
            await StringAsync("""
                SELECT STRING_AGG(r.Code, N',') WITHIN GROUP (ORDER BY r.Code)
                FROM sec.RolePermission AS rp
                JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
                WHERE rp.PermissionCode = N'System.RunJob'
                """));
    }

    /// <summary>
    /// Адмін-права керування, видані SystemAdministrator секцією <c>FP:sysadmin-rights</c> (рішення
    /// людини 2026-10-11). Перелік дзеркалить MERGE сіду; сторож нижче звіряє його з каталогом.
    /// </summary>
    private static readonly string[] SysAdminDangerous =
    [
        "Security.ManageUsers", "Security.ManageRoles", "Security.Simulate",
        "Integration.Manage", "System.ManageNotifications", "System.RunJob",
        "Report.ViewCampaign", "Calculation.Publish",
        // ✎ 2026-10-12 (рішення людини, квіз 12.10, №1): ще два, тією самою секцією.
        "Period.Reopen", "Document.ChangeKey",
    ];

    /// <summary>Небезпечні права, які SystemAdministrator НЕ має (на рішення людини, див. секцію сіду).</summary>
    /// <remarks>✎ P-1124-3: єдине утримане (`Report.EditDefinition`) прибрано з каталогу разом із конструктором звітів.</remarks>
    private static readonly string[] SysAdminWithheld = [];

    /// <summary>SQL-перелік утримуваних; порожній — <c>N''</c> (жодне право не має порожнього коду), а НЕ <c>NULL</c>: <c>NOT IN (NULL)</c> відкинув би всі рядки й зробив би перевірку порожньою.</summary>
    private static string SysAdminWithheldSql
        => SysAdminWithheld.Length == 0 ? "N''" : string.Join(", ", SysAdminWithheld.Select(code => $"N'{code}'"));

    private static string SysAdminDangerousSql
        => string.Join(", ", SysAdminDangerous.Select(code => $"N'{code}'"));

    /// <remarks>
    /// ⛔ Видача адмін-прав роллю — подія безпеки, а не мовчазна зміна даних: рівно ОДНА подія
    /// <c>RolePermissionsGranted</c> з роллю, кількістю й кодами прав, системним автором і без
    /// користувачів; повторний старт нічого не додає. Мутація: прибрати <c>NOT EXISTS</c> із
    /// секції <c>FP:sysadmin-rights</c> — друга частина (повтор) червоніє; прибрати INSERT — перша.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Видача_адмін_прав_системному_адміністратору_пишеться_однією_подією_безпеки_без_повтору()
    {
        const string EventFilter =
            "EventType = N'RolePermissionsGranted' AND DetailsJson LIKE N'%seed FP:sysadmin-rights%'";

        var eventsBefore = await ScalarAsync($"SELECT COUNT(*) FROM aud.SecurityEvent WHERE {EventFilter}");

        await ExecuteAsync($"""
            DELETE rp
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            WHERE r.Code = N'SystemAdministrator'
              AND rp.PermissionCode IN ({SysAdminDangerousSql})
            """);

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.Equal(
            eventsBefore + 1,
            await ScalarAsync($"SELECT COUNT(*) FROM aud.SecurityEvent WHERE {EventFilter}"));

        // Остання подія: роль-ціль, системний автор, без користувача-цілі.
        Assert.Equal(1, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM aud.SecurityEvent
            WHERE Id = (SELECT MAX(Id) FROM aud.SecurityEvent WHERE {EventFilter})
              AND ChangedByUserId = 0 AND TargetUserId IS NULL
              AND TargetRoleId = (SELECT Id FROM sec.Role WHERE Code = N'SystemAdministrator' AND IsBuiltIn = 1)
            """));

        var details = await StringAsync(
            $"SELECT TOP (1) DetailsJson FROM aud.SecurityEvent WHERE {EventFilter} ORDER BY Id DESC");
        using var json = System.Text.Json.JsonDocument.Parse(details!);
        Assert.Equal("SystemAdministrator", json.RootElement.GetProperty("role").GetString());
        Assert.Equal(SysAdminDangerous.Length, json.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(
            SysAdminDangerous.Order(StringComparer.Ordinal),
            json.RootElement.GetProperty("permissions").EnumerateArray()
                .Select(p => p.GetString()!).Order(StringComparer.Ordinal));

        // Повторний старт: пар не додано — нової події немає.
        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.Equal(
            eventsBefore + 1,
            await ScalarAsync($"SELECT COUNT(*) FROM aud.SecurityEvent WHERE {EventFilter}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Системний_адміністратор_має_кожне_адмін_право_каталогу_крім_названих_виключень()
    {
        // ⛔ FP:sysadmin-rights (прохід «від першого обличчя» 2026-10-11, P2; рішення людини):
        // вбудована роль з назвою «системний адміністратор» не бачила Security, Mapping, Pipeline,
        // Notifications, Campaign і не публікувала методики — «усе, крім небезпечного» віддавало
        // їй безпечне, а адміністрування якраз небезпечне.
        //
        // Сторож-храповик: КОЖНЕ право каталогу є в ролі, крім названого переліку утримуваних.
        // Нове право (особливо небезпечне), яке забули віддати чи свідомо втримати, червоніє тут —
        // рішення про нього приймається явно, а не випадає з шаблону `%`.
        Assert.Equal(0, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.Permission AS p
            WHERE p.Code NOT IN ({SysAdminWithheldSql})
              AND NOT EXISTS (
                SELECT 1
                FROM sec.RolePermission AS rp
                JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
                WHERE r.Code = N'SystemAdministrator' AND rp.PermissionCode = p.Code)
            """));

        // ⚠ Утримані — справді небезпечні й справді не видані (інакше список утримуваних брехав би).
        Assert.Equal(0, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            WHERE r.Code = N'SystemAdministrator'
              AND rp.PermissionCode IN ({SysAdminWithheldSql})
            """));

        // Перелік виданих небезпечних — рівно названі десять: нічого зайвого.
        Assert.Equal(SysAdminDangerous.Length, await ScalarAsync("""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role       AS r ON r.Id   = rp.RoleId AND r.IsBuiltIn = 1
            JOIN sec.Permission AS p ON p.Code = rp.PermissionCode
            WHERE r.Code = N'SystemAdministrator' AND p.IsDangerous = 1
            """));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Розгорнута_база_без_адмін_прав_системного_адміністратора_отримує_їх_повторним_seed()
    {
        // ⚠ Стара база: розгорнута до рішення, пар немає. MERGE лише додає — наявна роль отримує
        // відсутні пари на наступному старті (`SeedRunner`), без окремого скрипта оновлення.
        await ExecuteAsync($"""
            DELETE rp
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            WHERE r.Code = N'SystemAdministrator'
              AND rp.PermissionCode IN ({SysAdminDangerousSql})
            """);

        Assert.Equal(0, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            WHERE r.Code = N'SystemAdministrator' AND rp.PermissionCode IN ({SysAdminDangerousSql})
            """));

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.Equal(SysAdminDangerous.Length, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            WHERE r.Code = N'SystemAdministrator' AND rp.PermissionCode IN ({SysAdminDangerousSql})
            """));
    }

    /// <remarks>
    /// ✎ 2026-10-12 (квіз 12.10, №1). База після RC20 має вісім прав, без `Period.Reopen` і
    /// `Document.ChangeKey`: сід додає саме ці дві пари, пише про них одну подію й нічого більше;
    /// повтор — нуль змін. Мутація: прибрати
    /// `Period.Reopen`/`Document.ChangeKey` зі списку MERGE — перша частина червоніє; послабити
    /// `NOT EXISTS` події — червоніє повтор.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Системний_адміністратор_отримує_Period_Reopen_і_Document_ChangeKey()
    {
        const string EventFilter =
            "EventType = N'RolePermissionsGranted' AND DetailsJson LIKE N'%seed FP:sysadmin-rights%'";
        const string Added = "N'Period.Reopen', N'Document.ChangeKey'";

        // Стан бази RC20: вісім прав є, двох нових немає.
        await ExecuteAsync($"""
            DELETE rp
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            WHERE r.Code = N'SystemAdministrator' AND rp.PermissionCode IN ({Added})
            """);

        var eventsBefore = await ScalarAsync($"SELECT COUNT(*) FROM aud.SecurityEvent WHERE {EventFilter}");
        var pairsBefore = await ScalarAsync($"""
            SELECT COUNT(*) FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            WHERE r.Code = N'SystemAdministrator'
            """);

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.Equal(2, await ScalarAsync($"""
            SELECT COUNT(*) FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            WHERE r.Code = N'SystemAdministrator' AND rp.PermissionCode IN ({Added})
            """));
        Assert.Equal(pairsBefore + 2, await ScalarAsync("""
            SELECT COUNT(*) FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            WHERE r.Code = N'SystemAdministrator'
            """));

        // Одна подія, і в ній рівно дві нові пари.
        Assert.Equal(
            eventsBefore + 1,
            await ScalarAsync($"SELECT COUNT(*) FROM aud.SecurityEvent WHERE {EventFilter}"));
        var details = await StringAsync(
            $"SELECT TOP (1) DetailsJson FROM aud.SecurityEvent WHERE {EventFilter} ORDER BY Id DESC");
        using var json = System.Text.Json.JsonDocument.Parse(details!);
        Assert.Equal(2, json.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(
            ["Document.ChangeKey", "Period.Reopen"],
            json.RootElement.GetProperty("permissions").EnumerateArray()
                .Select(p => p.GetString()!).Order(StringComparer.Ordinal));

        // Ідемпотентність: повторний сід — ні пар, ні подій.
        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.Equal(pairsBefore + 2, await ScalarAsync("""
            SELECT COUNT(*) FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            WHERE r.Code = N'SystemAdministrator'
            """));
        Assert.Equal(
            eventsBefore + 1,
            await ScalarAsync($"SELECT COUNT(*) FROM aud.SecurityEvent WHERE {EventFilter}"));
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
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Право_Document_Submit_у_каталозі_і_видане_DataEntry_та_адміністратору()
    {
        // D-285: Write сам по собі подання не дає; DataEntry (заповнює й подає
        // свої форми) отримує право, SystemAdministrator — через шаблон `%`.
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Document.Submit' AND IsDangerous = 0"));

        Assert.Equal(1, await ScalarAsync(RolePair("DataEntry")));
        Assert.Equal(1, await ScalarAsync(RolePair("SystemAdministrator")));

        // ⚠ Не для ролей, що лише читають чи погоджують (Approver подає рівнем
        // гранта Submit, не правом).
        Assert.Equal(0, await ScalarAsync(RolePair("Viewer")));
        Assert.Equal(0, await ScalarAsync(RolePair("Auditor")));
        Assert.Equal(0, await ScalarAsync(RolePair("Approver")));

        static string RolePair(string role) => $"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'{role}' AND rp.PermissionCode = N'Document.Submit'
            """;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Розгорнута_база_без_права_Submit_у_DataEntry_отримує_його_повторним_seed()
    {
        // ⚠ Стара база: DataEntry розгорнуто до D-285, пари немає. MERGE лише
        // додає — наявна роль отримує право на наступному старті.
        const string Pair = """
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'DataEntry' AND rp.PermissionCode = N'Document.Submit'
            """;
        await ExecuteAsync("""
            DELETE rp
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'DataEntry' AND rp.PermissionCode = N'Document.Submit';
            """);
        Assert.Equal(0, await ScalarAsync(Pair));

        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        Assert.Equal(1, await ScalarAsync(Pair));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.2")]
    public async Task Кожна_розмірність_має_рівно_одну_базову_одиницю()
    {
        // Первинні розмірності (1..7; 12, 14, 15 — HSE301 F1: StdVolume,
        // Velocity, Area; 21, 23 — LAND:units: Power, Pressure) мають базову одиницю; похідні
        // (8..11, 13, 16..20, 22) — ні, бо складаються з чисельника і знаменника.
        Assert.Equal(12, await ScalarAsync("SELECT COUNT(*) FROM uom.Unit WHERE IsBase = 1"));
        Assert.Equal(12, await ScalarAsync("SELECT COUNT(*) FROM uom.Dimension WHERE BaseUnitId IS NOT NULL"));

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
        // + Nm3_per_s/_h/_day і mg_per_Nm3 (HSE301:NM3)
        // + rpm, m3_per_h, l_per_h, kg_per_h, t_per_h, mg_per_l (LAND:units).
        Assert.Equal(26, await ScalarAsync(
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
