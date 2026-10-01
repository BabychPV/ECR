// tests/Ecr.Api.Tests/DataSourcesControllerTests.Ssrf.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Політика SSRF адреси PI Web API на РЕАЛЬНОМУ HTTP і в справжній базі (TESTER-SCENARIOS Н-Л1, Н-Л1а, Н-Л3):
/// кожен ключ відмови, який гайд тестувальника називає, тут перевірено статусом, <c>messageKey</c> і полем
/// відповіді, а «джерела не створено / рядок не змінено» — запитом до бази, а не лише доменним тестом
/// <c>DataSourceEndpointPolicyTests</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази (кожен — точковою правкою):
/// <list type="bullet">
/// <item>у <c>DataSourceEndpointPolicy.IsBlocked</c> (<c>src/Ecr.Application/Integration/DataSourceEndpointPolicy.cs</c>)
/// прибрати <c>|| (b[0] == 169 &amp;&amp; b[1] == 254)</c> — <c>http://169.254.169.254/</c> створюється (201), червоніє
/// <see cref="Н_Л1_заборонена_адреса_PiWebApi_дає_422_з_ключем_і_полем_і_джерела_не_створює"/>; там же в
/// <c>TryParse</c> замінити <c>verdict = EndpointVerdict.Scheme;</c> на <c>verdict = EndpointVerdict.Malformed;</c> —
/// той самий тест отримує <c>dataSourceEndpointMalformed</c> замість <c>dataSourceEndpointScheme</c>;</item>
/// <item>у <c>DataSourceEndpointPolicy.CheckAddress</c> замінити
/// <c>IsBlocked(ip) || (negotiate &amp;&amp; IsPrivate(ip))</c> на <c>IsBlocked(ip)</c> — Negotiate-джерело з
/// <c>http://10.0.0.5/</c> створюється, червоніє <see cref="Н_Л1а_приватний_IP_літерал_заборонено_лише_для_Negotiate"/>;
/// на <c>IsBlocked(ip) || IsPrivate(ip)</c> — той самий тест червоніє на Basic-половині (422 замість 201);</item>
/// <item>у <c>DataSourceEndpointPolicy.CheckAddress</c> останній рядок замінити на <c>return EndpointVerdict.Allowed;</c>
/// (або в <c>EndpointNetwork.AllowedHosts</c> змінити ключ <c>PiWebApi:AllowedHosts</c>) — хост поза списком
/// створюється, червоніє <see cref="Н_Л1_хост_поза_PiWebApi_AllowedHosts_дає_422_а_хост_зі_списку_створюється"/>;</item>
/// <item>у <c>SaveDataSourceHandler.UpdateAsync</c> (<c>DataSourceHandlers.cs</c>) замінити
/// <c>else if (!confirmEndpointChange)</c> на <c>else if (false)</c> — зміна адреси без підтвердження дає 200,
/// червоніє <see cref="Н_Л3_зміна_адреси_Negotiate_джерела_без_підтвердження_422_а_з_підтвердженням_200"/>;
/// на <c>else if (true)</c> — той самий тест червоніє на підтвердженій половині.</item>
/// </list>
/// ⚠ Секрет і allowlist задаються змінними оточення (<c>ECR_Secrets__…</c>, <c>ECR_PiWebApi__AllowedHosts__n</c>) —
/// тим самим шляхом, що й у проді; див. коментар у <see cref="EcrApiFactory"/>. Тому кожен тест знімає їх у
/// <c>finally</c>, а створені активні джерела вимикає (інакше <c>/health/ready</c> «Degraded» для решти прогону).
/// </remarks>
public sealed partial class DataSourcesControllerTests
{
    private const string BasicSecret = "Basic c3ZjOlNzcmYtUHJvYmUtMjAyNg==";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    [Trait("Scenario", "Н-Л1")]
    public async Task Н_Л1_заборонена_адреса_PiWebApi_дає_422_з_ключем_і_полем_і_джерела_не_створює()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

        // Секрету під ці коди немає — тобто Negotiate (службовий акаунт процесу).
        (string Endpoint, string? Secondary, string Key, string Field)[] cases =
        [
            ("ftp://pi.example.local/piwebapi", null, "err.ECR-REQ-0422.dataSourceEndpointScheme", "endpoint"),
            ("http://127.0.0.1/x", null, "err.ECR-REQ-0422.dataSourceEndpointHostForbidden", "endpoint"),
            ("http://[::1]/", null, "err.ECR-REQ-0422.dataSourceEndpointHostForbidden", "endpoint"),
            ("http://169.254.169.254/", null, "err.ECR-REQ-0422.dataSourceEndpointHostForbidden", "endpoint"),
            ("http://0.0.0.0/", null, "err.ECR-REQ-0422.dataSourceEndpointHostForbidden", "endpoint"),
            ("http://2130706433/", null, "err.ECR-REQ-0422.dataSourceEndpointHostForbidden", "endpoint"),
            ("http://localhost/piwebapi", null, "err.ECR-REQ-0422.dataSourceEndpointHostForbidden", "endpoint"),
            ("http://10.0.0.5/", null, "err.ECR-REQ-0422.dataSourceEndpointHostForbidden", "endpoint"),
            (Endpoint, "http://169.254.169.254/latest/meta-data", "err.ECR-REQ-0422.dataSourceEndpointHostForbidden",
                "secondaryEndpoint"),
        ];

