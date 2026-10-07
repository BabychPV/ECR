// tests/Ecr.Api.Tests/Security/HiddenSheetRouteMatrixTests.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ecr.Application.Ports;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Матриця «роль × маршрут» для прихованого аркуша: для читача, якому аркуш B недоступний,
/// кожен маршрут читання відповідає ТАК САМО, як коли аркуша B у документі немає зовсім.
/// </summary>
/// <remarks>
/// ⛔ Метод — «до/після», а не перелік заборонених слів. Шаблон від початку має аркуш B (таблиця,
/// колонка, рядок, прив'язка методології), а роль — своє обмеження (звуження аркушем A або Deny на B),
/// тож межі читання в обох знімках однакові. Знімок R0: документ лише з A. Потім у документ додається
/// B і вся його «активність»: склад, стан Rejected, зауваження валідації, пізня правка в журналі,
/// <c>ModifiedAt/ModifiedBy</c> документа іншим автором, результат методології для рядка B (той самий
/// вихід, що прив'язаний і до видимої таблиці A). Знімок R1. Для звуженого читача R0 == R1 побайтно
/// (без <c>traceId</c>), і в R1 немає жодної назви/коду/автора B. Для ролі без обмежень ("none") R0 != R1
/// на КОЖНОМУ маршруті: це доказ, що сценарій справді показує B, тобто зелений сторож чогось вартий.
///
/// Що ловить кожен маршрут (рев'ю `security-review-hidden-sheet-p1*-2026-10-06.md`):
/// перелік/картка — R-7 (<c>hasLateEdits</c>, <c>modifiedAt</c>, <c>modifiedByDisplayName</c>), R-1, лічильники;
/// фільтр <c>hasLateEdits</c> — R-7; фільтр <c>state</c> — R-1; зведення — R-1/Н-4; <c>/validation</c> — R-10/S0
/// (заглушка «є приховані зауваження» дозволена рішенням <c>HiddenValidationIssues</c> і вирізається перед
/// порівнянням); <c>/tables</c>, <c>/tables/status</c> — S0; <c>calculation-results</c> — Н-1 (рядки) і Н-2
/// (свіжість), окремими варіантами; <c>audit/cells</c> — R-11; <c>campaign/summary</c> — R-8;
/// <c>document-template</c> — R-6 (окремий тест: метадані шаблону, порівнювати «до/після» нема з чим).
///
/// ⚠ Писалося в хмарі без .NET SDK: не компілювалось і не виконувалось. Очікувано червоні, доки відкриті
/// відповідні пункти: R-7, Н-1, Н-2, R-11, R-8 (рішення Q15-07), R-6 (продуктове рішення).
/// </remarks>
[Collection("SqlServer")]
public sealed class HiddenSheetRouteMatrixTests(SqlServerFixture sql)
{
    private const string Password = "Hidden-Sheet-Matrix-R7!";

    /// <summary>Маршрут → варіант «активності B», з яким він знімається.</summary>
    private static readonly (string Route, HiddenActivity Variant)[] Routes =
    [
        ("list-period", HiddenActivity.Full),
        ("list-noperiod", HiddenActivity.Full),
        ("list-state-rejected", HiddenActivity.Full),
        ("list-late-true", HiddenActivity.Full),
        ("list-late-false", HiddenActivity.Full),
        ("card-period", HiddenActivity.Full),
        ("card-noperiod", HiddenActivity.Full),
        ("summary", HiddenActivity.Full),
        ("validation", HiddenActivity.Full),
        ("tables", HiddenActivity.Full),
        ("tables-status", HiddenActivity.Full),
        ("calc-rows", HiddenActivity.RowsOnly),
        ("calc-fresh", HiddenActivity.FreshOnly),
        ("audit-cells", HiddenActivity.Full),
        ("campaign", HiddenActivity.Full),
    ];

    /// <summary>
    /// Знімки на (спосіб приховування, варіант) — один раз на клас: кожен знімок — це база, два
    /// підняті застосунки й вхід, а маршрутів п'ятнадцять.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<Captured>>> Cache = new(StringComparer.Ordinal);

