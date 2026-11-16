// tests/Ecr.Api.Tests/Security/WriteRequiresViewPermissionTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// PT-8 (пентест 2026-11): запис у документ проходить ТОЙ САМИЙ вхід, що й читання зрізу, — видимість документа І
/// право <c>Document.View</c> у його проєкті. Роль із грантом <c>Write</c> на проєкт, але без <c>Document.View</c>,
/// не пише комірок «наосліп» і не читає чужих значень через відмову <c>409 ECR-CELL-0409</c>.
/// </summary>
/// <remarks>
/// Пара Allow/Deny на кожній дії: <c>blind</c> (грант Write, права немає) — 403 <c>ECR-AUTH-0403</c>;
/// <c>sighted</c> (той самий грант + <c>Document.View</c>) — проходить ворота (відповідь уже від самої дії).
/// ⛔ Без виправлення: застарілий PATCH від <c>blind</c> — 409 з <c>theirValue</c>; оформлення — 200; рядок — 201.
/// Фільтри доступу тест не змінює.
/// </remarks>
[Collection("SqlServer")]
public sealed class WriteRequiresViewPermissionTests(SqlServerFixture sql)
{
    private const string Password = "Write-Needs-View-2026!";

    /// <summary>Значення, яке не може випадково збігтися ні з чим у відповіді.</summary>
    private const decimal Secret = 424242.5m;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PT-8")]
    public async Task PATCH_комірок_без_Document_View_403_і_не_віддає_чужих_значень()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        using (var blind = await SignedInAsync(app, s.Blind).ConfigureAwait(true))
        {
            var (status, body) = await StalePatchAsync(blind, s).ConfigureAwait(true);

            Assert.True(status == HttpStatusCode.Forbidden, $"{status}: {body}\n{app.ErrorsText}");
            var json = JsonDocument.Parse(body).RootElement;
            Assert.Equal("ECR-AUTH-0403", json.GetProperty("errorCode").GetString());
            Assert.False(json.TryGetProperty("conflicts", out _), body);
            Assert.DoesNotContain(Digits(Secret), body, StringComparison.Ordinal);
        }

