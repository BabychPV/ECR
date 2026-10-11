using System.Net;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// X8-07: увесь <c>Ecr.Api.Tests</c> ішов у <c>Development</c> із <c>Database:EditionMode = Standard</c>, а прод працює
/// в <c>Enterprise</c> (D-101): <c>ArchiveBatchSize</c>, <c>SupportsOnlineIndexRebuild</c> і
/// <c>SupportsResourceGovernor</c> там інші, і HTTP-конвеєр цієї гілки не проходив жодного разу. Фабрика тепер
/// вміє задати режим (<c>editionMode</c>), а справжній старт застосунку його застосовує.
/// </summary>
/// <remarks>
/// Мутації (CI): не записувати змінну оточення режиму у фабриці → <c>Enterprise_…</c> червоний (режим лишається
/// Standard); не прибирати змінну в <c>Dispose</c> → червоний <c>Типова_фабрика_після_Enterprise_лишається_Standard</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class EditionModeFactoryTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Enterprise_режим_проходить_старт_і_міняє_можливості_СУБД()
    {
        using var factory = new EcrApiFactory(sql, editionMode: "Enterprise");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var capabilities = factory.Services.GetRequiredService<ISqlCapabilities>();
        Assert.Equal(SqlEditionMode.Enterprise, capabilities.EffectiveMode);
        Assert.True(capabilities.SupportsOnlineIndexRebuild);
        Assert.True(capabilities.SupportsResourceGovernor);
        Assert.Equal(2_000_000, capabilities.ArchiveBatchSize);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Типова_фабрика_після_Enterprise_лишається_Standard()
    {
        using (var enterprise = new EcrApiFactory(sql, editionMode: "Enterprise"))
        {
            using var client = enterprise.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
        }

        // Змінна оточення — глобальна на процес: Enterprise-фабрика не має лишити її наступним тестам.
        Assert.Null(Environment.GetEnvironmentVariable("ECR_Database__EditionMode"));

        using var standard = new EcrApiFactory(sql);
        using var standardClient = standard.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await standardClient.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);

        var capabilities = standard.Services.GetRequiredService<ISqlCapabilities>();
        Assert.Equal(SqlEditionMode.Standard, capabilities.EffectiveMode);
        Assert.False(capabilities.SupportsOnlineIndexRebuild);
        Assert.Equal(500_000, capabilities.ArchiveBatchSize);
    }
}
