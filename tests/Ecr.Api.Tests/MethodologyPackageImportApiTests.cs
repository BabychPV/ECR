// tests/Ecr.Api.Tests/MethodologyPackageImportApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>POST /api/v1/methodologies/import</c> (крок V, FEATURE-HSE301-VIEW §11.6) через справжній
/// конвеєр і справжню базу: сухий прогін не пише, блокер — 422 без запису, повтор — без змін.
/// </summary>
/// <remarks>
/// Мутаційні докази (кожну зміну внесено окремо → названий тест червоний, після відкату — зелений):
/// прибрати <c>if (dryRun) return …</c> в <c>ImportMethodologyPackageHandler</c> →
/// <see cref="Сухий_прогін_звітує_і_нічого_не_пише"/>;
/// прибрати відмову за <c>plan.Blockers</c> → <see cref="Нерезолвне_посилання_дає_422_зі_звітом_і_без_запису"/>;
/// порівнювати наявну версію завжди як «інший вміст» (<c>SameAs</c> → <c>false</c>) →
/// <see cref="Повторний_імпорт_того_самого_пакета_без_змін_і_без_дублів"/>;
/// прибрати друге право (<c>ConstantPermission</c>) → <see cref="Без_права_на_константи_403"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyPackageImportApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Methodology-Import-2026!";

    private static readonly string[] Permissions =
        ["Calculation.View", "Calculation.EditFormula", "Calculation.EditConstant"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сухий_прогін_звітує_і_нічого_не_пише()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Permissions).ConfigureAwait(true);
        var names = Names();

        var response = await PostAsync(client, Package(names), dryRun: true).ConfigureAwait(true);
        var report = await JsonAsync(response, HttpStatusCode.OK).ConfigureAwait(true);

        Assert.True(report.GetProperty("dryRun").GetBoolean());
        Assert.False(report.GetProperty("applied").GetBoolean());
        Assert.Equal("created", report.GetProperty("outcome").GetString());
        Assert.Equal(2, report.GetProperty("totals").GetProperty("versionsToCreate").GetInt32());
        Assert.Equal(0, report.GetProperty("blockers").GetArrayLength());

        var owner = report.GetProperty("methodologies").EnumerateArray()
            .Single(m => m.GetProperty("code").GetString() == names.Owner);
        Assert.Equal(names.Library, owner.GetProperty("versions")[0].GetProperty("imports")[0].GetString());

        Assert.Equal(0, await MethodologyCountAsync(names).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нерезолвне_посилання_дає_422_зі_звітом_і_без_запису()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Permissions).ConfigureAwait(true);
        var names = Names();

        var response = await PostAsync(client, Package(names, ownerArguments: "@A;!Nowhere"), dryRun: false)
            .ConfigureAwait(true);
        var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);

        Assert.Equal("ECR-CALC-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CALC-0422.methodologyImportBlocked", problem.GetProperty("messageKey").GetString());

        var blocker = problem.GetProperty("report").GetProperty("blockers").EnumerateArray().Single();
        Assert.Equal("unresolvedFormula", blocker.GetProperty("kind").GetString());
        Assert.Equal("!Nowhere", blocker.GetProperty("detail").GetString());

        // ⛔ Усе або нічого: бібліотека в пакеті без блокерів теж не створилася.
        Assert.Equal(0, await MethodologyCountAsync(names).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторний_імпорт_того_самого_пакета_без_змін_і_без_дублів()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Permissions).ConfigureAwait(true);
        var names = Names();
        var package = Package(names);

        var first = await JsonAsync(await PostAsync(client, package, dryRun: false).ConfigureAwait(true), HttpStatusCode.OK)
            .ConfigureAwait(true);
        Assert.True(first.GetProperty("applied").GetBoolean());
        Assert.Equal("created", first.GetProperty("outcome").GetString());

        var ownerId = first.GetProperty("methodologies").EnumerateArray()
            .Single(m => m.GetProperty("code").GetString() == names.Owner)
            .GetProperty("methodologyId").GetInt32();

        await using (var db = Context())
        {
            var version = await db.MethodologyVersions.AsNoTracking().SingleAsync(v => v.MethodologyId == ownerId)
                .ConfigureAwait(true);
            Assert.Equal(TemplateVersionStatus.Draft, version.Status);
            Assert.Equal(1, await db.MethodologyImports.CountAsync(i => i.MethodologyVersionId == version.Id).ConfigureAwait(true));

            var constant = await db.MethodologyConstants.AsNoTracking()
                .SingleAsync(c => c.MethodologyVersionId == version.Id).ConfigureAwait(true);
            Assert.Equal("EF", constant.Code);
            Assert.Equal(2.5m, constant.Value);
            Assert.Equal(new DateOnly(2024, 1, 1), constant.ValidFrom);
            Assert.Null(constant.ValidTo);
        }

        Assert.Equal(1, await AuditCountAsync(names.Owner).ConfigureAwait(true));

        var second = await JsonAsync(await PostAsync(client, package, dryRun: false).ConfigureAwait(true), HttpStatusCode.OK)
            .ConfigureAwait(true);

        Assert.Equal("unchanged", second.GetProperty("outcome").GetString());
        Assert.False(second.GetProperty("applied").GetBoolean());
        Assert.Equal(2, second.GetProperty("totals").GetProperty("versionsUnchanged").GetInt32());

        Assert.Equal(2, await MethodologyCountAsync(names).ConfigureAwait(true));
        await using (var db = Context())
        {
            Assert.Equal(1, await db.MethodologyVersions.CountAsync(v => v.MethodologyId == ownerId).ConfigureAwait(true));
        }

        Assert.Equal(1, await AuditCountAsync(names.Owner).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Інший_вміст_тієї_самої_версії_дає_409_і_не_переписує()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Permissions).ConfigureAwait(true);
        var names = Names();

        await JsonAsync(await PostAsync(client, Package(names), dryRun: false).ConfigureAwait(true), HttpStatusCode.OK)
            .ConfigureAwait(true);

        var response = await PostAsync(client, Package(names, ownerText: "@A * 3 + !Shared"), dryRun: false)
            .ConfigureAwait(true);
        var problem = await JsonAsync(response, HttpStatusCode.Conflict).ConfigureAwait(true);

        Assert.Equal("err.ECR-CALC-0409.methodologyImportConflict", problem.GetProperty("messageKey").GetString());
        Assert.Equal(
            "draftDiffers",
            problem.GetProperty("report").GetProperty("conflicts")[0].GetProperty("kind").GetString());

        await using var db = Context();
        var owner = await db.Methodologies.AsNoTracking().SingleAsync(m => m.Code == names.Owner).ConfigureAwait(true);
        var formula = await db.MethodologyFormulas.AsNoTracking()
            .SingleAsync(f => db.MethodologyVersions.Any(v => v.Id == f.MethodologyVersionId && v.MethodologyId == owner.Id))
            .ConfigureAwait(true);
        Assert.Equal("@A * 2 + !Shared", formula.Expression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_на_константи_403()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ["Calculation.View", "Calculation.EditFormula"]).ConfigureAwait(true);
        var names = Names();

        var response = await PostAsync(client, Package(names), dryRun: true).ConfigureAwait(true);

        await JsonAsync(response, HttpStatusCode.Forbidden).ConfigureAwait(true);
    }

    // ── Опора ─────────────────────────────────────────────────────────────

    internal sealed record PackageNames(string Library, string Owner);

    internal static PackageNames Names()
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        return new PackageNames($"MIL{tag}", $"MIO{tag}");
    }

    /// <summary>Бібліотека з формулою й константою і методологія, що посилається на обидві.</summary>
    internal static object Package(
        PackageNames names, string ownerArguments = "@A;!Shared;CST.EF", string ownerText = "@A * 2 + !Shared")
        => new
        {
            format = "ecr-methodology-package",
            version = 1,
            library = names.Library,
            methodologies = new object[]
            {
                new
                {
                    name = names.Library,
                    versions = new[]
                    {
                        new
                        {
                            version = "V1",
                            formulas = new[] { Formula("Shared", "@A", "@A") },
                            constants = new[]
                            {
                                new
                                {
                                    name = "EF",
                                    parameter = "EF",
                                    unit = "t",
                                    values = new[]
                                    {
                                        new
                                        {
                                            category = "",
                                            version = "1",
                                            value = "2.5",
                                            startDate = "2023-12-31T19:00:00Z",
                                            endDate = "9999-02-19T19:00:00Z",
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
                new
                {
                    name = names.Owner,
                    versions = new[]
                    {
                        new
                        {
                            version = "V1",
                            formulas = new[] { Formula("OUT", ownerText, ownerArguments) },
                            constants = Array.Empty<object>(),
                        },
                    },
                },
            },
            blockers = Array.Empty<string>(),
        };

    private static object Formula(string name, string text, string arguments)
        => new
        {
            name,
            version = "1",
            arguments,
            text,
            startDate = "2023-12-31T19:00:00Z",
            endDate = "9999-02-19T19:00:00Z",
            isAvailable = true,
            report = "",
        };

    internal static Task<HttpResponseMessage> PostAsync(HttpClient client, object package, bool dryRun)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/import?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative),
            package);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");

        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<int> MethodologyCountAsync(PackageNames names)
    {
        await using var db = Context();
        return await db.Methodologies.CountAsync(m => m.Code == names.Library || m.Code == names.Owner)
            .ConfigureAwait(false);
    }

    private async Task<int> AuditCountAsync(string code)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM aud.StructureChange "
            + "WHERE EntityType = N'calc.MethodologyPackage' AND Operation = N'ImportPackage' AND NewJson LIKE @code;";
        command.Parameters.AddWithValue("@code", $"%{code}%");

        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, IReadOnlyList<string> permissions)
    {
        var name = $"mip_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Methodology import test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }
}
