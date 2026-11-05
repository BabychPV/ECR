// tests/Ecr.Api.Tests/EditAssignmentsLockRecheckApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// PM-EDITASSIGN-P3 (b): права делегатора звіряються повторно В ТРАНЗАКЦІЇ запису передачі, а не лише в підготовці.
/// </summary>
/// <remarks>
/// Гонитва відтворюється детерміновано: підміна <see cref="IEditAssignmentManagementStore"/> знімає ролі делегатора
/// в момент пошуку одержувачів - це крок <c>PrepareAsync</c> ПІСЛЯ перевірки прав і ДО транзакції <c>CommitAsync</c>.
/// Без повторної перевірки передачу, видану вже пониженим делегатором, записано (200), а одержувачі отримують
/// сповіщення про передачу, яка не діє.
///
/// Мутаційний доказ (локально, не комітиться): прибрати виклик <c>RecheckDelegatorAsync</c> у
/// <c>UpsertEditAssignmentHandler.CommitAsync</c> - червоніє <c>Делегатора_понижено_між_підготовкою_і_записом_…</c>
/// (200 замість 403, передачу записано).
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentsLockRecheckApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Edit-LockRecheck-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PM-EDITASSIGN-P3-b")]
    public async Task Без_пониження_передача_записується()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var hook = new UsersLookupHook();
        using var app = NewApp(hook);
        using var delegator = await SignInAsync(app, s.Delegator.Name).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.OK, PostAsync(delegator, s)).ConfigureAwait(true);

        Assert.Equal(1, await AssignmentCountAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PM-EDITASSIGN-P3-b")]
    public async Task Делегатора_понижено_між_підготовкою_і_записом_403_і_нічого_не_записано()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var hook = new UsersLookupHook();
        using var app = NewApp(hook);
        using var delegator = await SignInAsync(app, s.Delegator.Name).ConfigureAwait(true);

        // Ролі делегатора знімаються рівно між перевіркою прав (PrepareAsync) і транзакцією запису (CommitAsync).
        hook.Arm(() => RemoveRolesAsync(s.Delegator.Id));

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, PostAsync(delegator, s)).ConfigureAwait(true);

        Assert.True(hook.Fired, "підміна не спрацювала - гонитву не відтворено");
        Assert.Equal("err.ECR-ACCS-0403.notEditAssignmentManager", MessageKeyOf(body));
        Assert.Equal(0, await AssignmentCountAsync(s).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Person(int Id, string Name);

    private sealed record Scenario(TestDocument Document, byte Month, Person Delegator, Person Executor);

    private EcrApiFactory NewApp(UsersLookupHook hook)
        => new(sql)
        {
            ExtraServices = services =>
            {
                services.AddScoped<EditAssignmentManagementStore>();
                services.AddScoped<IEditAssignmentManagementStore>(
                    sp => UsersLookupProxy.Wrap(sp.GetRequiredService<EditAssignmentManagementStore>(), hook));
            },
        };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Scenario s)
        => client.PostAsJsonAsync(
            new Uri(
                $"/api/v1/documents/{s.Document.DocumentId.ToString(CultureInfo.InvariantCulture)}/edit-assignments",
                UriKind.Relative),
            new
            {
                slice = new { monthNumber = (int)s.Month },
                recipientUserIds = new[] { s.Executor.Id },
                isExclusive = false,
            });

    private static string MessageKeyOf(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        if (root.TryGetProperty("messageKey", out var key))
        {
            return key.GetString() ?? string.Empty;
        }

        return root.TryGetProperty("details", out var details) && details.TryGetProperty("messageKey", out var inner)
            ? inner.GetString() ?? string.Empty
            : $"немає messageKey у відповіді: {body}";
    }

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == expected,
            $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");

        return body;
    }

    private async Task<int> AssignmentCountAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.EditAssignments.AsNoTracking()
            .CountAsync(a => a.DocumentId == s.Document.DocumentId)
            .ConfigureAwait(false);
    }

    private async Task RemoveRolesAsync(int userId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await db.RoleAssignments.Where(a => a.UserId == userId).ExecuteDeleteAsync().ConfigureAwait(false);
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
    /// Документ у відкритому поточному періоді активного проєкту; числова колонка - місячна колонка поточного місяця;
    /// делегатор з Manage на проєкт (не автор документа), виконавець лише з Read. Передач ще немає.
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

        async Task<Person> NewUserAsync(string prefix, GrantLevel level)
        {
            var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"EL_{Guid.NewGuid():N}"[..20]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Edit assignment lock recheck" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, level));
            await db.SaveChangesAsync().ConfigureAwait(false);

            return new Person(user.Id, name);
        }

        var delegator = await NewUserAsync("eld", GrantLevel.Manage).ConfigureAwait(false);
        var executor = await NewUserAsync("ele", GrantLevel.Read).ConfigureAwait(false);

        var month = (byte)siteToday.Month;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = {month} WHERE Id = {document.ColumnDefIds[1]}")
            .ConfigureAwait(false);

        return new Scenario(document, month, delegator, executor);
    }

    /// <summary>Одноразова дія, що виконується перед першим пошуком одержувачів після <see cref="Arm"/>.</summary>
    private sealed class UsersLookupHook
    {
        private Func<Task>? pending;

        public bool Fired { get; private set; }

        public void Arm(Func<Task> action) => pending = action;

        public async Task RunOnceAsync()
        {
            var action = Interlocked.Exchange(ref pending, null);
            if (action is not null)
            {
                await action().ConfigureAwait(false);
                Fired = true;
            }
        }
    }

    /// <summary>
    /// Прозорий проксі сховища: усе передає справжньому, лише <c>GetUsersAsync</c> спершу виконує зведений гачок.
    /// </summary>
    public class UsersLookupProxy : DispatchProxy
    {
        private IEditAssignmentManagementStore inner = null!;
        private UsersLookupHook hook = null!;

        internal static IEditAssignmentManagementStore Wrap(IEditAssignmentManagementStore inner, object hook)
        {
            var proxy = Create<IEditAssignmentManagementStore, UsersLookupProxy>();
            var self = (UsersLookupProxy)(object)proxy;
            self.inner = inner;
            self.hook = (UsersLookupHook)hook;
            return proxy;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IEditAssignmentManagementStore.GetUsersAsync))
            {
                return GetUsersAsync(targetMethod, args);
            }

            try
            {
                return targetMethod.Invoke(inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private async Task<IReadOnlyDictionary<int, EditAssignmentUserRef>> GetUsersAsync(MethodInfo method, object?[]? args)
        {
            await hook.RunOnceAsync().ConfigureAwait(false);
            return await ((Task<IReadOnlyDictionary<int, EditAssignmentUserRef>>)method.Invoke(inner, args)!).ConfigureAwait(false);
        }
    }
}