        // Контроль: той самий грант + Document.View — ворота пройдено, відповідає сама дія (конфлікт версії зі значенням).
        using (var sighted = await SignedInAsync(app, s.Sighted).ConfigureAwait(true))
        {
            var (status, body) = await StalePatchAsync(sighted, s).ConfigureAwait(true);

            Assert.True(status == HttpStatusCode.Conflict, $"{status}: {body}\n{app.ErrorsText}");
            Assert.Contains(Digits(Secret), body, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PT-8")]
    public async Task Оформлення_і_рядки_без_Document_View_403_а_з_правом_ворота_пройдено()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var doc = s.Doc.DocumentId;
        var instance = s.Doc.TableInstanceId;

        using (var blind = await SignedInAsync(app, s.Blind).ConfigureAwait(true))
        {
            await AssertForbiddenAsync(app, await StylesAsync(blind, s).ConfigureAwait(true)).ConfigureAwait(true);
            await AssertForbiddenAsync(app, await blind.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{doc}/rows", UriKind.Relative),
                new { tableInstanceId = instance }).ConfigureAwait(true)).ConfigureAwait(true);
            await AssertForbiddenAsync(app, await blind.DeleteAsync(
                new Uri($"/api/v1/documents/{doc}/tables/{instance}/rows/{s.RowKey}", UriKind.Relative))
                .ConfigureAwait(true)).ConfigureAwait(true);

            // Поділ значення за дозволами — теж запис у документ.
            await AssertForbiddenAsync(app, await SplitsAsync(blind, s).ConfigureAwait(true)).ConfigureAwait(true);

            // Історія дозволів рядка й шапки (додати / скасувати) — теж запис у документ.
            var history = $"/api/v1/documents/{doc}/permit-history";
            await AssertNeedsViewAsync(app, await blind.PostAsJsonAsync(
                new Uri($"{history}/rows", UriKind.Relative),
                new { tableInstanceId = instance, rowKey = s.RowKey, columnCode = s.ColumnCodes[1], registryEntryId = 1L, validFrom = "2099-01-15" })
                .ConfigureAwait(true)).ConfigureAwait(true);
            await AssertNeedsViewAsync(app, await blind.PostAsJsonAsync(
                new Uri($"{history}/rows/undo", UriKind.Relative),
                new { tableInstanceId = instance, rowKey = s.RowKey, columnCode = s.ColumnCodes[1] })
                .ConfigureAwait(true)).ConfigureAwait(true);
            await AssertNeedsViewAsync(app, await blind.PostAsJsonAsync(
                new Uri($"{history}/header", UriKind.Relative),
                new { fieldCode = "ANY", registryEntryId = 1L, validFrom = "2099-01-15", periodKey = s.Doc.PeriodKey.Value })
                .ConfigureAwait(true)).ConfigureAwait(true);
            await AssertNeedsViewAsync(app, await blind.PostAsJsonAsync(
                new Uri($"{history}/header/undo", UriKind.Relative), new { fieldCode = "ANY" })
                .ConfigureAwait(true)).ConfigureAwait(true);

            // Огляд класу PT-8: читання історії дозволів і переліку блокувань, оновлення таблиці з дозволу.
            await AssertNeedsViewAsync(app, await blind.GetAsync(
                new Uri($"{history}?periodKey={s.Doc.PeriodKey.Value}", UriKind.Relative)).ConfigureAwait(true)).ConfigureAwait(true);
            await AssertNeedsViewAsync(app, await blind.GetAsync(
                new Uri($"/api/v1/documents/{doc}/row-locks?periodKey={s.Doc.PeriodKey.Value}", UriKind.Relative))
                .ConfigureAwait(true)).ConfigureAwait(true);
            await AssertNeedsViewAsync(app, await blind.PostAsync(
                new Uri($"/api/v1/documents/{doc}/permit-bindings/ANY/refresh?periodKey={s.Doc.PeriodKey.Value}", UriKind.Relative),
                content: null).ConfigureAwait(true)).ConfigureAwait(true);
        }

        // Контроль для читань і оновлення: з Document.View ворота пройдено (відмовляє вже сама дія, не право).
        using (var sighted = await SignedInAsync(app, s.Sighted).ConfigureAwait(true))
        {
            foreach (var response in new[]
                     {
                         await sighted.GetAsync(new Uri(
                             $"/api/v1/documents/{doc}/permit-history?periodKey={s.Doc.PeriodKey.Value}", UriKind.Relative)).ConfigureAwait(true),
                         await sighted.GetAsync(new Uri(
                             $"/api/v1/documents/{doc}/row-locks?periodKey={s.Doc.PeriodKey.Value}", UriKind.Relative)).ConfigureAwait(true),
                         await sighted.PostAsync(new Uri(
                             $"/api/v1/documents/{doc}/permit-bindings/ANY/refresh?periodKey={s.Doc.PeriodKey.Value}", UriKind.Relative),
                             content: null).ConfigureAwait(true),
                     })
            {
                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                    Assert.DoesNotContain("Document.View", body, StringComparison.Ordinal);
                    Assert.True((int)response.StatusCode < 500, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
                }
            }
        }

        // Контроль: з Document.View жодна з дій не відмовляє «немає права Document.View».
        using (var sighted = await SignedInAsync(app, s.Sighted).ConfigureAwait(true))
        {
            using var styles = await StylesAsync(sighted, s).ConfigureAwait(true);
            var stylesBody = await styles.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(styles.StatusCode == HttpStatusCode.OK, $"{styles.StatusCode}: {stylesBody}\n{app.ErrorsText}");

            using var row = await sighted.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{doc}/rows", UriKind.Relative),
                new { tableInstanceId = instance }).ConfigureAwait(true);
            var rowBody = await row.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.DoesNotContain("Document.View", rowBody, StringComparison.Ordinal);
            Assert.NotEqual(HttpStatusCode.NotFound, row.StatusCode);

            // Поділ: ворота пройдено — відмовляє вже сама дія (сегментів дозволу в документа немає), не право.
            using var splits = await SplitsAsync(sighted, s).ConfigureAwait(true);
            var splitsBody = await splits.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.DoesNotContain("Document.View", splitsBody, StringComparison.Ordinal);
            Assert.NotEqual("ECR-AUTH-0403", JsonDocument.Parse(splitsBody).RootElement.TryGetProperty("errorCode", out var code)
                ? code.GetString() : null);
        }
    }

    /// <summary>
    /// PT-8, виняток для збору: профіль системного записувача (<c>IsIntegrationWriter</c>, без прав ролей) проходить
    /// запис за самою видимістю документа. Цей профіль НЕ досяжний запитом: він виникає лише в контексті фонової
    /// задачі (<c>IntegrationActor.EnterAsync</c>), а технічний запис <c>svc-integration</c> увійти через HTTP не може —
    /// тож читати через відповідь 409 ним неможливо.
    /// </summary>
    /// <remarks>
    /// Контроль у тому ж тесті: всередині задачі той самий запис отримує <c>IsIntegrationWriter = true</c>.
    /// ⛔ Якщо колись профіль запиту (поза задачею) почне отримувати цю ознаку — перша перевірка червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PT-8")]
    public async Task Профіль_збирача_виникає_лише_в_задачі_а_технічний_запис_не_входить_через_HTTP()
    {
        using var app = new EcrApiFactory(sql);
        using var anonymous = app.CreateClient();

        // 1. Вхід технічного запису через HTTP — відмова (пароля в нього немає), з будь-яким паролем.
        foreach (var password in new[] { Password, string.Empty, Ecr.Infrastructure.Jobs.IntegrationActor.UserName })
        {
            using var login = await anonymous.PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName = Ecr.Infrastructure.Jobs.IntegrationActor.UserName, password }).ConfigureAwait(true);
            Assert.False(login.IsSuccessStatusCode, $"svc-integration увійшов через HTTP: {login.StatusCode}");
        }

        // 2. Поза задачею (так будується профіль запиту) ознаки записувача збору немає; у задачі — є.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Ecr.Infrastructure.Persistence.EcrDbContext>();
        var access = scope.ServiceProvider.GetRequiredService<Ecr.Application.Security.IAccessDecisionService>();
        var technicalId = await db.Users.AsNoTracking()
            .Where(u => u.UserName == Ecr.Infrastructure.Jobs.IntegrationActor.UserName)
            .Select(u => u.Id).SingleAsync().ConfigureAwait(true);

        var outside = await access.BuildProfileAsync(technicalId, CancellationToken.None).ConfigureAwait(true);
        Assert.False(outside.IsIntegrationWriter);

        var actor = scope.ServiceProvider.GetRequiredService<Ecr.Infrastructure.Jobs.IntegrationActor>();
        using (await actor.EnterAsync(CancellationToken.None).ConfigureAwait(true))
        {
            var inside = await access.BuildProfileAsync(technicalId, CancellationToken.None).ConfigureAwait(true);
            Assert.True(inside.IsIntegrationWriter);
            Assert.Empty(inside.Permissions);
        }
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(TestDocument Doc, string Blind, string Sighted, string RowKey, IReadOnlyList<string> ColumnCodes);

    private static async Task AssertForbiddenAsync(EcrApiFactory app, HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
            Assert.Equal("ECR-AUTH-0403", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
        }
    }

    /// <summary>403 саме через право: відмова називає <c>Document.View</c> (а не грант, поле чи колонку).</summary>
    private static async Task AssertNeedsViewAsync(EcrApiFactory app, HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
            var json = JsonDocument.Parse(body).RootElement;
            Assert.Equal("ECR-AUTH-0403", json.GetProperty("errorCode").GetString());
            Assert.Contains("Document.View", body, StringComparison.Ordinal);
        }
    }

    private static Task<HttpResponseMessage> SplitsAsync(HttpClient client, Scenario s)
        => client.PutAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/permit-history/splits", UriKind.Relative),
            new
            {
                periodKey = s.Doc.PeriodKey.Value,
                items = new[]
                {
                    new
                    {
                        tableInstanceId = s.Doc.TableInstanceId,
                        rowKey = s.RowKey,
                        columnCode = s.ColumnCodes[1],
                        baseVersion = "0x0000000000000001",
                        parts = new decimal?[] { 1m, 2m },
                    },
                },
            });

    private static Task<HttpResponseMessage> StylesAsync(HttpClient client, Scenario s)
        => client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}/cell-styles", UriKind.Relative),
            new
            {
                periodKey = s.Doc.PeriodKey.Value,
                items = new[] { new { rowKey = s.RowKey, columnCode = s.ColumnCodes[1], backgroundHex = "#FFEE00" } },
            });

    /// <summary><c>PATCH</c> рядка з версією, якої немає: відповідь про конфлікт називає чинне значення.</summary>
    private static async Task<(HttpStatusCode Status, string Body)> StalePatchAsync(HttpClient client, Scenario s)
    {
        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Doc.TableInstanceId,
                periodKey = s.Doc.PeriodKey.Value,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = s.RowKey,
                        baseVersion = "0x0000000000000001",
                        cells = new[] { new { columnCode = s.ColumnCodes[1], value = (object)"1" } },
                    },
                },
            }).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static string Digits(decimal value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>
    /// Документ із значенням у C2 і два користувачі з грантом <c>Write</c> на проєкт: <c>blind</c> — роль без
    /// <c>Document.View</c>, <c>sighted</c> — та сама роль плюс роль із <c>Document.View</c>.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 2).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
        db.CellValues.Add(new CellValue(
            new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]), doc.TableDefId,
            new CellValueData { ValueNumeric = Secret }));

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == doc.PeriodKey.Value).ConfigureAwait(false);
        period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);

        var blind = NewUser("wvb");
        var sighted = NewUser("wvs");
        db.Users.AddRange(blind, sighted);

        var writer = NewRole("WV_W");
        var viewer = NewRole("WV_V");
        db.Roles.AddRange(writer, viewer);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Грант Write на проєкт — без жодного права ролі.
        db.ResourceGrants.Add(new ResourceGrant(writer.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));
        db.RolePermissions.Add(new RolePermission(viewer.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(writer.Id, blind.Id, null));
        db.RoleAssignments.Add(new RoleAssignment(writer.Id, sighted.Id, null));
        db.RoleAssignments.Add(new RoleAssignment(viewer.Id, sighted.Id, null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var codes = await db.ColumnDefs.AsNoTracking()
            .Where(c => doc.ColumnDefIds.Contains(c.Id))
            .OrderBy(c => c.Ordinal)
            .Select(c => c.Code)
            .ToListAsync().ConfigureAwait(false);
        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == doc.RowIds[0])
            .Select(r => r.RowKeyValue)
            .SingleAsync().ConfigureAwait(false);

        return new Scenario(doc, blind.UserName, sighted.UserName, rowKey, codes);
    }

    private static User NewUser(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        return user;
    }

    private static Role NewRole(string prefix)
        => new(
            EcrCode.Create($"{prefix}_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = prefix }));
}
