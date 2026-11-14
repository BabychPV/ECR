using System.Text.Json;
using Ecr.Api.Startup;
using Ecr.Application.Health;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B105: прапор <c>Calculations:Hse400CompositionFromDocument</c> видно в <c>GET /api/v1/health/facts</c>, а вимкнений
/// прапор за наявного документа HSE400 поточного року дає попередження (Land інакше мовчки читає довідник).
/// </summary>
/// <remarks>
/// Червоні до фіксу (заглушки): <see cref="SystemFactsCalculationsDto.Of"/> завжди без попередження —
/// <see cref="Попередження_лише_коли_прапор_вимкнено_а_документ_поточного_року_є"/>; сховище завжди «документа
/// немає» — <see cref="Сховище_бачить_документ_HSE400_року_лише_за_опублікованої_версії"/>; контролер без прапора —
/// <see cref="Факти_показують_увімкнений_прапор_і_його_джерело"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class SystemHealthHse400FlagTests(SqlServerFixture sql)
{
    private const string ViewHealth = "System.ViewHealth";
    private static readonly Uri Facts = new("/api/v1/health/facts", UriKind.Relative);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE400")]
    public void Попередження_лише_коли_прапор_вимкнено_а_документ_поточного_року_є(
        bool enabled, bool documentInYear, bool expectedWarning)
    {
        var dto = SystemFactsCalculationsDto.Of(enabled, ConfigurationSourceLabel.Environment, documentInYear);

        Assert.Equal(enabled, dto.Hse400CompositionFromDocument);
        Assert.Equal(ConfigurationSourceLabel.Environment, dto.Hse400CompositionSource);
        Assert.Equal(expectedWarning, dto.Hse400CompositionWarning);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE400")]
    public void Мітка_джерела_за_останнім_постачальником_що_задає_ключ()
    {
        const string Key = Hse400CompositionSnapshotLoader.EnabledKey;

        // Власний префікс змінних — не зачіпає жодного іншого тесту процесу.
        var prefix = $"ECRB105_{Guid.NewGuid():N}_";
        var variable = prefix + Key.Replace(":", "__", StringComparison.Ordinal);
        Environment.SetEnvironmentVariable(variable, "true");
        try
        {
            var memory = new Dictionary<string, string?> { [Key] = "false" };

            var none = new ConfigurationBuilder().Build();
            Assert.Equal(SystemHostFacts.SourceDefault, ConfigurationSourceLabel.Of(none, Key));

            var onlyMemory = new ConfigurationBuilder().AddInMemoryCollection(memory).Build();
            Assert.Equal(ConfigurationSourceLabel.Other, ConfigurationSourceLabel.Of(onlyMemory, Key));

            // Змінна оточення іде пізніше — вона й дієва (як ECR_ у Program.cs).
            var withEnvironment = new ConfigurationBuilder()
                .AddInMemoryCollection(memory)
                .AddEnvironmentVariables(prefix)
                .Build();
            Assert.Equal(ConfigurationSourceLabel.Environment, ConfigurationSourceLabel.Of(withEnvironment, Key));
            Assert.True(Hse400CompositionSnapshotLoader.IsEnabled(withEnvironment));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE400")]
    public async Task Сховище_бачить_документ_HSE400_року_лише_за_опублікованої_версії()
    {
        // Власний шаблон (код передається сховищу замість HSE400) і рік 2099 — без глобальних рядків.
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 209906);
        string templateCode;
        await using (var db = sql.CreateContext())
        {
            templateCode = await db.Templates.Where(t => t.Id == doc.TemplateId).Select(t => t.Code).SingleAsync();
        }

        var inYear = new DateTime(2099, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        // Версія ще чернетка — шаблон не опубліковано, попереджати нема про що.
        Assert.False((await ReadAsync(templateCode, inYear)).Hse400DocumentInCurrentYear);

        await using (var db = sql.CreateContext())
        {
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            version.Publish(1, inYear);
            await db.SaveChangesAsync();
        }

        Assert.True((await ReadAsync(templateCode, inYear)).Hse400DocumentInCurrentYear);

        // Інший рік — документа немає.
        Assert.False((await ReadAsync(templateCode, inYear.AddYears(-1))).Hse400DocumentInCurrentYear);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE400")]
    public async Task Факти_показують_увімкнений_прапор_і_його_джерело()
    {
        using var factory = new EcrApiFactory(sql);
        using var app = factory.WithWebHostBuilder(
            b => b.UseSetting(Hse400CompositionSnapshotLoader.EnabledKey, "true"));
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, ViewHealth);

        var calculations = await ReadCalculationsAsync(client);

        Assert.True(calculations.GetProperty("hse400CompositionFromDocument").GetBoolean());
        Assert.False(calculations.GetProperty("hse400CompositionWarning").GetBoolean());
        Assert.NotEqual(SystemHostFacts.SourceDefault, calculations.GetProperty("hse400CompositionSource").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE400")]
    public async Task Факти_показують_вимкнений_прапор_за_умовчання()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, ViewHealth);

        var calculations = await ReadCalculationsAsync(client);

        Assert.False(calculations.GetProperty("hse400CompositionFromDocument").GetBoolean());
        Assert.Contains(
            calculations.GetProperty("hse400CompositionSource").GetString(),
            new[]
            {
                SystemHostFacts.SourceDefault, ConfigurationSourceLabel.AppSettings, ConfigurationSourceLabel.Environment,
                ConfigurationSourceLabel.ProgramData, ConfigurationSourceLabel.CommandLine, ConfigurationSourceLabel.Other,
            });
    }

    private async Task<Ecr.Application.Ports.SystemHealthSnapshot> ReadAsync(string templateCode, DateTime utcNow)
    {
        // Новий store — новий кеш знімка (30 с на екземпляр без DI).
        await using var db = sql.CreateContext();
        var store = new SystemHealthStore(db, NullLogger<SystemHealthStore>.Instance, hse400TemplateCode: templateCode);
        return await store.ReadAsync(utcNow, CancellationToken.None);
    }

    private static async Task<JsonElement> ReadCalculationsAsync(HttpClient client)
    {
        var response = await client.GetAsync(Facts);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("calculations").Clone();
    }
}