    public static TheoryData<string, string> NarrowedCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var how in new[] { "scope", "deny" })
        {
            foreach (var (route, _) in Routes)
            {
                // R-8 (Q15-07): лічильники огляду кампанії для читача з Deny на аркуш: випадок додається після зведення sec-campaign-progress (його тестами покрито).
                if (how == "deny" && route == "campaign")
                {
                    continue;
                }

                data.Add(how, route);
            }
        }

        return data;
    }

    public static TheoryData<string> ControlCases()
    {
        var data = new TheoryData<string>();
        foreach (var (route, _) in Routes)
        {
            data.Add(route);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NarrowedCases))]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public Task Маршрут_для_звуженого_читача_не_змінюється_від_появи_схованого_аркуша(string how, string route)
        => AssertRouteUnchangedAsync(how, route);

    private async Task AssertRouteUnchangedAsync(string how, string route)
    {
        var captured = await CapturedAsync(how, VariantOf(route)).ConfigureAwait(true);
        var before = captured.Before[route];
        var after = captured.After[route];

        // Без витоку назв: це видно і з «до/після», але повідомлення так одразу каже, ЩО витекло.
        foreach (var token in captured.Scenario.HiddenTokens)
        {
            Assert.False(
                after.Body.Contains(token, StringComparison.Ordinal),
                $"{how}/{route}: у відповіді є «{token}» схованого аркуша\n{after.Body}");
        }

        var normalizedBefore = Normalize(route, before, captured.Scenario);
        var normalizedAfter = Normalize(route, after, captured.Scenario);
        Assert.True(
            normalizedBefore == normalizedAfter,
            $"{how}/{route}: відповідь залежить від схованого аркуша B\n--- без B ---\n{normalizedBefore}\n--- з B ---\n{normalizedAfter}");
    }

    [Theory]
    [MemberData(nameof(ControlCases))]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Маршрут_для_ролі_без_обмежень_показує_аркуш_B_контроль_сценарію(string route)
    {
        // ⛔ Контроль: якщо сценарій «активності B» нічого не змінив у відповіді повної ролі, то й
        // рівність для звуженої нічого не доводить. Цей тест червоніє саме тоді.
        var captured = await CapturedAsync("none", VariantOf(route)).ConfigureAwait(true);
        var before = captured.Before[route];
        var after = captured.After[route];

        Assert.True(before.Status == HttpStatusCode.OK, $"none/{route}: {before.Status}\n{before.Body}");
        Assert.True(after.Status == HttpStatusCode.OK, $"none/{route}: {after.Status}\n{after.Body}");
        Assert.NotEqual(Normalize(route, before, captured.Scenario), Normalize(route, after, captured.Scenario));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Шаблон_документа_для_ролі_звуженої_аркушем_відмова_без_назви_схованого_аркуша()
    {
        // Роль, звужена аркушами, не має Document.Create у проєкті (створення документа — на весь проєкт),
        // тож майстер створення їй не віддається: 403, і тіло відмови не називає схований аркуш.
        var s = await ArrangeAsync("scope").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var reply = await GetAsync(client, $"/api/v1/projects/{s.ProjectId}/document-template").ConfigureAwait(true);

        Assert.True(reply.Status == HttpStatusCode.Forbidden, $"scope: {reply.Status}\n{reply.Body}");
        Assert.DoesNotContain(s.HiddenCode, reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(s.HiddenName, reply.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Шаблон_документа_для_читача_з_Deny_на_аркуш_лишає_конфігурацію_шаблону_свідомо()
    {
        // Свідомо (рішення координатора RC6 06–07.10, R-6 варіант 1): код/назва аркуша — конфігурація шаблону;
        // обмеження RC6, див. реліз-нотатки; змінюється лише разом із рішенням про групи/обов'язкові аркуші.
        // Фільтр зламав би обов'язкові аркуші й SheetGroupRule у майстрі створення (POST /documents з неповним
        // складом дав би 422, що саме розкриває аркуш). Дані/стани/лічильники схованого аркуша тут не віддаються.
        var s = await ArrangeAsync("deny").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var reply = await GetAsync(client, $"/api/v1/projects/{s.ProjectId}/document-template").ConfigureAwait(true);

        Assert.True(reply.Status == HttpStatusCode.OK, $"deny: {reply.Status}\n{reply.Body}");
        Assert.Contains(s.VisibleCode, reply.Body, StringComparison.Ordinal);
        Assert.Contains(s.HiddenCode, reply.Body, StringComparison.Ordinal);
        Assert.Contains(s.HiddenName, reply.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Шаблон_документа_для_ролі_без_обмежень_називає_обидва_аркуші()
    {
        var s = await ArrangeAsync("none").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var reply = await GetAsync(client, $"/api/v1/projects/{s.ProjectId}/document-template").ConfigureAwait(true);

        Assert.True(reply.Status == HttpStatusCode.OK, $"none: {reply.Status}\n{reply.Body}");
        Assert.Contains(s.VisibleCode, reply.Body, StringComparison.Ordinal);
        Assert.Contains(s.HiddenCode, reply.Body, StringComparison.Ordinal);
    }

    // ── знімки ───────────────────────────────────────────────────────────────────────────

    /// <summary>Яку «активність B» додати між знімками.</summary>
    /// <remarks>Бітові прапорці без <c>[Flags]</c>: перевірка — <see cref="Enum.HasFlag"/>.</remarks>
    private enum HiddenActivity
    {
        /// <summary>Нічого (обов'язкове нульове значення).</summary>
        None = 0,

        /// <summary>B у складі документа — є в кожному варіанті.</summary>
        Membership = 1,

        /// <summary>Стан B = Rejected (автор — редактор B).</summary>
        State = 2,

        /// <summary>Сім помилок валідації в таблиці B у збереженому підсумку.</summary>
        Validation = 4,

        /// <summary>Пізня правка комірки B у <c>aud.CellChange</c> після прогону методології.</summary>
        LateEdit = 8,

        /// <summary><c>ModifiedAt/ModifiedByUserId</c> документа — редактор B.</summary>
        Modified = 16,

        /// <summary>Результат методології для рядка B (вихід, прив'язаний і до A, і до B).</summary>
        CalcRows = 32,

        Full = Membership | State | Validation | LateEdit | Modified | CalcRows,

        /// <summary>Н-1 окремо від Н-2: лише рядки результату.</summary>
        RowsOnly = Membership | CalcRows,

        /// <summary>Н-2 окремо від Н-1: лише зміна входу B після прогону.</summary>
        FreshOnly = Membership | LateEdit,
    }

    private sealed record Reply(HttpStatusCode Status, string Body);

    private sealed record Captured(
        Scenario Scenario, IReadOnlyDictionary<string, Reply> Before, IReadOnlyDictionary<string, Reply> After);

    private sealed record Scenario(
        long DocumentId, int ProjectId, int PeriodKey, string UserName, string VisibleCode,
        int HiddenSheetId, string HiddenCode, string HiddenName, int HiddenTableId, int HiddenColumnId,
        string HiddenRowKey, int EditorId, string EditorName, int VersionId, int UnitId, long RunId,
        DateTime RunStartedAt, DateTime AuditFrom, DateTime AuditTo, IReadOnlyList<string> HiddenTokens);

    private static HiddenActivity VariantOf(string route)
        => Routes.Single(r => string.Equals(r.Route, route, StringComparison.Ordinal)).Variant;

    private Task<Captured> CapturedAsync(string how, HiddenActivity variant)
        => Cache.GetOrAdd(
                $"{how}|{(int)variant}",
                _ => new Lazy<Task<Captured>>(() => CaptureAsync(how, variant)))
            .Value;

    private async Task<Captured> CaptureAsync(string how, HiddenActivity variant)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(false);
        var before = await CaptureAllAsync(s).ConfigureAwait(false);
        await RevealHiddenActivityAsync(s, variant).ConfigureAwait(false);
        var after = await CaptureAllAsync(s).ConfigureAwait(false);

        return new Captured(s, before, after);
    }

    /// <summary>Усі маршрути одним застосунком і одним сеансом; новий застосунок — без кешів попереднього.</summary>
    private async Task<IReadOnlyDictionary<string, Reply>> CaptureAllAsync(Scenario s)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(false);

        var replies = new Dictionary<string, Reply>(StringComparer.Ordinal);
        foreach (var (route, _) in Routes)
        {
            replies[route] = await GetAsync(client, Url(route, s)).ConfigureAwait(false);
        }

        return replies;
    }

    private static string Url(string route, Scenario s)
    {
        var id = s.DocumentId.ToString(CultureInfo.InvariantCulture);
        var period = s.PeriodKey.ToString(CultureInfo.InvariantCulture);
        var project = s.ProjectId.ToString(CultureInfo.InvariantCulture);
        var list = $"/api/v1/documents?limit=200&projectId={project}";

        return route switch
        {
            "list-period" => $"{list}&periodKey={period}",
            "list-noperiod" => list,
            "list-state-rejected" => $"{list}&periodKey={period}&state=Rejected",
            "list-late-true" => $"{list}&periodKey={period}&hasLateEdits=true",
            "list-late-false" => $"{list}&periodKey={period}&hasLateEdits=false",
            "card-period" => $"/api/v1/documents/{id}?periodKey={period}",
            "card-noperiod" => $"/api/v1/documents/{id}",
            "summary" => $"/api/v1/documents/summary?periodKey={period}&projectId={project}",
            "validation" => $"/api/v1/documents/{id}/validation?periodKey={period}",
            "tables" => $"/api/v1/documents/{id}/tables?periodKey={period}",
            "tables-status" => $"/api/v1/documents/{id}/tables/status?periodKey={period}",
            "calc-rows" or "calc-fresh" => $"/api/v1/documents/{id}/calculation-results?periodKey={period}",
            "audit-cells" => "/api/v1/audit/cells"
                             + $"?from={Uri.EscapeDataString(s.AuditFrom.ToString("O", CultureInfo.InvariantCulture))}"
                             + $"&to={Uri.EscapeDataString(s.AuditTo.ToString("O", CultureInfo.InvariantCulture))}"
                             + $"&documentId={id}&limit=50",
            "campaign" => $"/api/v1/campaign/summary?periodKey={period}",
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };
    }

    /// <summary>
    /// Відповідь без шуму запиту; огляд кампанії — лише рядок свого проєкту (інші проєкти в спільній
    /// базі змінюють тести поруч); валідація — без заглушки «є приховані зауваження» (дозволений сигнал).
    /// </summary>
    private static string Normalize(string route, Reply reply, Scenario s)
    {
        if (reply.Status != HttpStatusCode.OK)
        {
            var problem = Regex.Replace(reply.Body, "\"(traceId|correlationId|requestId)\"\\s*:\\s*\"[^\"]*\",?", string.Empty);
            return $"{(int)reply.Status} {problem}";
        }

        var body = reply.Body;

        if (string.Equals(route, "campaign", StringComparison.Ordinal))
        {
            var row = JsonNode.Parse(body)?["projects"]?.AsArray()
                .FirstOrDefault(p => p?["projectId"]?.GetValue<int>() == s.ProjectId);
            return $"200 {row?.ToJsonString() ?? "<проєкту немає в огляді>"}";
        }

        if (string.Equals(route, "validation", StringComparison.Ordinal))
        {
            var root = JsonNode.Parse(body)!.AsObject();
            if (root["messages"] is JsonArray messages)
            {
                foreach (var placeholder in messages
                             .Where(m => string.Equals(m?["ruleCode"]?.GetValue<string>(), HiddenValidationIssues.Placeholder.RuleCode, StringComparison.Ordinal)
                                         && m?["tableDefId"]?.GetValue<int>() == 0)
                             .ToList())
                {
                    messages.Remove(placeholder);
                }
            }

            return $"200 {root.ToJsonString()}";
        }

        return $"200 {body}";
    }

    private static async Task<Reply> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    // ── сценарій ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R0: шаблон з A і B (B у шаблоні, але НЕ в документі), роль із обмеженням, A з даними:
    /// прогін методології з результатом для рядка A, правка A до прогону, підсумок валідації по A.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(string how)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var hiddenCode = $"HIDMX{tag}";
        var hiddenName = $"HiddenMatrixSheet{tag}";
        var hiddenTableCode = $"MXTB{tag}";
        var hiddenTableName = $"HiddenMatrixTable{tag}";
        var hiddenColumnCode = $"MXCB{tag}";
        var hiddenRowKey = $"MXRB{tag}";
        var editorName = $"HiddenEditor{tag}";
        var userName = $"mx{Guid.NewGuid():N}"[..20];
        var now = DateTime.UtcNow;
        var runStartedAt = now.AddHours(-2);

        await using var db = Context();

        var visibleRowId = b.RowIds[0];
        var visibleRowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == visibleRowId)
            .Select(r => r.RowKeyValue)
            .SingleAsync()
            .ConfigureAwait(false);

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));

        // Шаблон: аркуш B з таблицею, колонкою й рядком — до першого знімка (знімок метаданих однаковий).
        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create(hiddenCode), Name(hiddenName), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableB = new TableDef(
            sheetB.Id, EcrCode.Create(hiddenTableCode), Name(hiddenTableName), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var columnB = new ColumnDef(tableB.Id, EcrCode.Create(hiddenColumnCode), Name($"HiddenMatrixColumn{tag}"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(columnB);
        db.RowDefs.Add(new RowDef(tableB.Id, RowKey.Create(hiddenRowKey), 1, Name($"HiddenMatrixRow{tag}"), RowKind.Item));

        // Методологія з ОДНИМ виходом, прив'язаним і до видимої колонки A, і до колонки B (Н-1).
        var methodology = new Methodology(EcrCode.Create($"MXM{tag}"), Name("matrix methodology"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.CalculationBindings.Add(new CalculationBinding(b.TableDefId, b.ColumnDefIds[1], methodology.Id, "tons", "{}"));
        db.CalculationBindings.Add(new CalculationBinding(tableB.Id, columnB.Id, methodology.Id, "tons", "{}"));
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, runStartedAt.AddDays(-1));
        db.MethodologyVersions.Add(version);

        // Користувачі: читач і окремий редактор B (його ім'я не має з'явитися ніде).
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var editorUserName = $"ed{Guid.NewGuid():N}"[..20];
        var editor = new User(editorUserName, editorName, AuthProvider.Local);
        editor.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(editor);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Matrix role"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in new[] { "Document.View", "Document.Create", "Calculation.View", "Report.ViewCampaign", "Security.ViewAudit" })
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Manage));
        if (how == "deny")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, sheetB.Id, GrantLevel.Read, isDeny: true));
        }

        var assignment = new RoleAssignment(role.Id, user.Id, principalSid: null);
        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (how == "scope")
        {
            var scope = RoleAssignmentScope.Create([b.ProjectId], [b.SheetCode], null, null).ToJson();
            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {assignment.Id}")
                .ConfigureAwait(false);
        }

        // Підсумок валідації по A: 1 помилка + 1 попередження. У R1 той самий рядок доповнюється B.
        db.ValidationResults.Add(new ValidationResult(
            b.DocumentId, b.PeriodKey.Value, runStartedAt, 1, 1, 0, JsonSerializer.Serialize(VisibleMessages(b.TableDefId))));

        // Прогін методології (поточний) з результатом для рядка A.
        var run = new CalculationRun(b.ProjectId, b.PeriodKey.Value, null, runStartedAt, b.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(false);
        run.Complete("Succeeded", runStartedAt.AddMinutes(1), null, null);
        run.MakeCurrent();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        await new CalculationResultStore(db, new TestClock(runStartedAt.AddMinutes(1))).WriteResultsAsync(
                run.Id,
                [new CalculationOutput(b.DocumentId, visibleRowKey, [new CalculationOutputValue(version.Id, null, "tons", 11m, unitId)], [])],
                CancellationToken.None)
            .ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Звичайна (не пізня) правка A ДО прогону: журнал документа не порожній, свіжість не зачеплена.
        await WriteChangeAsync(
                b.DocumentId, b.PeriodKey.Value, visibleRowKey, b.ColumnDefIds[1], user.Id, late: false, runStartedAt.AddHours(-1))
            .ConfigureAwait(false);

        var tokens = new[] { hiddenCode, hiddenName, hiddenTableCode, hiddenTableName, hiddenColumnCode, hiddenRowKey, editorName, "SECRET" };

        return new Scenario(
            b.DocumentId, b.ProjectId, b.PeriodKey.Value, userName, b.SheetCode,
            sheetB.Id, hiddenCode, hiddenName, tableB.Id, columnB.Id, hiddenRowKey, editor.Id, editorName,
            version.Id, unitId, run.Id, runStartedAt, now.AddDays(-1), now.AddHours(1), tokens);
    }

    /// <summary>R1: аркуш B з'являється в документі разом з обраною «активністю».</summary>
    private async Task RevealHiddenActivityAsync(Scenario s, HiddenActivity activity)
    {
        var at = s.RunStartedAt.AddMinutes(30);

        await using var db = Context();

        db.DocumentSheets.Add(new DocumentSheet(s.DocumentId, s.HiddenSheetId));

        var key = new PeriodKey(s.PeriodKey);
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
        db.TableInstances.Add(new TableInstance(key, instanceId, s.DocumentId, s.HiddenTableId, at));
        db.TableRows.Add(new TableRow(key, rowId, instanceId, RowKey.Create(s.HiddenRowKey), 1, at));

        if (activity.HasFlag(HiddenActivity.State))
        {
            var state = new ApprovalState(s.DocumentId, s.HiddenSheetId, s.PeriodKey);
            state.Submit(s.EditorId, at);
            state.Reject(s.EditorId, "SECRET hidden rejection", at);
            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        if (activity.HasFlag(HiddenActivity.Validation))
        {
            var messages = VisibleMessages(tableA: null);
            for (var i = 0; i < 7; i++)
            {
                messages.Add(new(
                    ValidationSeverity.Error, $"RULE-MX-B{i.ToString(CultureInfo.InvariantCulture)}", "SECRET",
                    s.HiddenTableId, s.HiddenRowKey, null, false));
            }

            // Той самий рядок підсумку (без нового RunAt): інакше «до/після» різнилося б часом прогону.
            await AppendHiddenMessagesAsync(db, s, messages).ConfigureAwait(false);
        }

        if (activity.HasFlag(HiddenActivity.LateEdit))
        {
            await WriteChangeAsync(s.DocumentId, s.PeriodKey, s.HiddenRowKey, s.HiddenColumnId, s.EditorId, late: true, at)
                .ConfigureAwait(false);
        }

        if (activity.HasFlag(HiddenActivity.Modified))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE doc.Document SET ModifiedAt = {at}, ModifiedByUserId = {s.EditorId} WHERE Id = {s.DocumentId}")
                .ConfigureAwait(false);
        }

        if (activity.HasFlag(HiddenActivity.CalcRows))
        {
            await new CalculationResultStore(db, new TestClock(s.RunStartedAt.AddMinutes(1))).WriteResultsAsync(
                    s.RunId,
                    [new CalculationOutput(s.DocumentId, s.HiddenRowKey, [new CalculationOutputValue(s.VersionId, null, "tons", 777m, s.UnitId)], [])],
                    CancellationToken.None)
                .ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Підсумок валідації після появи B: ті самі видимі повідомлення A плюс сім помилок B; час прогону
    /// не змінюється (порівнюється відповідь, а не момент перевірки).
    /// </summary>
    private static async Task AppendHiddenMessagesAsync(EcrDbContext db, Scenario s, List<ValidationMessage> hidden)
    {
        var stored = await db.ValidationResults.AsNoTracking()
            .Where(v => v.DocumentId == s.DocumentId && v.PeriodKey == s.PeriodKey)
            .Select(v => v.MessagesJson)
            .SingleAsync()
            .ConfigureAwait(false);

        var all = JsonSerializer.Deserialize<List<ValidationMessage>>(stored) ?? [];
        all.RemoveAll(m => m.RuleCode.StartsWith("RULE-MX-B", StringComparison.Ordinal));
        all.AddRange(hidden.Where(m => m.RuleCode.StartsWith("RULE-MX-B", StringComparison.Ordinal)));
        var json = JsonSerializer.Serialize(all);

        await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE wf.ValidationResult SET ErrorCount = 8, MessagesJson = {json} WHERE DocumentId = {s.DocumentId} AND PeriodKey = {s.PeriodKey}")
            .ConfigureAwait(false);
    }

    private static List<ValidationMessage> VisibleMessages(int? tableA)
    {
        var messages = new List<ValidationMessage>();
        if (tableA is { } table)
        {
            messages.Add(new(ValidationSeverity.Error, "RULE-MX-A-E", "visible error", table, null, null, false));
            messages.Add(new(ValidationSeverity.Warning, "RULE-MX-A-W", "visible warning", table, null, null, false));
        }

        return messages;
    }

    /// <summary>Рядок журналу — прямим ADO, бо <c>aud.*</c> поза моделлю EF (як в <c>AuditCellFiltersTests</c>).</summary>
    private async Task WriteChangeAsync(
        long documentId, int periodKey, string rowKey, int columnDefId, int author, bool late, DateTime changedAt)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES
                (@changedAt, @periodKey, @documentId, 1, @rowKey, @columnDefId,
                 N'1', N'2', @author, N'UserEdit', @late);
            """;

        command.Parameters.AddWithValue("@changedAt", changedAt);
        command.Parameters.AddWithValue("@periodKey", periodKey);
        command.Parameters.AddWithValue("@documentId", documentId);
        command.Parameters.AddWithValue("@rowKey", rowKey);
        command.Parameters.AddWithValue("@columnDefId", columnDefId);
        command.Parameters.AddWithValue("@author", author);
        command.Parameters.AddWithValue("@late", late);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