        var codes = new List<string>();

        foreach (var (endpoint, secondary, key, field) in cases)
        {
            var code = $"NL{Guid.NewGuid():N}"[..12].ToUpperInvariant();
            codes.Add(code);

            var refused = await client.PostAsJsonAsync(
                Sources,
                new
                {
                    code,
                    nameL10n = new Dictionary<string, string> { ["en"] = "PI AF" },
                    transport = "PiWebApi",
                    endpoint,
                    secondaryEndpoint = secondary,
                    catalog = "EcrDb",
                    maxParallel = 4,
                    isActive = false,
                }).ConfigureAwait(true);

            Assert.True(
                refused.StatusCode == HttpStatusCode.UnprocessableEntity,
                $"{endpoint} / {secondary}: {refused.StatusCode} {app.ErrorsText}");

            var problem = await BodyAsync(refused, null).ConfigureAwait(true);
            Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
            Assert.True(
                key == problem.GetProperty("messageKey").GetString(),
                $"{endpoint} / {secondary}: {problem}");
            Assert.Equal(field, problem.GetProperty("field").GetString());
        }

        await using var db = NewDb();
        Assert.False(await db.DataSources.AnyAsync(s => codes.Contains(s.Code)).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    [Trait("Scenario", "Н-Л1а")]
    public async Task Н_Л1а_приватний_IP_літерал_заборонено_лише_для_Negotiate()
    {
        const string privateIp = "http://10.0.0.5/piwebapi";
        var negotiateCode = $"NG{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var basicCode = $"BA{Guid.NewGuid():N}"[..12].ToUpperInvariant();

        // Basic-секрет у середовищі — саме він робить джерело «не Negotiate».
        Environment.SetEnvironmentVariable($"ECR_Secrets__DataSource.{basicCode}", BasicSecret);
        int? id = null;

        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

            // ⛔ Negotiate: службові облікові дані на приватний літерал — відмова.
            var refused = await client.PostAsJsonAsync(Sources, Body(negotiateCode, endpoint: privateIp))
                .ConfigureAwait(true);
            Assert.True(
                refused.StatusCode == HttpStatusCode.UnprocessableEntity, $"{refused.StatusCode}: {app.ErrorsText}");
            var problem = await BodyAsync(refused, null).ConfigureAwait(true);
            Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
            Assert.Equal(
                "err.ECR-REQ-0422.dataSourceEndpointHostForbidden", problem.GetProperty("messageKey").GetString());
            Assert.Equal("endpoint", problem.GetProperty("field").GetString());

            // Basic: та сама адреса блок-листом не заборонена.
            var created = await client.PostAsJsonAsync(
                Sources, Body(basicCode, endpoint: privateIp, secretConfirmation: BasicSecret)).ConfigureAwait(true);
            Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
            id = (await BodyAsync(created, null).ConfigureAwait(true)).GetProperty("id").GetInt32();

            await using var db = NewDb();
            Assert.False(await db.DataSources.AnyAsync(s => s.Code == negotiateCode).ConfigureAwait(true));
            var stored = await db.DataSources.AsNoTracking().SingleAsync(s => s.Id == id).ConfigureAwait(true);
            Assert.Equal(privateIp, stored.Endpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"ECR_Secrets__DataSource.{basicCode}", null);

            if (id is { } done)
            {
                await SetActiveAsync(done, false).ConfigureAwait(true);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    [Trait("Scenario", "Н-Л1")]
    public async Task Н_Л1_хост_поза_PiWebApi_AllowedHosts_дає_422_а_хост_зі_списку_створюється()
    {
        var outsideCode = $"AO{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var exactCode = $"AE{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var wildcardCode = $"AW{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var ids = new List<int>();

        // ⚠ Через оточення, як і решту конфігурації стенда (EcrApiFactory): `EndpointNetwork`
        // читає `PiWebApi:AllowedHosts` з IConfiguration на кожен виклик.
        Environment.SetEnvironmentVariable("ECR_PiWebApi__AllowedHosts__0", "pi.allowed.example");
        Environment.SetEnvironmentVariable("ECR_PiWebApi__AllowedHosts__1", "*.corp-allowed.example");

        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

            // Хост поза списком; і сам домен wildcard-правила (`*.d` збігається лише з піддоменом).
            foreach (var outside in new[] { "https://pi.other.example/piwebapi", "https://corp-allowed.example/piwebapi" })
            {
                var refused = await client.PostAsJsonAsync(Sources, Body(outsideCode, endpoint: outside))
                    .ConfigureAwait(true);
                Assert.True(
                    refused.StatusCode == HttpStatusCode.UnprocessableEntity,
                    $"{outside}: {refused.StatusCode} {app.ErrorsText}");
                var problem = await BodyAsync(refused, null).ConfigureAwait(true);
                Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
                Assert.Equal(
                    "err.ECR-REQ-0422.dataSourceEndpointHostNotAllowed", problem.GetProperty("messageKey").GetString());
                Assert.Equal("endpoint", problem.GetProperty("field").GetString());
            }

            foreach (var (code, allowed) in new[]
                     {
                         (exactCode, "https://PI.ALLOWED.EXAMPLE/piwebapi"),
                         (wildcardCode, "https://af01.corp-allowed.example/piwebapi"),
                     })
            {
                var created = await client.PostAsJsonAsync(Sources, Body(code, endpoint: allowed)).ConfigureAwait(true);
                Assert.True(
                    created.StatusCode == HttpStatusCode.Created, $"{allowed}: {created.StatusCode} {app.ErrorsText}");
                ids.Add((await BodyAsync(created, null).ConfigureAwait(true)).GetProperty("id").GetInt32());
            }

            await using var db = NewDb();
            Assert.False(await db.DataSources.AnyAsync(s => s.Code == outsideCode).ConfigureAwait(true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ECR_PiWebApi__AllowedHosts__0", null);
            Environment.SetEnvironmentVariable("ECR_PiWebApi__AllowedHosts__1", null);

            foreach (var id in ids)
            {
                await SetActiveAsync(id, false).ConfigureAwait(true);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    [Trait("Scenario", "Н-Л3")]
    public async Task Н_Л3_зміна_адреси_Negotiate_джерела_без_підтвердження_422_а_з_підтвердженням_200()
    {
        const string moved = "https://pi2.corp.example/piwebapi";

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

        // Вимкнене джерело без секрету в середовищі — тобто Negotiate.
        var (id, code) = await AddInactiveAsync().ConfigureAwait(true);

        // Без прапора і з явним `false` — однаково відмова.
        foreach (bool? confirm in new bool?[] { null, false })
        {
            var version = await VersionAsync(client, id).ConfigureAwait(true);
            var refused = await SendAsync(client, HttpMethod.Put, id, version, Move(code, moved, confirm))
                .ConfigureAwait(true);

            Assert.True(
                refused.StatusCode == HttpStatusCode.UnprocessableEntity,
                $"confirm={confirm}: {refused.StatusCode} {app.ErrorsText}");
            var problem = await BodyAsync(refused, null).ConfigureAwait(true);
            Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
            Assert.Equal(
                "err.ECR-REQ-0422.dataSourceEndpointChangeUnconfirmed", problem.GetProperty("messageKey").GetString());
            Assert.Equal("confirmEndpointChange", problem.GetProperty("field").GetString());

            await using var db = NewDb();
            var stored = await db.DataSources.AsNoTracking().SingleAsync(s => s.Id == id).ConfigureAwait(true);
            Assert.Equal(Endpoint, stored.Endpoint);
            Assert.Equal("EcrDb", stored.Catalog);
        }

        // Зміна без зміни адреси підтвердження не потребує.
        var renamed = await SendAsync(
            client, HttpMethod.Put, id, await VersionAsync(client, id).ConfigureAwait(true),
            Body(code, isActive: false, catalog: "Renamed")).ConfigureAwait(true);
        Assert.True(renamed.StatusCode == HttpStatusCode.OK, $"{renamed.StatusCode}: {app.ErrorsText}");

        var confirmed = await SendAsync(
            client, HttpMethod.Put, id, await VersionAsync(client, id).ConfigureAwait(true),
            Move(code, moved, confirm: true)).ConfigureAwait(true);
        Assert.True(confirmed.StatusCode == HttpStatusCode.OK, $"{confirmed.StatusCode}: {app.ErrorsText}");
        Assert.Equal(moved, (await BodyAsync(confirmed, null).ConfigureAwait(true)).GetProperty("endpoint").GetString());

        await using (var db = NewDb())
        {
            var stored = await db.DataSources.AsNoTracking().SingleAsync(s => s.Id == id).ConfigureAwait(true);
            Assert.Equal(moved, stored.Endpoint);
        }
    }

    /// <summary>Тіло PUT зі зміненою адресою; <c>confirm == null</c> — без поля <c>confirmEndpointChange</c>.</summary>
    private static object Move(string code, string endpoint, bool? confirm)
    {
        var name = new Dictionary<string, string> { ["en"] = "PI AF primary" };

        return confirm is { } value
            ? new
            {
                code,
                nameL10n = name,
                transport = "PiWebApi",
                endpoint,
                catalog = "EcrDb",
                maxParallel = 4,
                isActive = false,
                confirmEndpointChange = value,
            }
            : new
            {
                code,
                nameL10n = name,
                transport = "PiWebApi",
                endpoint,
                catalog = "EcrDb",
                maxParallel = 4,
                isActive = false,
            };
    }
}
