using System.Text.Json;
using Ecr.Api.Startup;
using Ecr.Application.Health;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
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
/// Червоні до фіксу (заглушки): <see cref="SystemFactsCalculationsDto.Of(bool, string, bool)"/> завжди без попередження —
/// <see cref="Попередження_лише_коли_прапор_вимкнено_а_документ_поточного_року_є"/>; сховище завжди «документа
/// немає» — <see cref="Сховище_бачить_документ_HSE400_року_лише_за_опублікованої_версії"/>.
/// ✎ P-0118-31/33: ключ виведено — дієве значення й мітку <c>Binding</c> дає біт зв'язки; змінна оточення без дії —
/// <see cref="Факти_ігнорують_виведений_ключ_значення_дає_лише_зв_язка"/>.
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
        // ConfigurationSourceLabel — загальний механізм міток; ключ узято довільний (виведений ключ HSE400 застосунок
        // більше не читає — P-0118-31/33).
        const string Key = "Calculations:B105LabelProbe";

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
    public async Task Факти_ігнорують_виведений_ключ_значення_дає_лише_зв_язка()
    {
        // ✎ P-0118-31/33: залишкова змінна live (ECR_Calculations__Hse400CompositionFromDocument) — без дії. Факти без
        // змінної, із "true" і з "false" однакові, а джерело — лише Default (зв'язки немає) або Binding (біт зв'язки).
        // Мутація: повернути читання ключа в SystemHealthController — джерело Environment, червоний.
        const string Variable = "ECR_" + "Calculations__Hse400CompositionFromDocument";
        var previous = Environment.GetEnvironmentVariable(Variable);
        var seen = new List<(bool Enabled, string? Source)>();
        try
        {
            foreach (var value in new[] { null, "true", "false" })
            {
                Environment.SetEnvironmentVariable(Variable, value);
                using var app = new EcrApiFactory(sql);
                using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, ViewHealth);
                var calculations = await ReadCalculationsAsync(client);
                seen.Add((
                    calculations.GetProperty("hse400CompositionFromDocument").GetBoolean(),
                    calculations.GetProperty("hse400CompositionSource").GetString()));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }

        Assert.All(seen, s => Assert.Contains(
            s.Source, new[] { SystemHostFacts.SourceDefault, SystemFactsCalculationsDto.SourceBinding }));
        Assert.All(seen, s => Assert.Equal(seen[0], s));

        // Без зв'язки читання з документа немає.
        Assert.All(seen, s => Assert.True(s.Source == SystemFactsCalculationsDto.SourceBinding || !s.Enabled));
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
