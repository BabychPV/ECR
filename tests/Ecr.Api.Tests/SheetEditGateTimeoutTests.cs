// tests/Ecr.Api.Tests/SheetEditGateTimeoutTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Правка аркуша, поки хтось тримає виняткове блокування подання, і час
/// очікування вичерпано, — зрозуміла відмова <c>409 ECR-DOC-4091</c>, а не
/// <c>500</c>.
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>SheetEditGate</c> на тайм-аут <c>sp_getapplock</c> кидав голий
/// <c>InvalidOperationException</c>; <c>ExceptionHandlingMiddleware</c> такого не
/// знає і віддавав <c>500 ECR-SYS-0500</c> «зверніться до адміністратора» — на
/// стан, який минає сам, щойно подання закінчиться.
///
/// ⚠ Блокування тримає СПРАВЖНІЙ <c>SheetEditGate</c> у власній транзакції
/// окремого з'єднання — рівно так, як його тримає подання. Ключ ресурсу тест не
/// дублює: інакше тест і код могли б тихо розійтися в рядку ключа, і «правка не
/// дочекалась» перетворилося б на «правка й не чекала».
///
/// ⚠ Тайм-аут скорочено до пів секунди підміною <see cref="SheetEditGatePolicy"/> у
/// контейнері — той самий шов, яким його задає <c>Database:SheetLockTimeoutSeconds</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetEditGateTimeoutTests(SqlServerFixture sql)
{
    private const string Password = "Api-Sheet-Busy-2026!";

    /// <summary>Скорочене очікування блокування — щоб тест не чекав пів хвилини.</summary>
    private static readonly TimeSpan ShortLockTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Пояс майданчика; той самий, який ставить <see cref="TestDocumentBuilder"/>.</summary>
    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Almaty").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Правка_поки_аркуш_подається_довше_за_тайм_аут_дає_409_з_реченням_а_не_500()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var factory = new EcrApiFactory(sql);
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton(new SheetEditGatePolicy(ShortLockTimeout))));
        using var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = scenario.UserName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {factory.ErrorsText}");

        var sliceUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/tables/{scenario.Document.TableInstanceId}",
            UriKind.Relative);
        var opened = await client.GetAsync(sliceUri).ConfigureAwait(true);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}: {factory.ErrorsText}");

        var numberColumn = JsonDocument
            .Parse(await opened.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("columns")
            .EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ElementAt(1);

        // ── Хтось подає аркуш: виняткове блокування взято й тримається ────
        await using var holder = scenario.Builder.CreateContext();
        await using var submitTransaction = await holder.Database.BeginTransactionAsync().ConfigureAwait(true);
        await new SheetEditGate(holder)
            .EnterSubmitAsync(
                scenario.Document.DocumentId, scenario.Document.SheetDefId,
                new PeriodKey(scenario.PeriodKey), CancellationToken.None)
            .ConfigureAwait(true);

        var rowKey = $"BSY{Guid.NewGuid():N}"[..12];

        var patched = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{scenario.Document.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = scenario.Document.TableInstanceId,
                periodKey = scenario.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion = (string?)null,
                        cells = new object[] { new { columnCode = numberColumn, value = (object)"12.5" } },
                    },
                },
            }).ConfigureAwait(true);

        var body = await patched.Content.ReadAsStringAsync().ConfigureAwait(true);

        await submitTransaction.RollbackAsync().ConfigureAwait(true);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ. До виправлення тут був `500 ECR-SYS-0500`.
        Assert.True(
            patched.StatusCode == HttpStatusCode.Conflict,
            $"Правка під утримуваним блокуванням: очікували 409, отримали {patched.StatusCode}\n{body}\n{factory.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-DOC-4091", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-DOC-4091.sheetBeingSubmitted", problem.GetProperty("messageKey").GetString());

        // ⚠ Речення — з КАТАЛОГУ мовою користувача, а не запасне українське
        // серверне: жодної кирилиці й жодного непідставленого плейсхолдера.
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.DoesNotContain('{', detail);
        Assert.DoesNotContain('}', detail);
        Assert.False(
            detail.Any(ch => ch is >= 'Ѐ' and <= 'ӿ'),
            $"У реченні відмови є кирилиця — каталог не спрацював: «{detail}»");
        Assert.Contains("being submitted", detail, StringComparison.Ordinal);

        var title = problem.GetProperty("title").GetString()!;
        Assert.NotEqual("ECR-DOC-4091", title);

        // І нічого не записано: відмова — до першого запису транзакції.
        await using var db = scenario.Builder.CreateContext();
        var stored = await db.CellValues
            .AsNoTracking()
            .CountAsync(c => c.PeriodKeyValue == scenario.PeriodKey
                             && c.ColumnDefId == scenario.Document.ColumnDefIds[1])
            .ConfigureAwait(true);
        Assert.Equal(0, stored);
    }

    /// <summary>Документ, права і стан, за яких запис ДОЗВОЛЕНИЙ — заважає лише блокування.</summary>
    /// <remarks>
    /// ⚠ Та сама підготовка, що в <c>CellWriteRoundTripTests.ArrangeAsync</c>, і
    /// з тих самих причин: кожен крок знімає одну умову з <c>EditRules.CanEdit</c>,
    /// і без будь-якого з них відмова була б законною — не від блокування.
    /// </remarks>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder
            .BuildAsync(periodKey, columnCount: 3, rowCount: 2, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);

        var userName = $"busy_{Guid.NewGuid():N}"[..20];
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"BUSY_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet-busy writer" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(
            new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);

        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(builder, document, userName, periodKey);
    }

    /// <summary>Підготовлений сценарій.</summary>
    private sealed record Scenario(
        TestDocumentBuilder Builder,
        TestDocument Document,
        string UserName,
        int PeriodKey);
}
