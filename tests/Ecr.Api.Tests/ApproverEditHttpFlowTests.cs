// tests/Ecr.Api.Tests/ApproverEditHttpFlowTests.cs
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-442 (BRIEF-1021 П.3) наскрізно через HTTP: після <c>Submit</c> поданий аркуш править ЛИШЕ погоджувач
/// поточного кроку маршруту; правка — у журналі з позначкою «правка погоджувача»
/// (<c>aud.CellChange.Origin = ApproverEdit</c>); при затвердженні знімається новий зріз даних із цією правкою.
/// </summary>
/// <remarks>
/// Матриця прав на поданому аркуші: погоджувач поточного кроку — так; автор (рівень <c>Submit</c>) — ні;
/// погоджувач ІНШОГО кроку — ні; сторонній із <c>Write</c> — ні. Після кроку 1 черга переходить — і право
/// правити переходить разом із нею. Затверджений аркуш не править ніхто.
///
/// Мутаційні докази (у локальному worktree, не комітяться):
/// (а) <c>EditRules.CanEdit</c>: гілку <c>Submitted when IsCurrentApprover</c> прибрати — червоніє крок
///     «погоджувач першого кроку править» (403 DocumentSubmitted);
/// (б) <c>AccessDecisionService.IsCurrentApproverAsync</c>: не передавати роль кроку
///     (<c>CanApprove(profile, ctx)</c> без <c>step?.RoleId</c>) — червоніє «погоджувач ІНШОГО кроку не править»;
/// (в) <c>PatchCellsHandler.EnsureSheetStillEditableAsync</c>: не перевіряти погоджувача під блокуванням
///     (одразу <c>ApproverEdit = true</c> при <c>ByApprover</c>) — тут зелено (кроки йдуть послідовно), гонку
///     ловить <c>PatchCellsTests.D442_черга_перейшла_під_блокуванням_правку_погоджувача_відхилено</c>; (г) <c>BuildAuditRecords</c>: <c>batchOrigin = request.Origin</c> —
///     червоніє перевірка походження <c>ApproverEdit</c>; (д) <c>ApproveSheetHandler</c>: не кликати
///     <c>ApprovalSnapshotRefresh.RefreshAsync</c> — червоніє «зріз затвердження містить правку».
/// </remarks>
[Collection("SqlServer")]
public sealed class ApproverEditHttpFlowTests(SqlServerFixture sql)
{
    private const string Password = "Approver-Edit-D442!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();
    private static readonly JsonSerializerOptions MapJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-442")]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Поданий_аркуш_править_лише_погоджувач_поточного_кроку_з_позначкою_і_новим_зрізом()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit).ConfigureAwait(true);
        var first = await NewUserAsync(s, GrantLevel.Approve).ConfigureAwait(true);
        var second = await NewUserAsync(s, GrantLevel.Approve).ConfigureAwait(true);
        var outsider = await NewUserAsync(s, GrantLevel.Write).ConfigureAwait(true);
        await RouteAsync(s, first.RoleId, second.RoleId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var firstClient = await SignInAsync(app, first.Name).ConfigureAwait(true);
        using var secondClient = await SignInAsync(app, second.Name).ConfigureAwait(true);
        using var outsiderClient = await SignInAsync(app, outsider.Name).ConfigureAwait(true);

        var column = await NumberColumnCodeAsync(author, s).ConfigureAwait(true);
        var rowKey = $"AE{Guid.NewGuid():N}"[..12];

        // Draft: автор пише й подає.
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(author, s, rowKey, column, 10m, null)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));
        Assert.Single(await SnapshotsAsync(s).ConfigureAwait(true));

        // Матриця на кроці 1: автор, сторонній і погоджувач ДРУГОГО кроку — 403 DocumentSubmitted.
        foreach (var (client, who) in new[] { (author, "автор"), (outsiderClient, "сторонній"), (secondClient, "погоджувач іншого кроку") })
        {
            var denied = await ExpectAsync(
                    app, HttpStatusCode.Forbidden,
                    PatchAsync(client, s, rowKey, column, 99m, await RowVersionAsync(client, s, rowKey).ConfigureAwait(true)))
                .ConfigureAwait(true);
            Assert.True(ReasonOf(denied) == nameof(EditDenyReason.DocumentSubmitted), $"{who}: {denied}");
        }

        Assert.Equal(10m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));

        // Погоджувач поточного (першого) кроку править — аркуш лишається Submitted, журнал — ApproverEdit.
        await ExpectAsync(
                app, HttpStatusCode.OK,
                PatchAsync(firstClient, s, rowKey, column, 11m, await RowVersionAsync(firstClient, s, rowKey).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(11m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));
        Assert.Equal(
            (CellChangeOrigins.ApproverEdit, await UserIdAsync(s, first.Name).ConfigureAwait(true)),
            await LastChangeAsync(s).ConfigureAwait(true));

        // Крок 1 погоджено: новий зріз містить правку погоджувача; черга (і право правити) — у другого.
        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(firstClient, s)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));
        var afterStep = await SnapshotsAsync(s).ConfigureAwait(true);
        Assert.Equal(2, afterStep.Count);
        Assert.Equal(11m, CellValue(afterStep[^1], s));

        var stale = await ExpectAsync(
                app, HttpStatusCode.Forbidden,
                PatchAsync(firstClient, s, rowKey, column, 98m, await RowVersionAsync(firstClient, s, rowKey).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), ReasonOf(stale));

        await ExpectAsync(
                app, HttpStatusCode.OK,
                PatchAsync(secondClient, s, rowKey, column, 12m, await RowVersionAsync(secondClient, s, rowKey).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(
            (CellChangeOrigins.ApproverEdit, await UserIdAsync(s, second.Name).ConfigureAwait(true)),
            await LastChangeAsync(s).ConfigureAwait(true));

        // Остаточне затвердження: зріз — з правкою другого; далі не править ніхто.
        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(secondClient, s)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await StatusAsync(s).ConfigureAwait(true));
        var afterApprove = await SnapshotsAsync(s).ConfigureAwait(true);
        Assert.Equal(3, afterApprove.Count);
        Assert.Equal(12m, CellValue(afterApprove[^1], s));

        var locked = await ExpectAsync(
                app, HttpStatusCode.Forbidden,
                PatchAsync(secondClient, s, rowKey, column, 13m, await RowVersionAsync(secondClient, s, rowKey).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(nameof(EditDenyReason.DocumentApproved), ReasonOf(locked));
        Assert.Equal(12m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-442")]
    public async Task Затвердження_без_правок_погоджувача_нового_зрізу_не_дає()
    {
        // Без маршруту погоджувач — той, кому `CanApprove` дозволяє (грант Approve): затвердження одноетапне.
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit).ConfigureAwait(true);
        var approver = await NewUserAsync(s, GrantLevel.Approve).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var approverClient = await SignInAsync(app, approver.Name).ConfigureAwait(true);

        var column = await NumberColumnCodeAsync(author, s).ConfigureAwait(true);
        var rowKey = $"AN{Guid.NewGuid():N}"[..12];

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(author, s, rowKey, column, 7m, null)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);
        var submitted = await SnapshotsAsync(s).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(approverClient, s)).ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Approved, await StatusAsync(s).ConfigureAwait(true));
        Assert.Equal(submitted, await SnapshotsAsync(s).ConfigureAwait(true));
    }

    /// <summary>
    /// ✎ D-445 (пряме слово людини 24.10; замінює закриття імпорту з боргу AN-129): Excel-імпорт поданого аркуша
    /// відкрито погоджувачу ПОТОЧНОГО кроку — як правку в сітці (D-442). Перегляд показує йому зміну, застосування
    /// пише значення з позначкою <c>ApproverEdit</c> і ставить перерахунок формул саме цього поданого аркуша
    /// (<c>RecalculationSeed.ApproverSheetDefId</c>); аркуш лишається <c>Submitted</c>. Автор, сторонній і погоджувач
    /// іншого кроку — відмова <c>DocumentSubmitted</c> і в перегляді, і в застосуванні плану, побудованого на чернетці.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутації (локально, не комітяться): повернути <c>SubmittedSheetImportGuard.Close</c> у перегляд — червоніє
    /// «перегляд погоджувача: 1 зміна»; у <c>EnsureWorkbookSheetsEditableAsync</c> лишити безумовний
    /// <c>CheckSheetStatus</c> — застосування 403; <c>batchOrigin</c> без <c>Import</c> — журнал <c>Import</c>, червоне;
    /// не передати <c>ApproverEdit</c> у <c>BuildRecalculationSeeds</c> книги — насіння без аркуша, червоне;
    /// прибрати гілку <c>Submitted when IsCurrentApprover</c> у <c>EditRules</c> — перегляд погоджувача без змін.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-445")]
    [Trait("Requirement", "ФВ-4.3")]
    public async Task Імпорт_погоджувача_поточного_кроку_на_поданому_аркуші_пише_з_позначкою_і_перерахунком()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit, "Document.Import").ConfigureAwait(true);
        var first = await NewUserAsync(s, GrantLevel.Approve, "Document.Import").ConfigureAwait(true);
        var second = await NewUserAsync(s, GrantLevel.Approve, "Document.Import").ConfigureAwait(true);
        var outsider = await NewUserAsync(s, GrantLevel.Write, "Document.Import").ConfigureAwait(true);
        await RouteAsync(s, first.RoleId, second.RoleId).ConfigureAwait(true);

        var recalculations = new ConcurrentQueue<string>();
        using var app = App(recalculations, approveDenied: null);
        using var author = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var firstClient = await SignInAsync(app, first.Name).ConfigureAwait(true);
        using var secondClient = await SignInAsync(app, second.Name).ConfigureAwait(true);
        using var outsiderClient = await SignInAsync(app, outsider.Name).ConfigureAwait(true);

        var column = await NumberColumnCodeAsync(author, s).ConfigureAwait(true);
        var rowKey = $"AI{Guid.NewGuid():N}"[..12];

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(author, s, rowKey, column, 10m, null)).ConfigureAwait(true);

        // Чернетка: автор і сторонній (Write) імпорт пишуть — їхні плани застосовні, доки аркуш не подано.
        var authorDraft = await PreviewAsync(app, author, s, Book(s, column, rowKey, 55m)).ConfigureAwait(true);
        var outsiderDraft = await PreviewAsync(app, outsiderClient, s, Book(s, column, rowKey, 55m)).ConfigureAwait(true);
        Assert.Equal(1, authorDraft.GetProperty("changes").GetArrayLength());
        Assert.Equal(1, outsiderDraft.GetProperty("changes").GetArrayLength());

        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);

        // Поданий: автор, сторонній і погоджувач ДРУГОГО кроку — перегляд без змін, відмова «подано».
        foreach (var (client, who) in new[] { (author, "автор"), (outsiderClient, "сторонній"), (secondClient, "погоджувач іншого кроку") })
        {
            var preview = await PreviewAsync(app, client, s, Book(s, column, rowKey, 77m)).ConfigureAwait(true);
            AssertRejected(preview, rowKey, EditDenyReason.DocumentSubmitted, who);
        }

        // Плани чернетки автора і стороннього — відмова під блокуванням аркуша; значення не змінилося.
        foreach (var (client, plan, who) in new[] { (author, authorDraft, "автор"), (outsiderClient, outsiderDraft, "сторонній") })
        {
            var denied = await ExpectAsync(app, HttpStatusCode.Forbidden, ApplyAsync(client, s, plan)).ConfigureAwait(true);
            Assert.True(denied.Contains(nameof(EditDenyReason.DocumentSubmitted), StringComparison.Ordinal), $"{who}: {denied}");
        }

        Assert.Equal(10m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));

        // Погоджувач поточного (першого) кроку: перегляд показує зміну, застосування пише — як правка в сітці.
        var approverPlan = await PreviewAsync(app, firstClient, s, Book(s, column, rowKey, 66m)).ConfigureAwait(true);
        Assert.Equal(1, approverPlan.GetProperty("changes").GetArrayLength());
        Assert.Equal(0, approverPlan.GetProperty("rejected").GetArrayLength());

        recalculations.Clear();
        await ExpectAsync(app, HttpStatusCode.OK, ApplyAsync(firstClient, s, approverPlan)).ConfigureAwait(true);

        Assert.Equal(66m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));
        Assert.Equal(
            (CellChangeOrigins.ApproverEdit, await UserIdAsync(s, first.Name).ConfigureAwait(true)),
            await LastChangeAsync(s).ConfigureAwait(true));

        // Перерахунок формул — з аркушем погоджувача в насінні: лише так каскад пише в поданий аркуш (D-442).
        var seeds = recalculations
            .SelectMany(payload => JsonDocument.Parse(payload).RootElement.GetProperty("Cells").EnumerateArray()
                .Select(cell => cell.TryGetProperty("ApproverSheetDefId", out var sheet) ? sheet.GetInt32() : (int?)null))
            .ToList();
        Assert.True(seeds.Count > 0, "імпорт погоджувача не поставив перерахунку формул");
        Assert.All(seeds, sheet => Assert.Equal(s.Document.SheetDefId, sheet));
    }

    /// <summary>
    /// ✎ D-445: імпорт погоджувача на поданому аркуші — лише поки черга його. Крок погоджено між переглядом і
    /// застосуванням — план колишнього погоджувача відмовляє <c>DocumentSubmitted</c>; аркуш затверджено між
    /// переглядом і застосуванням — <c>DocumentApproved</c>; перегляд затвердженого — відмова.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-445")]
    public async Task Імпорт_на_поданому_після_переходу_черги_і_на_затвердженому_відмовляє()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit).ConfigureAwait(true);
        var first = await NewUserAsync(s, GrantLevel.Approve, "Document.Import").ConfigureAwait(true);
        var second = await NewUserAsync(s, GrantLevel.Approve, "Document.Import").ConfigureAwait(true);
        await RouteAsync(s, first.RoleId, second.RoleId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var firstClient = await SignInAsync(app, first.Name).ConfigureAwait(true);
        using var secondClient = await SignInAsync(app, second.Name).ConfigureAwait(true);

        var column = await NumberColumnCodeAsync(author, s).ConfigureAwait(true);
        var rowKey = $"AQ{Guid.NewGuid():N}"[..12];

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(author, s, rowKey, column, 10m, null)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);

        // Перегляд погоджувача першого кроку — зміна; далі він погоджує крок, і черга переходить до другого.
        var firstPlan = await PreviewAsync(app, firstClient, s, Book(s, column, rowKey, 55m)).ConfigureAwait(true);
        Assert.Equal(1, firstPlan.GetProperty("changes").GetArrayLength());
        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(firstClient, s)).ConfigureAwait(true);

        var stale = await ExpectAsync(app, HttpStatusCode.Forbidden, ApplyAsync(firstClient, s, firstPlan)).ConfigureAwait(true);
        Assert.Contains(nameof(EditDenyReason.DocumentSubmitted), stale, StringComparison.Ordinal);
        Assert.Equal(10m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));

        // Другий крок: перегляд — зміна; аркуш затверджено до застосування — план відмовляє «затверджено».
        var secondPlan = await PreviewAsync(app, secondClient, s, Book(s, column, rowKey, 66m)).ConfigureAwait(true);
        Assert.Equal(1, secondPlan.GetProperty("changes").GetArrayLength());
        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(secondClient, s)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await StatusAsync(s).ConfigureAwait(true));

        var approved = await ExpectAsync(app, HttpStatusCode.Forbidden, ApplyAsync(secondClient, s, secondPlan)).ConfigureAwait(true);
        Assert.Contains(nameof(EditDenyReason.DocumentApproved), approved, StringComparison.Ordinal);
        Assert.Equal(10m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));

        var afterApprove = await PreviewAsync(app, secondClient, s, Book(s, column, rowKey, 77m)).ConfigureAwait(true);
        AssertRejected(afterApprove, rowKey, EditDenyReason.DocumentApproved, "погоджувач після затвердження");
    }

    /// <summary>
    /// ✎ D-445: «чия черга» під блокуванням аркуша — та сама повторна перевірка (<c>CanApproveAsync</c>), що й у
    /// правки в сітці. Рішення зрізу ще дає погоджувачеві запис, а перевірка під блокуванням каже «вже не його»
    /// (крок погоджено в ту саму мить) — застосування відмовляє <c>DocumentSubmitted</c>, нічого не записано.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: у <c>EnsureWorkbookSheetsEditableAsync</c> не кликати <c>StillCurrentApproverAsync</c> (довіряти
    /// <c>ByApprover</c>) — значення записано, тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-445")]
    public async Task Імпорт_погоджувача_перевіряє_чергу_під_блокуванням_аркуша()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit).ConfigureAwait(true);
        var approver = await NewUserAsync(s, GrantLevel.Approve, "Document.Import").ConfigureAwait(true);
        await RouteAsync(s, approver.RoleId).ConfigureAwait(true);

        var approveDenied = new StrongBox<bool>(false);
        using var app = App(recalculations: null, approveDenied);
        using var author = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var approverClient = await SignInAsync(app, approver.Name).ConfigureAwait(true);

        var column = await NumberColumnCodeAsync(author, s).ConfigureAwait(true);
        var rowKey = $"AL{Guid.NewGuid():N}"[..12];

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(author, s, rowKey, column, 10m, null)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, WorkflowAsync(author, s, "submit")).ConfigureAwait(true);

        var plan = await PreviewAsync(app, approverClient, s, Book(s, column, rowKey, 55m)).ConfigureAwait(true);
        Assert.Equal(1, plan.GetProperty("changes").GetArrayLength());

        approveDenied.Value = true;
        var denied = await ExpectAsync(app, HttpStatusCode.Forbidden, ApplyAsync(approverClient, s, plan)).ConfigureAwait(true);
        Assert.Contains(nameof(EditDenyReason.DocumentSubmitted), denied, StringComparison.Ordinal);
        Assert.Equal(10m, await StoredValueAsync(s, rowKey).ConfigureAwait(true));
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(s).ConfigureAwait(true));
    }

    private static void AssertRejected(JsonElement preview, string rowKey, EditDenyReason reason, string who)
    {
        Assert.True(preview.GetProperty("changes").GetArrayLength() == 0, $"{who}: {preview}");
        var rejected = Assert.Single(preview.GetProperty("rejected").EnumerateArray());
        Assert.Equal(rowKey, rejected.GetProperty("rowKey").GetString());
        Assert.Equal("ECR-ACCS-0403", rejected.GetProperty("reasonCode").GetString());
        Assert.True(rejected.GetProperty("messageKey").GetString() == $"deny.{reason}", $"{who}: {rejected}");
    }

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, Scenario s, JsonElement preview)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/import/apply", UriKind.Relative),
            new { previewToken = preview.GetProperty("previewToken").GetString() });

    /// <summary>
    /// Застосунок із записом постановок перерахунку формул (<paramref name="recalculations"/>) і перемикачем
    /// «<c>CanApproveAsync</c> відмовляє» (<paramref name="approveDenied"/>); <c>null</c> — без підміни.
    /// </summary>
    private EcrApiFactory App(ConcurrentQueue<string>? recalculations, StrongBox<bool>? approveDenied)
        => new(sql)
        {
            ExtraServices = services =>
            {
                if (recalculations is not null)
                {
                    Decorate<IBackgroundJobScheduler>(services, target =>
                    {
                        var proxy = DispatchProxy.Create<IBackgroundJobScheduler, RecordingSchedulerProxy>();
                        var recorder = (RecordingSchedulerProxy)(object)proxy;
                        recorder.Target = target;
                        recorder.Recalculations = recalculations;
                        return proxy;
                    });
                }

                if (approveDenied is not null)
                {
                    Decorate<IAccessDecisionService>(services, target =>
                    {
                        var proxy = DispatchProxy.Create<IAccessDecisionService, ApproveSwitchProxy>();
                        var gate = (ApproveSwitchProxy)(object)proxy;
                        gate.Target = target;
                        gate.Denied = approveDenied;
                        return proxy;
                    });
                }
            },
        };

    private static void Decorate<T>(IServiceCollection services, Func<T, T> wrap)
        where T : class
    {
        var original = services.Last(d => d.ServiceType == typeof(T));
        services.Remove(original);
        services.Add(ServiceDescriptor.Describe(
            typeof(T),
            sp => wrap(original.ImplementationFactory is { } factory
                ? (T)factory(sp)
                : (T)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)),
            original.Lifetime));
    }

    private static object? Forward(MethodInfo method, object target, object?[]? args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>Пересилає все справжньому планувальнику; payload постановок перерахунку формул — у чергу тесту.</summary>
    public class RecordingSchedulerProxy : DispatchProxy
    {
        public IBackgroundJobScheduler Target { get; set; } = null!;

        public ConcurrentQueue<string> Recalculations { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod is { Name: nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync), IsGenericMethod: true }
                && targetMethod.GetGenericArguments()[0] == typeof(IFormulaRecalculationJob)
                && args is [_, { } payload, ..])
            {
                Recalculations.Enqueue(JsonSerializer.Serialize(payload));
            }

            return Forward(targetMethod, Target, args);
        }
    }

    /// <summary>Пересилає все справжній службі доступу; <c>CanApproveAsync</c> відмовляє, поки ввімкнено перемикач.</summary>
    public class ApproveSwitchProxy : DispatchProxy
    {
        public IAccessDecisionService Target { get; set; } = null!;

        public StrongBox<bool> Denied { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            return Denied.Value && targetMethod.Name == nameof(IAccessDecisionService.CanApproveAsync)
                ? Task.FromResult(EditDecision.Deny(EditDenyReason.NoGrant))
                : Forward(targetMethod, Target, args);
        }
    }

    private static async Task<JsonElement> PreviewAsync(EcrApiFactory app, HttpClient client, Scenario s, byte[] book)
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(book);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "book.xlsx");

        var body = await ExpectAsync(
                app, HttpStatusCode.OK,
                client.PostAsync(
                    new Uri($"/api/v1/documents/{s.Document.DocumentId}/import/preview", UriKind.Relative), content))
            .ConfigureAwait(false);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Книга з одним рядком (<paramref name="rowKey"/>) і одним значенням числової колонки.</summary>
    private static byte[] Book(Scenario s, string columnCode, string rowKey, decimal value)
    {
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("MAIN").Cell(2, 1).Value = value;

        var block = new ExcelTableBlock(
            s.Document.TableInstanceId, s.Document.TableDefId, "MAIN_FILE", "MAIN", HeaderRow: 1,
            Columns: [new ExcelColumnRef(s.Document.ColumnDefIds[1], columnCode, 1, false, null)],
            Rows: [new ExcelRowRef(rowKey, 2)]);

        var map = new ExcelWorkbookMap(s.Document.DocumentId, s.PeriodKey, s.Document.TemplateVersionId, [block]);
        workbook.Worksheets.Add(ExcelWorkbookMap.SheetName).Cell(1, 1).Value = JsonSerializer.Serialize(map, MapJson);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(TestDocumentBuilder Builder, TestDocument Document, int PeriodKey);

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == expected,
            $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");

        return body;
    }

    private static string ReasonOf(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        if (root.TryGetProperty("reason", out var reason))
        {
            return reason.GetString() ?? string.Empty;
        }

        return root.TryGetProperty("details", out var details) && details.TryGetProperty("reason", out var inner)
            ? inner.GetString() ?? string.Empty
            : $"немає reason у відповіді: {body}";
    }

    private static Task<HttpResponseMessage> WorkflowAsync(HttpClient client, Scenario s, string action)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/{action}", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey });

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Scenario s)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/approve", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey, approved = true, reason = (string?)null });

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client, Scenario s, string rowKey, string columnCode, decimal value, string? baseVersion)
        => client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = s.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion,
                        cells = new object[] { new { columnCode, value = (object)value.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
                    },
                },
            });

    private static async Task<string> RowVersionAsync(HttpClient client, Scenario s, string rowKey)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative))
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"GET зрізу: {response.StatusCode}\n{text}");

        foreach (var row in JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray())
        {
            if (string.Equals(row.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal))
            {
                return row.GetProperty("rowVersion").GetString()!;
            }
        }

        Assert.Fail($"Рядка «{rowKey}» немає у зрізі.\n{text}");
        return string.Empty;
    }

    private static async Task<string> NumberColumnCodeAsync(HttpClient client, Scenario s)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative))
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"GET зрізу: {response.StatusCode}\n{text}");

        return JsonDocument.Parse(text).RootElement.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ElementAt(1);
    }

    private static decimal CellValue(string payload, Scenario s)
    {
        var cell = Assert.Single(
            SubmissionPayload.Read(payload),
            c => c.Column == s.Document.ColumnDefIds[1] && c.Value is not null);

        return decimal.Parse(cell.Value!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<DocumentStatus?> StatusAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();

        return await db.ApprovalStates.AsNoTracking()
            .Where(a => a.DocumentId == s.Document.DocumentId
                        && a.SheetDefId == s.Document.SheetDefId
                        && a.PeriodKey == s.PeriodKey)
            .Select(a => (DocumentStatus?)a.Status)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    private async Task<decimal?> StoredValueAsync(Scenario s, string rowKey)
    {
        await using var db = s.Builder.CreateContext();

        return await (
                from c in db.CellValues.AsNoTracking()
                join r in db.TableRows.AsNoTracking()
                    on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
                where c.PeriodKeyValue == s.PeriodKey
                      && c.ColumnDefId == s.Document.ColumnDefIds[1]
                      && r.TableInstanceId == s.Document.TableInstanceId
                      && r.RowKeyValue == rowKey
                select c.ValueNumeric)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Походження й автор ОСТАННЬОГО запису журналу числової колонки документа.</summary>
    private async Task<(string Origin, int UserId)> LastChangeAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();

        var row = await db.Database
            .SqlQuery<ChangeRow>(
                $"""
                SELECT TOP (1) Origin, ChangedByUserId FROM aud.CellChange
                WHERE DocumentId = {s.Document.DocumentId} AND ColumnDefId = {s.Document.ColumnDefIds[1]}
                  AND PeriodKey = {s.PeriodKey}
                ORDER BY Id DESC
                """)
            .SingleAsync()
            .ConfigureAwait(false);

        return (row.Origin, row.ChangedByUserId);
    }

    private sealed record ChangeRow(string Origin, int ChangedByUserId);

    private async Task<int> UserIdAsync(Scenario s, string name)
    {
        await using var db = s.Builder.CreateContext();

        return await db.Users.AsNoTracking().Where(u => u.UserName == name).Select(u => u.Id).SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Зрізи подання аркуша (<c>calc.SubmissionSnapshot</c>) за порядком створення.</summary>
    private async Task<List<string>> SnapshotsAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();

        return await db.Database
            .SqlQuery<string>(
                $"""
                SELECT PayloadJson AS Value FROM calc.SubmissionSnapshot
                WHERE DocumentId = {s.Document.DocumentId} AND SheetDefId = {s.Document.SheetDefId}
                  AND PeriodKey = {s.PeriodKey}
                ORDER BY Id
                """)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    private async Task RouteAsync(Scenario s, params int[] roleIds)
    {
        await using var db = s.Builder.CreateContext();
        var route = new ApprovalRoute(
            EcrCode.Create($"D442_{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-442 route" }),
            s.Document.ProjectId);
        foreach (var roleId in roleIds)
        {
            route.AddStep(roleId);
        }

        db.ApprovalRoutes.Add(route);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Користувач із власною роллю: грант <paramref name="level"/> на проєкт документа.</summary>
    private async Task<(string Name, int RoleId)> NewUserAsync(Scenario s, GrantLevel level, params string[] extraPermissions)
    {
        var name = $"d442_{Guid.NewGuid():N}"[..20];

        await using var db = s.Builder.CreateContext();
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-442" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        foreach (var permission in extraPermissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Document.ProjectId, level));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (name, role.Id);
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>
    /// Документ у ВІДКРИТОМУ поточному періоді активного проєкту (як у <c>RejectedSheetHttpFlowTests</c>:
    /// інакше <c>PeriodStateJob</c> на старті застосунку закрив би минулий місяць).
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder
            .BuildAsync(periodKey, columnCount: 3, rowCount: 2, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(builder, document, periodKey);
    }
}
