// tests/Ecr.Api.Tests/SubmitTwiceHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Security;
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
using Xunit.Abstractions;

namespace Ecr.Api.Tests;

/// <summary>
/// Подвійне «Подати» / «Затвердити» наскрізно через HTTP: клієнт може надіслати другий запит із діалогу
/// підтвердження, що вже закривається. Сервер мусить прийняти рівно один: другий — відмова
/// (<c>403 ECR-ACCS-0403</c> або запасний <c>409 ECR-DOC-0409</c>), ніколи не 5xx; у базі — рівно один зріз
/// подання (<c>calc.SubmissionSnapshot</c>) і рівно одна подія журналу переходів (<c>wf.ApprovalEvent</c>).
/// </summary>
/// <remarks>
/// ⚠ Як зроблено «справді одночасно». Обидва запити йдуть справжнім конвеєром; над <see cref="ISheetEditGate"/>
/// стоїть прозора обгортка тесту, яка перед винятковим блокуванням аркуша (<c>EnterSubmitAsync</c> — ним
/// користуються і подання, і затвердження) чекає, доки ДРУГИЙ запит дійде до того самого місця. Тобто обидва
/// вже пройшли всі перевірки до транзакції (для затвердження — і швидку перевірку права) і стоять у відкритих
/// транзакціях; далі блокування бере один, другий чекає його коміту. Обгортка нічого не послаблює: вона лише
/// затримує виклик і пересилає його справжньому блокуванню. Що перекриття справді було — окремий асерт.
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitTwiceHttpTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const string Password = "Submit-Twice-PE!";

    /// <summary>Тестовий період — у 2099 році (чужих даних там немає).</summary>
    private const int Period = 209903;

    private const string AccessDenied = "ECR-ACCS-0403";
    private const string WrongState = "ECR-DOC-0409";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.7")]
    public async Task Повторне_подання_поданого_аркуша_відмовляє_без_другого_зрізу_і_другої_події()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, submitter).ConfigureAwait(true);

        var first = await SendAsync(SubmitAsync(author, s)).ConfigureAwait(true);
        Assert.True(first.Status == HttpStatusCode.NoContent, $"перше подання: {first}\n{app.ErrorsText}");

        var second = await SendAsync(SubmitAsync(author, s)).ConfigureAwait(true);

        Assert.True(second.Status == HttpStatusCode.Forbidden, $"друге подання: {second}\n{app.ErrorsText}");
        Assert.Equal(AccessDenied, second.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.submitDenied", second.MessageKey);
        Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), second.Reason);

        await AssertSubmittedOnceAsync(s, DocumentStatus.Submitted).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.7")]
    public async Task Два_одночасні_подання_одного_користувача_проходить_рівно_одне()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit).ConfigureAwait(true);

        var meeting = new Meeting();
        using var app = App(meeting);
        using var tabOne = await SignInAsync(app, submitter).ConfigureAwait(true);
        using var tabTwo = await SignInAsync(app, submitter).ConfigureAwait(true);

        meeting.Arm(s.Document.DocumentId);
        var answers = await Task.WhenAll(
            Task.Run(() => SendAsync(SubmitAsync(tabOne, s))),
            Task.Run(() => SendAsync(SubmitAsync(tabTwo, s)))).ConfigureAwait(true);
        meeting.Disarm();

        AssertExactlyOneWins(answers, "err.ECR-ACCS-0403.submitDenied", "err.ECR-DOC-0409.submitWrongState", app);
        Assert.True(meeting.Overlapped, "подання не перекрилися: другий запит не дійшов до блокування аркуша");

        await AssertSubmittedOnceAsync(s, DocumentStatus.Submitted).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.13")]
    public async Task Повторне_затвердження_затвердженого_аркуша_відмовляє_без_другої_події()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit).ConfigureAwait(true);
        var approver = await NewUserAsync(s, GrantLevel.Approve).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, submitter).ConfigureAwait(true);
        using var approverClient = await SignInAsync(app, approver).ConfigureAwait(true);

        var submitted = await SendAsync(SubmitAsync(author, s)).ConfigureAwait(true);
        Assert.True(submitted.Status == HttpStatusCode.NoContent, $"подання: {submitted}\n{app.ErrorsText}");

        var first = await SendAsync(ApproveAsync(approverClient, s)).ConfigureAwait(true);
        Assert.True(first.Status == HttpStatusCode.NoContent, $"перше затвердження: {first}\n{app.ErrorsText}");

        var second = await SendAsync(ApproveAsync(approverClient, s)).ConfigureAwait(true);

        Assert.True(second.Status == HttpStatusCode.Forbidden, $"друге затвердження: {second}\n{app.ErrorsText}");
        Assert.Equal(AccessDenied, second.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.approveDenied", second.MessageKey);

        // Причина — загальна `BusinessRule` («аркуш у стані Approved, а не Submitted», `EditRules.CanApprove`).
        Assert.Equal(nameof(EditDenyReason.BusinessRule), second.Reason);

        await AssertApprovedOnceAsync(s).ConfigureAwait(true);
    }

    /// <summary>
    /// Два затвердження разом: той самий погоджувач із двох вкладок і двоє різних погоджувачів того самого кроку
    /// (без маршруту — кожен, кому грант <c>Approve</c> дозволяє).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.13")]
    public async Task Два_одночасні_затвердження_проходить_рівно_одне(bool differentApprovers)
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(s, GrantLevel.Submit).ConfigureAwait(true);
        var approverOne = await NewUserAsync(s, GrantLevel.Approve).ConfigureAwait(true);
        var approverTwo = differentApprovers
            ? await NewUserAsync(s, GrantLevel.Approve).ConfigureAwait(true)
            : approverOne;

        var meeting = new Meeting();
        using var app = App(meeting);
        using var author = await SignInAsync(app, submitter).ConfigureAwait(true);
        using var tabOne = await SignInAsync(app, approverOne).ConfigureAwait(true);
        using var tabTwo = await SignInAsync(app, approverTwo).ConfigureAwait(true);

        var submitted = await SendAsync(SubmitAsync(author, s)).ConfigureAwait(true);
        Assert.True(submitted.Status == HttpStatusCode.NoContent, $"подання: {submitted}\n{app.ErrorsText}");

        meeting.Arm(s.Document.DocumentId);
        var answers = await Task.WhenAll(
            Task.Run(() => SendAsync(ApproveAsync(tabOne, s))),
            Task.Run(() => SendAsync(ApproveAsync(tabTwo, s)))).ConfigureAwait(true);
        meeting.Disarm();

        AssertExactlyOneWins(answers, "err.ECR-ACCS-0403.approveDenied", "err.ECR-DOC-0409.approveWrongState", app);
        Assert.True(meeting.Overlapped, "затвердження не перекрилися: другий запит не дійшов до блокування аркуша");

        await AssertApprovedOnceAsync(s).ConfigureAwait(true);
    }

    // ───────────────────────────── асерти ─────────────────────────────

    /// <summary>Рівно одна відповідь 204; друга — 403 <c>ECR-ACCS-0403</c> або 409 <c>ECR-DOC-0409</c>, не 5xx.</summary>
    private void AssertExactlyOneWins(Answer[] answers, string deniedKey, string wrongStateKey, EcrApiFactory app)
    {
        var all = $"{answers[0]}\n{answers[1]}\n{app.ErrorsText}";

        // Котра з двох відмов випала цього разу — у вивід тесту (обидві дозволені, залежить від моменту).
        foreach (var a in answers)
        {
            output.WriteLine($"RACE {(int)a.Status} {a.ErrorCode} {a.MessageKey} {a.Reason}");
        }

        Assert.True(answers.All(a => (int)a.Status < 500), $"5xx на одночасних запитах:\n{all}");
        Assert.True(answers.Count(a => a.Status == HttpStatusCode.NoContent) == 1, $"успішних має бути рівно один:\n{all}");

        var refused = answers.Single(a => a.Status != HttpStatusCode.NoContent);
        Assert.True(
            (refused.Status == HttpStatusCode.Forbidden && refused.ErrorCode == AccessDenied && refused.MessageKey == deniedKey)
            || (refused.Status == HttpStatusCode.Conflict && refused.ErrorCode == WrongState && refused.MessageKey == wrongStateKey),
            $"відмова не 403 {AccessDenied} і не 409 {WrongState}:\n{all}");
    }

    private static async Task AssertSubmittedOnceAsync(Scenario s, DocumentStatus expected)
    {
        var (status, snapshots, events) = await TraceAsync(s).ConfigureAwait(false);

        Assert.Equal(expected, status);
        Assert.Equal(1, snapshots);
        Assert.Equal([ApprovalAction.Submit], events);
    }

    private static async Task AssertApprovedOnceAsync(Scenario s)
    {
        var (status, snapshots, events) = await TraceAsync(s).ConfigureAwait(false);

        Assert.Equal(DocumentStatus.Approved, status);

        // Погоджувач аркуша не правив — затвердження нового зрізу не знімає (D-442): лишається зріз подання.
        Assert.Equal(1, snapshots);
        Assert.Equal([ApprovalAction.Submit, ApprovalAction.Approve], events);
    }

    /// <summary>Стан аркуша, число зрізів і дії журналу переходів (за порядком запису) — для аркуша × періоду.</summary>
    private static async Task<(DocumentStatus? Status, int Snapshots, List<ApprovalAction> Events)> TraceAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();

        var status = await db.ApprovalStates.AsNoTracking()
            .Where(a => a.DocumentId == s.Document.DocumentId
                        && a.SheetDefId == s.Document.SheetDefId
                        && a.PeriodKey == Period)
            .Select(a => (DocumentStatus?)a.Status)
            .SingleOrDefaultAsync()
            .ConfigureAwait(false);

        var snapshots = await db.SubmissionSnapshots.AsNoTracking()
            .CountAsync(x => x.DocumentId == s.Document.DocumentId
                             && x.SheetDefId == s.Document.SheetDefId
                             && x.PeriodKey == Period)
            .ConfigureAwait(false);

        var events = await db.ApprovalEvents.AsNoTracking()
            .Where(e => e.DocumentId == s.Document.DocumentId
                        && e.SheetDefId == s.Document.SheetDefId
                        && e.PeriodKey == Period)
            .OrderBy(e => e.Id)
            .Select(e => e.Action)
            .ToListAsync()
            .ConfigureAwait(false);

        return (status, snapshots, events);
    }

    // ───────────────────────────── запити ─────────────────────────────

    /// <summary>Відповідь сервера: код, поля тіла помилки й саме тіло — для повідомлення асерту.</summary>
    private sealed record Answer(HttpStatusCode Status, string? ErrorCode, string? MessageKey, string? Reason, string Body)
    {
        public override string ToString() => $"{(int)Status} {Body}";
    }

    private static async Task<Answer> SendAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (response.IsSuccessStatusCode || body.Length == 0 || body[0] != '{')
        {
            return new Answer(response.StatusCode, null, null, null, body);
        }

        var root = JsonDocument.Parse(body).RootElement;

        return new Answer(
            response.StatusCode, Text(root, "errorCode"), Text(root, "messageKey"),
            Text(root, "reason") ?? (root.TryGetProperty("details", out var details) ? Text(details, "reason") : null),
            body);
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Scenario s)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/submit", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = Period });

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Scenario s)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/approve", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = Period, approved = true, reason = (string?)null });

    // ───────────────────────────── зустріч двох запитів ─────────────────────────────

    /// <summary>
    /// Місце зустрічі двох запитів перед винятковим блокуванням аркуша: перший чекає на другого (не довше
    /// <see cref="Patience"/>), далі обидва йдуть до справжнього блокування.
    /// </summary>
    public sealed class Meeting
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long documentId;
        private int arrived;

        /// <summary>Обидва запити дійшли до блокування, поки жоден його ще не взяв.</summary>
        public bool Overlapped => both.Task.IsCompletedSuccessfully;

        public void Arm(long document) => Interlocked.Exchange(ref documentId, document);

        public void Disarm() => Interlocked.Exchange(ref documentId, 0);

        public bool Watches(long document) => Interlocked.Read(ref documentId) == document;

        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref arrived) >= 2)
            {
                both.TrySetResult();
            }

            try
            {
                await both.Task.WaitAsync(Patience).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Другий запит не прийшов: перший іде далі сам, а тест упаде на асерті `Overlapped`.
            }
        }
    }

    /// <summary>Пересилає все справжньому блокуванню; <c>EnterSubmitAsync</c> документа тесту — після зустрічі.</summary>
    public class MeetingGateProxy : DispatchProxy
    {
        public ISheetEditGate Target { get; set; } = null!;

        public Meeting Meeting { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            return targetMethod.Name == nameof(ISheetEditGate.EnterSubmitAsync)
                   && args is [long document, ..]
                   && Meeting.Watches(document)
                ? MeetThenEnterAsync(targetMethod, args)
                : Forward(targetMethod, args);
        }

        private async Task MeetThenEnterAsync(MethodInfo method, object?[]? args)
        {
            await Meeting.ArriveAsync().ConfigureAwait(false);
            await ((Task)Forward(method, args)!).ConfigureAwait(false);
        }

        private object? Forward(MethodInfo method, object?[]? args)
        {
            try
            {
                return method.Invoke(Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    private EcrApiFactory App(Meeting meeting)
        => new(sql)
        {
            ExtraServices = services =>
            {
                var original = services.Last(d => d.ServiceType == typeof(ISheetEditGate));
                services.Remove(original);
                services.Add(ServiceDescriptor.Describe(
                    typeof(ISheetEditGate),
                    sp =>
                    {
                        var target = original.ImplementationFactory is { } factory
                            ? (ISheetEditGate)factory(sp)
                            : (ISheetEditGate)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
                        var proxy = DispatchProxy.Create<ISheetEditGate, MeetingGateProxy>();
                        var gate = (MeetingGateProxy)(object)proxy;
                        gate.Target = target;
                        gate.Meeting = meeting;
                        return proxy;
                    },
                    original.Lifetime));
            },
        };

    // ───────────────────────────── підготовка ─────────────────────────────

    private sealed record Scenario(TestDocumentBuilder Builder, TestDocument Document);

    /// <summary>Користувач із власною роллю: <c>Document.View</c> і грант <paramref name="level"/> на проєкт документа.</summary>
    private static async Task<string> NewUserAsync(Scenario s, GrantLevel level)
    {
        var name = $"st_{Guid.NewGuid():N}"[..20];

        await using var db = s.Builder.CreateContext();
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Submit twice" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Document.ProjectId, level));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>Документ у ВІДКРИТОМУ періоді 2099 року активного проєкту, з аркушем у складі документа.</summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder
            .BuildAsync(Period, columnCount: 3, rowCount: 2, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == Period)
            .ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(builder, document);
    }
}
