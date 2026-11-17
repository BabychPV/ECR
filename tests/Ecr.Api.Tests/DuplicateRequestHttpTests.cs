// tests/Ecr.Api.Tests/DuplicateRequestHttpTests.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Api.Tests;

/// <summary>
/// Два ОДНАКОВІ запити наскрізно через HTTP — послідовно і справді одночасно: подвійний клік, повтор із діалогу,
/// що закривається, дві вкладки. Кожен тест фіксує, що сервер робить насправді: відмову другого, ідемпотентний
/// повтор або (де так задумано) дві незалежні сутності/задачі — і ніколи 5xx та не два рядки з одним ключем.
/// </summary>
/// <remarks>
/// ⚠ Як зроблено «одночасно». Обидва запити несуть заголовок <see cref="MeetHeader"/>; проміжне ПЗ тесту на самому
/// вході конвеєра тримає перший, доки не прийде другий, і відпускає обидва разом (асерт <c>Overlapped</c>).
/// Далі вони йдуть справжнім конвеєром без жодних підмін: хто кого випередить у обробнику — справа сервера.
/// Застосунок один на клас (старт хоста — десятки секунд); дані кожного тесту власні.
/// </remarks>
[Collection("SqlServer")]
public sealed partial class DuplicateRequestHttpTests(
    SqlServerFixture sql, DuplicateRequestHttpTests.SharedApp shared, ITestOutputHelper output)
    : IClassFixture<DuplicateRequestHttpTests.SharedApp>
{
    private const string Password = "Dup-Request-PE-2099!";
    private const string MeetHeader = "X-Test-Meet";
    private const string Reason = "повторний запит: перевірка ідемпотентності";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    /// <summary>Вікно дозволу тесту завершення: від фіксованої минулої дати до кінця 2099 (B161, правило 394).</summary>
    private static readonly DateOnly PermitFrom = new(2001, 1, 1);

    /// <summary>Кінець вікна дозволу.</summary>
    private static readonly DateOnly PermitTo = new(2099, 12, 31);

    /// <summary>Фіксована минула дата завершення: після неї дозвіл нечинний на будь-який день прогону.</summary>
    private static readonly DateOnly PermitEnd = new(2001, 6, 30);

    private EcrApiFactory App => shared.Factory;

    // ───────────────────────────── ролі ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_ролі_двічі_друге_409_roleCodeTaken_і_роль_одна()
    {
        var (one, two) = await SignInTwiceAsync(["Security.ManageRoles"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        static object Body(string code) => new
        {
            code,
            nameL10n = new Dictionary<string, string> { ["en"] = "Duplicate request" },
            permissionCodes = Array.Empty<string>(),
        };

        // Послідовно.
        var code = $"DQ{Guid.NewGuid():N}"[..12];
        _created.RoleCodes.Add(code);
        var created = await SendAsync(first, HttpMethod.Post, "/api/v1/roles", Body(code)).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Post, "/api/v1/roles", Body(code)).ConfigureAwait(true);

        Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
        AssertRefused(repeated, HttpStatusCode.Conflict, "ECR-SEC-0409", "err.ECR-SEC-0409.roleCodeTaken");
        Assert.Equal(1, await RolesWithCodeAsync(code).ConfigureAwait(true));

        // Одночасно.
        var raced = $"DQ{Guid.NewGuid():N}"[..12];
        _created.RoleCodes.Add(raced);
        var replies = await RaceAsync("roles", first, second, HttpMethod.Post, "/api/v1/roles", Body(raced)).ConfigureAwait(true);

        AssertNoServerError(replies);
        Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
        AssertRefused(
            replies.Single(r => r.Status != HttpStatusCode.Created),
            HttpStatusCode.Conflict, "ECR-SEC-0409", "err.ECR-SEC-0409.roleCodeTaken");
        Assert.Equal(1, await RolesWithCodeAsync(raced).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_ролі_двічі_послідовно_друге_404_а_сусідня_роль_ціла()
    {
        var (one, two) = await SignInTwiceAsync(["Security.ManageRoles"]).ConfigureAwait(true);
        using var first = one;
        two.Dispose();

        var neighbour = await NewRoleAsync().ConfigureAwait(true);

        // Послідовно.
        var victim = await NewRoleAsync().ConfigureAwait(true);
        var deleted = await SendAsync(first, HttpMethod.Delete, $"/api/v1/roles/{victim}", null).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Delete, $"/api/v1/roles/{victim}", null).ConfigureAwait(true);

        Assert.True(deleted.Status == HttpStatusCode.NoContent, $"{deleted}\n{App.ErrorsText}");
        AssertRefused(repeated, HttpStatusCode.NotFound, "ECR-SEC-0404", "err.ECR-SEC-0404.roleNotFound");
        Assert.Equal((false, true), await RolesExistAsync(victim, neighbour).ConfigureAwait(true));
    }

    /// <summary>
    /// B150: два одночасні DELETE тієї самої ролі — один <c>204</c>, другий той самий
    /// <c>404 ECR-SEC-0404 roleNotFound</c>, що й послідовний повтор (раніше — <c>500</c> «Sequence contains no
    /// elements» з <c>UserStore.RemoveRoleAsync</c>); сусідня роль ціла.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_ролі_двічі_одночасно_одне_204_друге_404_а_сусідня_роль_ціла()
    {
        var (one, two) = await SignInTwiceAsync(["Security.ManageRoles"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var neighbour = await NewRoleAsync().ConfigureAwait(true);
        var raced = await NewRoleAsync().ConfigureAwait(true);
        var replies = await RaceAsync("role-delete", first, second, HttpMethod.Delete, $"/api/v1/roles/{raced}", null)
            .ConfigureAwait(true);

        var exist = await RolesExistAsync(raced, neighbour).ConfigureAwait(true);
        output.WriteLine($"ROWS role-delete victim={exist.Victim} neighbour={exist.Neighbour}");

        AssertNoServerError(replies);
        Assert.Single(replies, r => r.Status == HttpStatusCode.NoContent);
        AssertRefused(
            replies.Single(r => r.Status != HttpStatusCode.NoContent),
            HttpStatusCode.NotFound, "ECR-SEC-0404", "err.ECR-SEC-0404.roleNotFound");
        Assert.Equal((false, true), exist);
    }

    // ───────────────────────────── документи ─────────────────────────────

    /// <summary>
    /// Документує ЧИННЕ правило: бізнес-унікальності документа немає (ні за проєктом, ні за версією шаблону, ні за
    /// назвою) — унікальний лише номер <c>(ProjectId, BusinessKey)</c>, який видає сервер. Два однакові
    /// <c>POST /documents</c> дають ДВА документи з різними номерами (<c>CreateDocumentHandler</c>: «його документ
    /// законний, зайнятий лише НОМЕР»). Тест не оцінює правило, а фіксує його.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чинне_правило_два_однакові_створення_документа_дають_два_документи_з_різними_номерами()
    {
        var s = await ArrangeDocumentAsync(209904).ConfigureAwait(true);
        var (one, two) = await SignInTwiceAsync(
            ["Document.View", "Document.Create"], (ResourceKind.Project, s.ProjectId, GrantLevel.Write)).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var body = new { projectId = s.ProjectId, sheetDefIds = new[] { s.SheetDefId } };
        var before = await DocumentKeysAsync(s.ProjectId).ConfigureAwait(true);

        // Послідовно.
        var a = await SendAsync(first, HttpMethod.Post, "/api/v1/documents", body).ConfigureAwait(true);
        var b = await SendAsync(first, HttpMethod.Post, "/api/v1/documents", body).ConfigureAwait(true);
        Assert.True(a.Status == HttpStatusCode.Created && b.Status == HttpStatusCode.Created, $"{a}\n{b}\n{App.ErrorsText}");

        // Одночасно.
        var replies = await RaceAsync("documents", first, second, HttpMethod.Post, "/api/v1/documents", body).ConfigureAwait(true);
        Assert.All(replies, r => Assert.True(r.Status == HttpStatusCode.Created, $"{r}\n{App.ErrorsText}"));

        var ids = new[] { a, b, replies[0], replies[1] }.Select(r => r.Json.GetProperty("documentId").GetInt64()).ToList();
        Assert.Equal(4, ids.Distinct().Count());

        var after = await DocumentKeysAsync(s.ProjectId).ConfigureAwait(true);
        Assert.Equal(before.Count + 4, after.Count);
        Assert.Equal(after.Count, after.Distinct(StringComparer.Ordinal).Count());
    }

    // ───────────────────────────── задачі: експорт і зріз звіту ─────────────────────────────

    /// <summary>
    /// Чинна поведінка: експорт документа не зводить однакові запити — кожен ставить власну задачу
    /// (<c>ExportDocumentHandler</c>: новий <c>exportId</c> на виклик, <c>EnqueueAsync</c> без ключа).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чинна_поведінка_два_однакові_експорти_документа_ставлять_дві_задачі()
    {
        var s = await ArrangeDocumentAsync(209905).ConfigureAwait(true);
        var (one, two) = await SignInTwiceAsync(
            ["Document.View", "Document.Export"], (ResourceKind.Project, s.ProjectId, GrantLevel.Write)).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var url = $"/api/v1/documents/{s.DocumentId}/export";
        var body = new { includeFormulas = false, includeStyles = false, language = "en", periodKey = s.PeriodKey.Value };

        await AssertTwoJobsEachAsync("export", first, second, url, body, s.DocumentId).ConfigureAwait(true);
    }

    /// <summary>Чинна поведінка: річний експорт результатів розрахунку — так само задача на кожен запит.</summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чинна_поведінка_два_однакові_річні_експорти_результатів_ставлять_дві_задачі()
    {
        var s = await ArrangeDocumentAsync(209906).ConfigureAwait(true);
        var (one, two) = await SignInTwiceAsync(
            ["Document.View", "Document.Export", "Calculation.View"],
            (ResourceKind.Project, s.ProjectId, GrantLevel.Write)).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var url = $"/api/v1/documents/{s.DocumentId}/calculation-results/year/export";
        var body = new { year = 2099, layout = "long", language = "en" };

        await AssertTwoJobsEachAsync("calc-year-export", first, second, url, body, s.DocumentId).ConfigureAwait(true);
    }

    /// <summary>
    /// Чинна поведінка: побудова зрізу звіту — задача на кожен запит (<c>BuildReportSnapshotHandler</c>:
    /// <c>EnqueueAsync</c> без ключа; кожна побудова — новий незмінний зріз). Поточним лишається не більше одного.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чинна_поведінка_дві_однакові_побудови_зрізу_звіту_ставлять_дві_задачі_поточний_зріз_один()
    {
        var s = await ArrangeDocumentAsync(209907).ConfigureAwait(true);
        var (one, two) = await SignInTwiceAsync(
            ["Report.EditDefinition", "Report.BuildSnapshot", "Report.ViewRegulatory", "System.ViewHealth", "Document.View"],
            (ResourceKind.Project, s.ProjectId, GrantLevel.Write)).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var (code, versionId) = await PublishReportAsync(first).ConfigureAwait(true);
        var url = $"/api/v1/reports/{code}/build";
        var jobCode = typeof(IReportSnapshotJob).FullName!;
        var before = await JobCountAsync(jobCode, documentId: null).ConfigureAwait(true);

        // Послідовно — у періоді 209907; одночасно — у 209908 (щоб лічити зрізи окремо).
        var body = new { projectId = s.ProjectId, periodKey = 209907 };
        var a = await SendAsync(first, HttpMethod.Post, url, body).ConfigureAwait(true);
        var b = await SendAsync(first, HttpMethod.Post, url, body).ConfigureAwait(true);
        Assert.True(a.Status == HttpStatusCode.Accepted && b.Status == HttpStatusCode.Accepted, $"{a}\n{b}\n{App.ErrorsText}");

        var racedBody = new { projectId = s.ProjectId, periodKey = 209908 };
        var replies = await RaceAsync("report-build", first, second, HttpMethod.Post, url, racedBody).ConfigureAwait(true);
        Assert.All(replies, r => Assert.True(r.Status == HttpStatusCode.Accepted, $"{r}\n{App.ErrorsText}"));

        var jobIds = new[] { a, b, replies[0], replies[1] }.Select(r => r.Json.GetProperty("jobId").GetString()!).ToList();
        _created.JobIds.AddRange(jobIds);
        Assert.Equal(4, jobIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(before + 4, await JobCountAsync(jobCode, documentId: null).ConfigureAwait(true));

        // Задачі доходять до кінця (успіх чи відмова — не предмет тесту); поточних зрізів — не більше одного на період.
        foreach (var jobId in jobIds)
        {
            output.WriteLine($"JOB report-build {await WaitJobAsync(first, jobId).ConfigureAwait(true)}");
        }

        foreach (var period in new[] { 209907, 209908 })
        {
            var (total, current) = await SnapshotsAsync(versionId, s.ProjectId, period).ConfigureAwait(true);
            output.WriteLine($"SNAPSHOTS report-build period={period} total={total} current={current}");
            Assert.True(current <= 1, $"поточних зрізів {current} за період {period}");
        }
    }

    // ───────────────────────────── маршрут погодження ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заміна_маршруту_погодження_двічі_послідовно_той_самий_маршрут_без_зайвих_кроків()
    {
        var s = await ArrangeDocumentAsync(209909).ConfigureAwait(true);
        var (one, two) = await SignInTwiceAsync(
            ["Project.Manage"], (ResourceKind.Project, s.ProjectId, GrantLevel.Manage)).ConfigureAwait(true);
        using var first = one;
        two.Dispose();

        var roleIds = new[] { await NewRoleAsync().ConfigureAwait(true), await NewRoleAsync().ConfigureAwait(true) };
        var url = $"/api/v1/projects/{s.ProjectId}/approval-route";
        var body = new { roleIds };

        var a = await SendAsync(first, HttpMethod.Put, url, body).ConfigureAwait(true);
        var b = await SendAsync(first, HttpMethod.Put, url, body).ConfigureAwait(true);

        Assert.True(a.Status == HttpStatusCode.OK && b.Status == HttpStatusCode.OK, $"{a}\n{b}\n{App.ErrorsText}");
        Assert.Equal(2, b.Json.GetProperty("steps").GetInt32());
        var (routes, steps) = await RouteAsync(s.ProjectId).ConfigureAwait(true);
        Assert.Equal(1, routes);
        Assert.Equal(roleIds, steps);
    }

    // ───────────────────────────── поточний період ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Закріплення_поточного_періоду_двічі_ідемпотентне()
    {
        var s = await ArrangeDocumentAsync(209910, open: true).ConfigureAwait(true);
        var (one, two) = await SignInTwiceAsync(
            ["Period.Configure", "Document.View"], (ResourceKind.Project, s.ProjectId, GrantLevel.Manage)).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var periodId = await PeriodIdAsync(s.ProjectId, 209910).ConfigureAwait(true);
        var url = $"/api/v1/projects/{s.ProjectId}/settings/current-period";
        var body = new { mode = "Pinned", pinnedPeriodId = periodId, reason = Reason };

        // Послідовно.
        var a = await SendAsync(first, HttpMethod.Put, url, body).ConfigureAwait(true);
        var b = await SendAsync(first, HttpMethod.Put, url, body).ConfigureAwait(true);
        Assert.True(a.Status == HttpStatusCode.OK && b.Status == HttpStatusCode.OK, $"{a}\n{b}\n{App.ErrorsText}");
        Assert.Equal(("Pinned", (int?)periodId), await CurrentPeriodAsync(s.ProjectId).ConfigureAwait(true));

        // Застарілий маршрут — те саме закріплення, теж двічі.
        var legacy = $"/api/v1/projects/{s.ProjectId}/current-period";
        var legacyBody = new { pinnedPeriodId = periodId, reason = Reason };
        var c = await SendAsync(first, HttpMethod.Put, legacy, legacyBody).ConfigureAwait(true);
        var d = await SendAsync(first, HttpMethod.Put, legacy, legacyBody).ConfigureAwait(true);
        Assert.True(c.Status == HttpStatusCode.NoContent && d.Status == HttpStatusCode.NoContent, $"{c}\n{d}\n{App.ErrorsText}");

        // Одночасно.
        var replies = await RaceAsync("current-period", first, second, HttpMethod.Put, url, body).ConfigureAwait(true);
        Assert.All(replies, r => Assert.True(r.Status == HttpStatusCode.OK, $"{r}\n{App.ErrorsText}"));
        Assert.Equal(("Pinned", (int?)periodId), await CurrentPeriodAsync(s.ProjectId).ConfigureAwait(true));
        Assert.Equal(1, await PeriodCountAsync(s.ProjectId).ConfigureAwait(true));
    }

    // ───────────────────────────── довідник: вікно чинності запису ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вікно_чинності_запису_довідника_двічі_ідемпотентне()
    {
        var (one, two, userId) = await SignInTwiceWithIdAsync(["Registry.View", "Registry.EditData"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var (registryId, registryCode, entryId) = await NewRegistryEntryAsync(userId).ConfigureAwait(true);
        var url = $"/api/v1/registries/{registryCode}/entries/{entryId}/validity";

        // Послідовно.
        var body = new { from = "2099-01-01", to = (string?)null };
        var a = await SendAsync(first, HttpMethod.Post, url, body).ConfigureAwait(true);
        var b = await SendAsync(first, HttpMethod.Post, url, body).ConfigureAwait(true);
        Assert.True(a.Status == HttpStatusCode.OK && b.Status == HttpStatusCode.OK, $"{a}\n{b}\n{App.ErrorsText}");
        Assert.Equal((1, "2099-01-01"), await RegistryEntriesAsync(registryId, entryId).ConfigureAwait(true));

        // Одночасно — інше вікно, щоб повтор справді щось писав.
        var racedBody = new { from = "2099-02-01", to = (string?)null };
        var replies = await RaceAsync("validity", first, second, HttpMethod.Post, url, racedBody).ConfigureAwait(true);

        // ✎ B150: друга з двох одночасних транзакцій над системно-версійованим dic.RegistryEntry могла впасти 13535
        // («transaction time earlier than period start») голим 500. Тепер — 200, або 409 concurrentChange; 500 — ніколи.
        Assert.Contains(replies, r => r.Status == HttpStatusCode.OK);
        Assert.All(replies, r => Assert.True(
            r.Status == HttpStatusCode.OK
            || (r.Status == HttpStatusCode.Conflict && r.ErrorCode == "ECR-CELL-0409"
                && r.MessageKey == "err.ECR-CELL-0409.concurrentChange"),
            $"{r}\n{App.ErrorsText}"));
        Assert.Equal((1, "2099-02-01"), await RegistryEntriesAsync(registryId, entryId).ConfigureAwait(true));
    }

    // ───────────────────────────── дозвіл: завершення ─────────────────────────────

    /// <summary>
    /// Завершення дозволу явною датою <see cref="PermitEnd"/>: перший запит робить дозвіл нечинним, другий —
    /// <c>409 ECR-PRM-4096 notActive</c> (<c>EndPermitHandler</c>, перевірка під блокуванням запису). Окремого кроку
    /// «підтвердити завершення» на сервері немає — маршрут один, <c>POST /permits/{id}/end</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ B161 (правило 394): дати фіксовані, без <c>DateTime.UtcNow</c>. ⚠ Не 2099: стан дозволу сервер рахує на
    /// СПРАВЖНЮ сьогоднішню дату (<c>PermitLifecycle.StatusOf</c>, <c>end &lt;= today</c> → <c>Expired</c>), тож
    /// завершення датою в 2099 лишило б дозвіл чинним, і другий запит дав би <c>200</c>, а не <c>notActive</c>. Тому
    /// вікно — <see cref="PermitFrom"/>..<see cref="PermitTo"/> (2099), а дата завершення — фіксована минула
    /// <see cref="PermitEnd"/>: результат не залежить від дня прогону.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Завершення_дозволу_двічі_друге_409_notActive()
    {
        var (one, two) = await SignInTwiceAsync(
            ["Registry.View", "Registry.EditData", "Permit.EditType", "Permit.PublishType"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var type = await PublishPermitTypeAsync(first).ConfigureAwait(true);
        var body = new { reason = "об'єкт виведено з експлуатації", endDate = Iso(PermitEnd) };

        // Послідовно.
        var permit = await ActivePermitAsync(first, type, "DQ-1").ConfigureAwait(true);
        var ended = await SendAsync(first, HttpMethod.Post, $"/api/v1/permits/{permit}/end", body).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Post, $"/api/v1/permits/{permit}/end", body).ConfigureAwait(true);

        Assert.True(ended.Status == HttpStatusCode.OK, $"{ended}\n{App.ErrorsText}");
        Assert.Equal(Iso(PermitEnd), ended.Json.GetProperty("actualEnd").GetString());
        AssertRefused(repeated, HttpStatusCode.Conflict, "ECR-PRM-4096", "err.ECR-PRM-4096.notActive");

        // Одночасно.
        var raced = await ActivePermitAsync(first, type, "DQ-2").ConfigureAwait(true);
        var replies = await RaceAsync("permit-end", first, second, HttpMethod.Post, $"/api/v1/permits/{raced}/end", body)
            .ConfigureAwait(true);

        AssertNoServerError(replies);
        Assert.Single(replies, r => r.Status == HttpStatusCode.OK);
        AssertRefused(
            replies.Single(r => r.Status != HttpStatusCode.OK),
            HttpStatusCode.Conflict, "ECR-PRM-4096", "err.ECR-PRM-4096.notActive");

        var read = await SendAsync(first, HttpMethod.Get, $"/api/v1/permits/{raced}", null).ConfigureAwait(true);
        Assert.Equal(Iso(PermitEnd), read.Json.GetProperty("actualEnd").GetString());
    }

    // ───────────────────────────── спільне для задач ─────────────────────────────

    /// <summary>Два послідовні й два одночасні запити постановки задачі експорту: 202 кожен, задач у журналі — чотири.</summary>
    private async Task AssertTwoJobsEachAsync(
        string label, HttpClient first, HttpClient second, string url, object body, long documentId)
    {
        var jobCode = typeof(IExcelExportJob).FullName!;
        Assert.Equal(0, await JobCountAsync(jobCode, documentId).ConfigureAwait(false));

        var a = await SendAsync(first, HttpMethod.Post, url, body).ConfigureAwait(false);
        var b = await SendAsync(first, HttpMethod.Post, url, body).ConfigureAwait(false);
        Assert.True(a.Status == HttpStatusCode.Accepted && b.Status == HttpStatusCode.Accepted, $"{a}\n{b}\n{App.ErrorsText}");
        Assert.Equal(2, await JobCountAsync(jobCode, documentId).ConfigureAwait(false));

        var replies = await RaceAsync(label, first, second, HttpMethod.Post, url, body).ConfigureAwait(false);
        Assert.All(replies, r => Assert.True(r.Status == HttpStatusCode.Accepted, $"{r}\n{App.ErrorsText}"));

        var jobIds = new[] { a, b, replies[0], replies[1] }.Select(r => r.Json.GetProperty("jobId").GetString()!).ToList();
        _created.JobIds.AddRange(jobIds);
        Assert.Equal(4, jobIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, await JobCountAsync(jobCode, documentId).ConfigureAwait(false));
    }

    // ───────────────────────────── запити ─────────────────────────────

    /// <summary>Відповідь сервера: код, поля тіла помилки й саме тіло.</summary>
    private sealed record Reply(HttpStatusCode Status, string? ErrorCode, string? MessageKey, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;

        public override string ToString() => $"{(int)Status} {Body}";
    }

    private static async Task<Reply> SendAsync(HttpClient client, HttpMethod method, string url, object? body, string? meet = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (meet is not null)
        {
            request.Headers.Add(MeetHeader, meet);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (response.IsSuccessStatusCode || text.Length == 0 || text[0] != '{')
        {
            return new Reply(response.StatusCode, null, null, text);
        }

        var root = JsonDocument.Parse(text).RootElement;
        return new Reply(response.StatusCode, Text(root, "errorCode"), Text(root, "messageKey"), text);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Два однакові запити, відпущені в конвеєр РАЗОМ (проміжне ПЗ тесту чекає на обидва).</summary>
    private async Task<Reply[]> RaceAsync(
        string label, HttpClient first, HttpClient second, HttpMethod method, string url, object? body)
    {
        var key = Guid.NewGuid().ToString("N");
        var meeting = shared.Meetings.GetOrAdd(key, _ => new SubmitTwiceHttpTests.Meeting());

        var replies = await Task.WhenAll(
            Task.Run(() => SendAsync(first, method, url, body, key)),
            Task.Run(() => SendAsync(second, method, url, body, key))).ConfigureAwait(false);

        foreach (var reply in replies)
        {
            output.WriteLine($"RACE {label} {(int)reply.Status} {reply.ErrorCode} {reply.MessageKey}");
        }

        Assert.True(meeting.Overlapped, $"{label}: запити не зустрілися на вході конвеєра");
        return replies;
    }

    private void AssertNoServerError(Reply[] replies)
        => Assert.True(
            replies.All(r => (int)r.Status < 500),
            $"5xx на однакових одночасних запитах:\n{replies[0]}\n{replies[1]}\n{App.ErrorsText}");

    private void AssertRefused(Reply reply, HttpStatusCode status, string errorCode, string messageKey)
    {
        Assert.True(reply.Status == status, $"очікувалось {(int)status}: {reply}\n{App.ErrorsText}");
        Assert.Equal(errorCode, reply.ErrorCode);
        Assert.Equal(messageKey, reply.MessageKey);
    }

    private async Task<string> WaitJobAsync(HttpClient client, string jobId)
    {
        var url = $"/api/v1/jobs/{Uri.EscapeDataString(jobId)}";
        var state = "?";

        for (var attempt = 0; attempt < 240; attempt++)
        {
            var reply = await SendAsync(client, HttpMethod.Get, url, null).ConfigureAwait(false);
            Assert.True(reply.Status == HttpStatusCode.OK, $"задача {jobId}: {reply}\n{App.ErrorsText}");

            state = reply.Json.GetProperty("state").GetString() ?? "?";
            if (state is not ("Queued" or "Running"))
            {
                // Разом зі станом — повідомлення й код помилки задачі (у вивід тесту).
                return $"{state} {Text(reply.Json, "errorCode")} {Text(reply.Json, "message")}";
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        Assert.Fail($"задача {jobId} не дійшла до кінця за 60 с (стан {state}).\n{App.ErrorsText}");
        return state;
    }

    // ───────────────────────────── читання бази ─────────────────────────────

    private EcrDbContext Db() => new TestDocumentBuilder(sql.ConnectionString).CreateContext();

    private async Task<int> RolesWithCodeAsync(string code)
    {
        await using var db = Db();
        return await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sec.Role WHERE Code = {code}")
            .SingleAsync().ConfigureAwait(false);
    }

    private async Task<(bool Victim, bool Neighbour)> RolesExistAsync(int victim, int neighbour)
    {
        await using var db = Db();
        var ids = await db.Roles.AsNoTracking().Where(r => r.Id == victim || r.Id == neighbour).Select(r => r.Id)
            .ToListAsync().ConfigureAwait(false);
        return (ids.Contains(victim), ids.Contains(neighbour));
    }

    private async Task<List<string>> DocumentKeysAsync(int projectId)
    {
        await using var db = Db();
        return await db.Database
            .SqlQuery<string>($"SELECT BusinessKey AS Value FROM doc.Document WHERE ProjectId = {projectId}")
            .ToListAsync().ConfigureAwait(false);
    }

    private async Task<int> JobCountAsync(string jobCode, long? documentId)
    {
        await using var db = Db();
        return await db.JobProgresses.AsNoTracking()
            .CountAsync(j => j.JobCode == jobCode && (documentId == null || j.DocumentId == documentId))
            .ConfigureAwait(false);
    }

    private async Task<(int Total, int Current)> SnapshotsAsync(int versionId, int projectId, int periodKey)
    {
        await using var db = Db();
        var flags = await db.ReportSnapshots.AsNoTracking()
            .Where(x => x.ReportVersionId == versionId && x.ProjectId == projectId && x.PeriodKey == periodKey)
            .Select(x => x.IsCurrent)
            .ToListAsync().ConfigureAwait(false);
        return (flags.Count, flags.Count(f => f));
    }

    private async Task<(int Routes, int[] StepRoles)> RouteAsync(int projectId)
    {
        await using var db = Db();
        var routes = await db.ApprovalRoutes.AsNoTracking().Where(r => r.ProjectId == projectId).Select(r => r.Id)
            .ToListAsync().ConfigureAwait(false);
        var steps = await db.ApprovalSteps.AsNoTracking().Where(x => routes.Contains(x.ApprovalRouteId))
            .OrderBy(x => x.Ordinal).Select(x => x.RoleId).ToArrayAsync().ConfigureAwait(false);
        return (routes.Count, steps);
    }

    private async Task<int> PeriodIdAsync(int projectId, int periodKey)
    {
        await using var db = Db();
        return await db.Periods.AsNoTracking()
            .Where(p => p.ProjectId == projectId && p.PeriodKeyValue == periodKey).Select(p => p.Id)
            .SingleAsync().ConfigureAwait(false);
    }

    private async Task<int> PeriodCountAsync(int projectId)
    {
        await using var db = Db();
        return await db.Periods.AsNoTracking().CountAsync(p => p.ProjectId == projectId).ConfigureAwait(false);
    }

    private async Task<(string Mode, int? PeriodId)> CurrentPeriodAsync(int projectId)
    {
        await using var db = Db();
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId).ConfigureAwait(false);
        return (project.CurrentPeriodMode.ToString(), project.CurrentPeriodId);
    }

    private async Task<(int Entries, string ValidFrom)> RegistryEntriesAsync(int registryId, long entryId)
    {
        await using var db = Db();
        var entries = await db.RegistryEntries.AsNoTracking().Where(e => e.RegistryDefId == registryId)
            .ToListAsync().ConfigureAwait(false);
        var from = entries.Single(e => e.Id == entryId).ValidFrom;
        return (entries.Count, string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd}", from));
    }

    // ───────────────────────────── підготовка ─────────────────────────────

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Документ у періоді 2099 року з аркушем у складі; <paramref name="open"/> — активний проєкт і відкритий період.</summary>
    private async Task<TestDocument> ArrangeDocumentAsync(int periodKey, bool open = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey, columnCount: 3, rowCount: 2, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);
        _created.Documents.Add(document);

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        if (open)
        {
            var now = DateTime.UtcNow;
            var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
            project.Activate(now);

            var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
            var period = await db.Periods
                .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(false);
            period.RecomputeBoundaries(policy, SiteZone);
            period.AdvanceTo(PeriodState.Open, now);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return document;
    }

    /// <summary>Роль без жодного використання (ні призначень, ні грантів, ні кроків маршруту).</summary>
    private async Task<int> NewRoleAsync()
    {
        await using var db = Db();
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Duplicate request"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        _created.RoleIds.Add(role.Id);
        return role.Id;
    }

    private async Task<(int RegistryId, string Code, long EntryId)> NewRegistryEntryAsync(int userId)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = Db();

        var registry = new RegistryDef(EcrCode.Create($"DQ{tag}"), Name($"Duplicate request {tag}"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);
        _created.RegistryIds.Add(registry.Id);

        var entry = new RegistryEntry(registry.Id, EcrCode.Create($"E{tag}"), Name($"E{tag}"), userId, DateTime.UtcNow);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (registry.Id, $"DQ{tag}", entry.Id);
    }

    private async Task<(string Code, int VersionId)> PublishReportAsync(HttpClient client)
    {
        var code = $"DQ{Guid.NewGuid():N}"[..16];
        var created = await SendAsync(client, HttpMethod.Post, "/api/v1/reports", new
        {
            code,
            nameL10n = new Dictionary<string, string> { ["en"] = "Duplicate request report" },
            isRegulatory = true,
            version = "1.0",
            columns = new[]
            {
                new { code = "DocumentId", kind = "number" },
                new { code = "OutputCode", kind = "text" },
                new { code = "Value", kind = "number" },
            },
            rules = (object?)null,
        }).ConfigureAwait(false);
        Assert.True((int)created.Status is >= 200 and < 300, $"опис звіту: {created}\n{App.ErrorsText}");

        var definitionId = created.Json.GetProperty("id").GetInt32();
        _created.ReportDefIds.Add(definitionId);
        var versionId = created.Json.GetProperty("versions")[0].GetProperty("id").GetInt32();

        var published = await SendAsync(
            client, HttpMethod.Post, $"/api/v1/reports/{definitionId}/versions/{versionId}/publish",
            new { reason = "публікація для тесту повторних запитів" }).ConfigureAwait(false);
        Assert.True((int)published.Status is >= 200 and < 300, $"публікація звіту: {published}\n{App.ErrorsText}");

        return (code, versionId);
    }

    private async Task<string> PublishPermitTypeAsync(HttpClient client)
    {
        var code = $"DQ{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        _created.PermitTypeCodes.Add(code);
        var created = await SendAsync(
            client, HttpMethod.Post, "/api/v1/permit-types", new { code, nameL10n = new { en = code }, category = "Air" })
            .ConfigureAwait(false);
        Assert.True(created.Status == HttpStatusCode.Created, $"тип дозволу: {created}\n{App.ErrorsText}");

        var replaced = await SendAsync(client, HttpMethod.Put, $"/api/v1/permit-types/{code}", new
        {
            rowVersion = created.Json.GetProperty("rowVersion").GetString(),
            nameL10n = new { en = code },
            category = "Air",
            tables = new[]
            {
                new
                {
                    code = "Rows",
                    nameL10n = new { en = "Rows" },
                    order = 1,
                    columns = new[]
                    {
                        new
                        {
                            code = "Name", nameL10n = new { en = "Name" }, dataType = "String", unitId = (int?)null, required = true,
                            key = true, group = (string?)null, precision = (int?)null, scale = (int?)null, listScope = (string?)null,
                            listRegistryCode = (string?)null, siblingTableCode = (string?)null, choices = (string[]?)null, order = 1,
                        },
                    },
                    rules = Array.Empty<object>(),
                },
            },
            rules = Array.Empty<object>(),
        }).ConfigureAwait(false);
        Assert.True(replaced.Status == HttpStatusCode.OK, $"таблиці типу дозволу: {replaced}\n{App.ErrorsText}");

        var published = await SendAsync(
            client, HttpMethod.Post, $"/api/v1/permit-types/{code}/publish",
            new { reason = "публікація для тесту повторних запитів", rowVersion = replaced.Json.GetProperty("rowVersion").GetString() })
            .ConfigureAwait(false);
        Assert.True(published.Status == HttpStatusCode.OK, $"публікація типу дозволу: {published}\n{App.ErrorsText}");

        return code;
    }

    /// <summary>Активований дозвіл з вікном <see cref="PermitFrom"/>..<see cref="PermitTo"/> (чинний на будь-який день прогону).</summary>
    private async Task<long> ActivePermitAsync(HttpClient client, string type, string number)
    {
        var created = await SendAsync(client, HttpMethod.Post, "/api/v1/permits", new
        {
            typeCode = type,
            number,
            location = (string?)null,
            issueDate = (string?)null,
            validFrom = Iso(PermitFrom),
            validTo = Iso(PermitTo),
            reportingFlags = (object?)null,
        }).ConfigureAwait(false);
        Assert.True(created.Status == HttpStatusCode.Created, $"дозвіл: {created}\n{App.ErrorsText}");

        var id = created.Json.GetProperty("id").GetInt64();
        var activated = await SendAsync(client, HttpMethod.Post, $"/api/v1/permits/{id}/activate", null).ConfigureAwait(false);
        Assert.True(activated.Status == HttpStatusCode.OK, $"активація дозволу: {activated}\n{App.ErrorsText}");

        return id;
    }

    private async Task<(HttpClient First, HttpClient Second)> SignInTwiceAsync(
        string[] permissions, params (ResourceKind Kind, int ResourceId, GrantLevel Level)[] grants)
    {
        var (first, second, _) = await SignInTwiceWithIdAsync(permissions, grants).ConfigureAwait(false);
        return (first, second);
    }

    /// <summary>Свіжий користувач із власною роллю (права й гранти) і два його сеанси — «дві вкладки».</summary>
    private async Task<(HttpClient First, HttpClient Second, int UserId)> SignInTwiceWithIdAsync(
        string[] permissions, params (ResourceKind Kind, int ResourceId, GrantLevel Level)[] grants)
    {
        var name = $"dq_{Guid.NewGuid():N}"[..20];
        int userId;

        await using (var db = Db())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Duplicate request operator"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);
            userId = user.Id;
            _created.UserNames.Add(name);
            _created.RoleIds.Add(role.Id);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            foreach (var (kind, resourceId, level) in grants)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, kind, resourceId, level));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return (await SignInAsync(name).ConfigureAwait(false), await SignInAsync(name).ConfigureAwait(false), userId);
    }

    private async Task<HttpClient> SignInAsync(string userName)
    {
        var client = App.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {App.ErrorsText}");

        return client;
    }

    // ───────────────────────────── застосунок класу ─────────────────────────────

    /// <summary>Один застосунок на клас: проміжне ПЗ зустрічі двох запитів на вході конвеєра.</summary>
    public sealed class SharedApp : IDisposable
    {
        public SharedApp(SqlServerFixture sql)
        {
            Factory = new EcrApiFactory(sql)
            {
                ExtraServices = services => services.AddSingleton<IStartupFilter>(new MeetingStartupFilter(Meetings)),
            };
        }

        public EcrApiFactory Factory { get; }

        public ConcurrentDictionary<string, SubmitTwiceHttpTests.Meeting> Meetings { get; } = new(StringComparer.Ordinal);

        public void Dispose() => Factory.Dispose();
    }

    /// <summary>Запит із заголовком зустрічі чекає на свою пару на вході конвеєра; решта проходять як є.</summary>
    public sealed class MeetingStartupFilter(ConcurrentDictionary<string, SubmitTwiceHttpTests.Meeting> meetings) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use(async (context, following) =>
                {
                    if (context.Request.Headers.TryGetValue(MeetHeader, out var key)
                        && meetings.TryGetValue(key.ToString(), out var meeting))
                    {
                        await meeting.ArriveAsync().ConfigureAwait(false);
                    }

                    await following(context).ConfigureAwait(false);
                });

                next(app);
            };
    }
}
