// tests/Ecr.Api.Tests/MethodologyCyclePublishApiTests.cs
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
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
/// MS x B179 (закриття A1): публікація версії-скрипта методології, що замикає цикл залежностей документів, - HTTP 422
/// <c>ECR-TMPL-0422</c> / <c>err.ECR-TMPL-0422.dependencyCycle</c> із полем <c>cycle</c>; без циклу публікація проходить.
/// </summary>
/// <remarks>
/// <para>
/// Справжній хост і SQL. Шаблон-джерело S наповнює довідник R зв'язкою; скрипт читає R
/// (<c>REGFIELD(REGFIND(...))</c>) і пише в Calculated-колонку: у шаблон S - цикл S - R - скрипт - S; в інший шаблон O -
/// циклу немає. Золотий набір для успішної гілки рахує фейк-модуль (як у <c>MethodologyScriptApiTests</c>); відмова
/// настає ПІСЛЯ золотого набору (граф читає лише опубліковані версії). Перемикання Formulas - Script - у <c>MethodologyScriptMigrationApiTests</c>.
/// </para>
/// <para>
/// Мутації (локально): прибрати <c>RefuseMethodologiesAsync</c> з <c>PublishMethodologyHandler</c> - перший тест
/// червоний; відмовляти завжди, коли скрипт читає довідник - другий тест червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyCyclePublishApiTests(SqlServerFixture sql)
{
    private const string Password = "Cycle-Publish-B179-1!";
    private const int PeriodKey = 209901;

    private static readonly DateTime Now = new(2099, 1, 10, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Doc(TestDocument Document, string TableCode, int CalculatedColumnId)
    {
        public int TableDefId => Document.TableDefId;
    }

    private sealed record World(Doc Source, Doc Other, string RegistryCode, string SourceTemplateCode);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Публікація_скрипта_що_читає_довідник_і_пише_в_шаблон_цього_довідника_відмовляє_422_з_циклом()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        using var baseApp = new EcrApiFactory(sql);
        using var app = WithFakeModule(baseApp);
        var (author, publisher) = await UsersAsync(app).ConfigureAwait(true);
        var created = await CreateAsync(author, world, world.Source).ConfigureAwait(true);

        using var response = await publisher.PostAsJsonAsync(
            new Uri(PublishUrl(created), UriKind.Relative), new { changeReason = "B179", effectiveFrom = "2099-01-01" }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-TMPL-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal(DependencyCyclePublishCheck.MessageKey, problem.GetProperty("messageKey").GetString());
        Assert.Equal(
            $"{created.Code} → {world.SourceTemplateCode} → {world.RegistryCode} → {created.Code}",
            problem.GetProperty("cycle").GetString());

        // Транзакція відкочена: чернетка, прив'язки виходу немає.
        await using var db = Context();
        Assert.Equal(
            TemplateVersionStatus.Draft,
            await db.MethodologyVersions.Where(v => v.Id == created.VersionId).Select(v => v.Status).SingleAsync().ConfigureAwait(true));
        Assert.False(await db.CalculationBindings.AnyAsync(b => b.MethodologyId == created.MethodologyId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Публікація_скрипта_що_читає_довідник_і_пише_в_інший_шаблон_проходить()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        using var baseApp = new EcrApiFactory(sql);
        using var app = WithFakeModule(baseApp);
        var (author, publisher) = await UsersAsync(app).ConfigureAwait(true);
        var created = await CreateAsync(author, world, world.Other).ConfigureAwait(true);

        using var response = await publisher.PostAsJsonAsync(
            new Uri(PublishUrl(created), UriKind.Relative), new { changeReason = "B179", effectiveFrom = "2099-01-01" }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");

        await using var db = Context();
        Assert.Equal(
            TemplateVersionStatus.Published,
            await db.MethodologyVersions.Where(v => v.Id == created.VersionId).Select(v => v.Status).SingleAsync().ConfigureAwait(true));
    }

    /// <summary>
    /// F13: методологія, що ВЖЕ лежала на циклі залежностей документів (цикл з'явився не через цю публікацію), не
    /// блокується: нова версія без нових циклів публікується. Перевіряє проводку <c>onCycle</c> у <c>PublishMethodologyHandler</c>.
    /// </summary>
    /// <remarks>Мутація: передати у <c>RefuseMethodologiesAsync</c> порожній набір замість <c>onCycle</c> - тест червоний (422).</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Нова_версія_методології_що_вже_лежить_на_циклі_публікується()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        using var baseApp = new EcrApiFactory(sql);
        using var app = WithFakeModule(baseApp);
        var (author, publisher) = await UsersAsync(app).ConfigureAwait(true);
        var created = await CreateAsync(author, world, world.Other).ConfigureAwait(true);

        using var first = await publisher.PostAsJsonAsync(
            new Uri(PublishUrl(created), UriKind.Relative), new { changeReason = "B179", effectiveFrom = "2099-01-01" }).ConfigureAwait(true);
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"{(int)first.StatusCode}: {await first.Content.ReadAsStringAsync().ConfigureAwait(true)}");

        // Цикл виникає ззовні: шаблон, у який пише скрипт, сам наповнює довідник, який скрипт читає.
        await using (var db = Context())
        {
            var registryId = await db.RegistryDefs.Where(r => r.Code == world.RegistryCode).Select(r => r.Id).SingleAsync().ConfigureAwait(true);
            db.DocumentSourceBindings.Add(new DocumentSourceBinding($"B179CYC2_{Guid.NewGuid():N}"[..20], world.Other.Document.TemplateId, registryId));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var copy = await JsonAsync(
            await author.PostAsJsonAsync(
                new Uri($"/api/v1/methodologies/{created.MethodologyId}/versions", UriKind.Relative),
                new { versionNumber = "2.0", copyFromVersionId = created.VersionId, level = "Configuration", engine = "Script" })
                .ConfigureAwait(true)).ConfigureAwait(true);
        var secondVersionId = copy.RootElement.GetProperty("id").GetInt32();

        using var second = await publisher.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{created.MethodologyId}/versions/{secondVersionId}/publish", UriKind.Relative),
            new { changeReason = "B179 v2", effectiveFrom = "2099-02-01" }).ConfigureAwait(true);
        var body = await second.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(second.StatusCode == HttpStatusCode.OK, $"{(int)second.StatusCode}: {body}");
    }

    /// <summary>
    /// MS x B179: те саме для ФОРМУЛЬНОЇ версії - формула читає довідник (<c>REGFIELD(REGFIND(...))</c>) і результат пишеться в
    /// колонку шаблону-джерела цього довідника: 422 <c>ECR-TMPL-0422</c> із шляхом, транзакція публікації відкочена.
    /// </summary>
    /// <remarks>
    /// Мутація: прибрати <c>RefuseMethodologiesAsync</c> з формульного шляху <c>PublishMethodologyHandler</c> - тест червоний
    /// (200 замість 422); відмовляти завжди, коли формула читає довідник - наступний тест червоний. Золотий набір рахує
    /// фейк-модуль (як для скрипта), цикл виявляє перевірка ПІСЛЯ публікації.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Публікація_формульної_версії_що_читає_довідник_і_пише_в_шаблон_цього_довідника_відмовляє_422_з_циклом()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        using var baseApp = new EcrApiFactory(sql);
        using var app = WithFakeModule(baseApp);
        var (author, publisher) = await UsersAsync(app).ConfigureAwait(true);
        var created = await CreateFormulaAsync(author, world, world.Source).ConfigureAwait(true);

        using var response = await publisher.PostAsJsonAsync(
            new Uri(PublishUrl(created), UriKind.Relative), new { changeReason = "B179", effectiveFrom = "2099-01-01" }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-TMPL-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal(DependencyCyclePublishCheck.MessageKey, problem.GetProperty("messageKey").GetString());
        Assert.Equal(
            $"{created.Code} → {world.SourceTemplateCode} → {world.RegistryCode} → {created.Code}",
            problem.GetProperty("cycle").GetString());

        await using var db = Context();
        Assert.Equal(
            TemplateVersionStatus.Draft,
            await db.MethodologyVersions.Where(v => v.Id == created.VersionId).Select(v => v.Status).SingleAsync().ConfigureAwait(true));
    }

    /// <summary>Формульна версія, що читає довідник, але пише в ІНШИЙ шаблон, публікується.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Публікація_формульної_версії_що_читає_довідник_і_пише_в_інший_шаблон_проходить()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        using var baseApp = new EcrApiFactory(sql);
        using var app = WithFakeModule(baseApp);
        var (author, publisher) = await UsersAsync(app).ConfigureAwait(true);
        var created = await CreateFormulaAsync(author, world, world.Other).ConfigureAwait(true);

        using var response = await publisher.PostAsJsonAsync(
            new Uri(PublishUrl(created), UriKind.Relative), new { changeReason = "B179", effectiveFrom = "2099-01-01" }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");

        await using var db = Context();
        Assert.Equal(
            TemplateVersionStatus.Published,
            await db.MethodologyVersions.Where(v => v.Id == created.VersionId).Select(v => v.Status).SingleAsync().ConfigureAwait(true));
    }

    // ───────────────────────────── стенд ─────────────────────────────

    private sealed record Created(int MethodologyId, int VersionId, string Code);

    /// <summary>Цикл виявляє перевірка ПІСЛЯ золотого набору (граф читає лише опубліковані версії): золотий набір рахує фейк.</summary>
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithFakeModule(EcrApiFactory baseApp)
        => baseApp.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            // Модуль скрипта зареєстровано фабрикою: прибираємо його, щоб золотий набір рахував фейк.
            foreach (var descriptor in s.Where(d => d.ServiceType == typeof(ICalculationModule)
                                                    && (d.ImplementationType == typeof(Ecr.Calculations.ScriptCalculationModule)
                                                        || (d.ImplementationFactory is not null && d.ImplementationInstance is null))).ToList())
            {
                s.Remove(descriptor);
            }

            s.AddSingleton<ICalculationModule>(new FakeScriptModule());
        }));
    private static string PublishUrl(Created c) => $"/api/v1/methodologies/{c.MethodologyId}/versions/{c.VersionId}/publish";

    private async Task<World> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registryCode = $"B179CYC_{tag}";
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        int registryId;
        await using (var db = builder.CreateContext())
        {
            var registry = new RegistryDef(EcrCode.Create(registryCode), Name(registryCode), isTemporal: false);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync().ConfigureAwait(false);
            var fields = new List<RegistryFieldDef>();
            foreach (var (code, type, ordinal) in new[]
                     {
                         ("YEAR", CellDataType.Int, 1), ("MONTH", CellDataType.Int, 2), ("STREAM", CellDataType.String, 3),
                         ("INDICATOR", CellDataType.String, 4), ("VALUE", CellDataType.Decimal, 5),
                     })
            {
                var field = new RegistryFieldDef(registry.Id, EcrCode.Create(code), Name(code), type, ordinal);
                field.Update(Name(code), ordinal, ordinal < 5);
                db.RegistryFieldDefs.Add(field);
                fields.Add(field);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }

            db.RegistryKeyDefs.Add(new RegistryKeyDef(
                registry.Id, EcrCode.Create("PK"), Name("PK"), [.. fields.Take(4)], isPrimary: true, ignoreCase: true, 1, Now));
            await db.SaveChangesAsync().ConfigureAwait(false);
            registryId = registry.Id;
        }

        var source = await NewDocAsync(builder).ConfigureAwait(false);
        var other = await NewDocAsync(builder).ConfigureAwait(false);

        string sourceTemplateCode;
        await using (var db = builder.CreateContext())
        {
            db.DocumentSourceBindings.Add(new DocumentSourceBinding($"B179CYC_{tag}", source.Document.TemplateId, registryId));
            await db.SaveChangesAsync().ConfigureAwait(false);
            sourceTemplateCode = await db.Templates.Where(t => t.Id == source.Document.TemplateId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);
        }

        return new World(source, other, registryCode, sourceTemplateCode);
    }

    private static async Task<Doc> NewDocAsync(TestDocumentBuilder builder)
    {
        var doc = await builder.BuildAsync(periodKey: PeriodKey, columnCount: 2).ConfigureAwait(false);
        await using var db = builder.CreateContext();
        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == doc.TableDefId).ConfigureAwait(false);
        var calculated = new ColumnDef(table.Id, EcrCode.Create("OUT_A"), Name("Out A"), 20, CellDataType.Calculated);
        db.ColumnDefs.Add(calculated);
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId).ConfigureAwait(false);
        version.Publish(1, Now);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new Doc(doc, table.Code, calculated.Id);
    }

    /// <summary>Скрипт читає довідник і пише <c>OUT_A</c> у таблицю <paramref name="target"/>; золотий набір - один випадок.</summary>
    private static async Task<Created> CreateAsync(HttpClient author, World world, Doc target)
    {
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var code = $"MC{tag}";
        using var methodology = await JsonAsync(
            await author.PostAsJsonAsync(new Uri("/api/v1/methodologies", UriKind.Relative), new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = "Cycle script" },
                kind = 0,
                group = (string?)null,
            }).ConfigureAwait(false)).ConfigureAwait(false);
        var methodologyId = methodology.RootElement.GetProperty("id").GetInt32();

        using var version = await JsonAsync(
            await author.PostAsJsonAsync(new Uri($"/api/v1/methodologies/{methodologyId}/versions", UriKind.Relative), new
            {
                versionNumber = "1.0",
                copyFromVersionId = (int?)null,
                level = "Configuration",
                engine = "Script",
            }).ConfigureAwait(false)).ConfigureAwait(false);
        var versionId = version.RootElement.GetProperty("id").GetInt32();

        var text = $"rows from {target.TableCode};\n"
            + $"output OUT_A : kg = REGFIELD(REGFIND(\"{world.RegistryCode}\", period.Year, period.Month, \"a\", \"b\"), \"VALUE\");";
        using var saved = await JsonAsync(
            await author.PutAsJsonAsync(
                new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/script", UriKind.Relative), new { text }).ConfigureAwait(false))
            .ConfigureAwait(false);
        Assert.Empty(saved.RootElement.GetProperty("diagnostics").EnumerateArray());

        using var testCase = await JsonAsync(
            await author.PutAsJsonAsync(
                new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/tests/case1", UriKind.Relative),
                new
                {
                    inputJson = $$"""{"periodKey":{"value":{{PeriodKey}}},"documentId":{{target.Document.DocumentId}},"arguments":[]}""",
                    expectedJson = """{"OUT_A":5}""",
                    tolerance = 0m,
                }).ConfigureAwait(false)).ConfigureAwait(false);
        return new Created(methodologyId, versionId, code);
    }

    /// <summary>
    /// Формульна версія (Strict, формула рівня рядка читає довідник), прив'язка виходу до <c>OUT_A</c> таблиці
    /// <paramref name="target"/>; золотий набір - один випадок (рахує фейк-модуль).
    /// </summary>
    private async Task<Created> CreateFormulaAsync(HttpClient author, World world, Doc target)
    {
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var code = $"MF{tag}";
        using var methodology = await JsonAsync(
            await author.PostAsJsonAsync(new Uri("/api/v1/methodologies", UriKind.Relative), new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = "Cycle formula" },
                kind = 0,
                group = (string?)null,
            }).ConfigureAwait(false)).ConfigureAwait(false);
        var methodologyId = methodology.RootElement.GetProperty("id").GetInt32();

        using var version = await JsonAsync(
            await author.PostAsJsonAsync(new Uri($"/api/v1/methodologies/{methodologyId}/versions", UriKind.Relative), new
            {
                versionNumber = "1.0",
                copyFromVersionId = (int?)null,
                level = "Configuration",
                engine = "Formulas",
            }).ConfigureAwait(false)).ConfigureAwait(false);
        var versionId = version.RootElement.GetProperty("id").GetInt32();

        await using (var db = Context())
        {
            var entity = await db.MethodologyVersions.SingleAsync(v => v.Id == versionId).ConfigureAwait(false);
            entity.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
            var formula = entity.AddFormula(
                EcrCode.Create("F1"),
                $"REGFIELD(REGFIND('{world.RegistryCode}', [Period].Year, [Period].Sequence, 'a', 'b'), 'VALUE')",
                FormulaResultType.Number,
                null);
            formula.SetScope(MethodologyFormulaScope.Row);
            db.MethodologyFormulas.Add(formula);
            db.CalculationBindings.Add(new CalculationBinding(target.TableDefId, target.CalculatedColumnId, methodologyId, "OUT_A", "{}"));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using var testCase = await JsonAsync(
            await author.PutAsJsonAsync(
                new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/tests/case1", UriKind.Relative),
                new
                {
                    inputJson = $$"""{"periodKey":{"value":{{PeriodKey}}},"documentId":{{target.Document.DocumentId}},"arguments":[]}""",
                    expectedJson = """{"OUT_A":5}""",
                    tolerance = 0m,
                }).ConfigureAwait(false)).ConfigureAwait(false);
        return new Created(methodologyId, versionId, code);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
            return JsonDocument.Parse(body);
        }
    }

    private async Task<(HttpClient Author, HttpClient Publisher)> UsersAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
        => (await SignInAsync(app, ["Calculation.View", "Calculation.EditFormula"]).ConfigureAwait(false),
            await SignInAsync(app, ["Calculation.View", "Calculation.Publish"]).ConfigureAwait(false));

    private async Task<HttpClient> SignInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string[] permissions)
    {
        var name = $"cyc_{Guid.NewGuid():N}"[..20];
        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12].ToUpperInvariant()), Name("Cycle tests"));
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
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}");
        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Фейковий модуль скрипта: відповідає на <c>CanHandle</c> для скриптів і дає число, яке чекає золотий набір.</summary>
    private sealed class FakeScriptModule : ICalculationModule
    {
        public string Code => "fake-script";

        public CalculationLevel Level => CalculationLevel.Configuration;

        public ConcurrentQueue<MethodologyDescriptor> Seen { get; } = new();

        public bool CanHandle(MethodologyDescriptor methodology) => methodology.Engine == MethodologyEngine.Script;

        public Task<CalculationBindingContext> PrepareAsync(
            MethodologyDescriptor methodology, long documentId, PeriodKey periodKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CalculationOutput> ExecuteAsync(CalculationBindingContext binding, CalculationInput input, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct)
        {
            Seen.Enqueue(input.Methodology);
            return Task.FromResult(new CalculationOutput(
                input.DocumentId, input.SourceRowKey,
                [new CalculationOutputValue(input.Methodology.MethodologyVersionId, null, "OUT_A", 5m, 1)],
                []));
        }
    }
}
