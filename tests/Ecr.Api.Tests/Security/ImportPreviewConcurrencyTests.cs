// tests/Ecr.Api.Tests/Security/ImportPreviewConcurrencyTests.cs

using System.Reflection;
using Ecr.Api.Controllers;
using Ecr.Api.Options;
using Ecr.Api.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// P1-04 = S1-02 (AUDIT-2026-10-09b): перегляд імпорту книги розбирає її повністю в пам'яті,
/// тож одночасних розборів не більше за межу, понад чергу — відмова 429.
/// </summary>
/// <remarks>
/// ⚠ Без бази й без годинника: обмежувач одночасності береться з політики напряму, а
/// «зайнятість» тримає незвільнена оренда — результат детермінований.
/// </remarks>
public sealed class ImportPreviewConcurrencyTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Третій_одночасний_перегляд_отримує_відмову_а_другий_чекає_першого()
    {
        var policy = new ImportPreviewRateLimitPolicy(Config(
            (ImportPreviewRateLimitPolicy.PermitKey, "1"),
            (ImportPreviewRateLimitPolicy.QueueLimitKey, "1")));
        var partition = policy.GetPartition(new DefaultHttpContext());
        using var limiter = partition.Factory(partition.PartitionKey);

        var first = await limiter.AcquireAsync();
        Assert.True(first.IsAcquired);

        // Другий стає в чергу: розбір першого ще триває.
        var second = limiter.AcquireAsync().AsTask();
        Assert.False(second.IsCompleted);

        // Третій — понад чергу: відмова одразу, а не очікування.
        using var third = await limiter.AcquireAsync();
        Assert.False(third.IsAcquired);

        // Перший закінчив — другий отримує дозвіл.
        first.Dispose();
        using var secondLease = await second;
        Assert.True(secondLease.IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Без_ключів_два_розбори_одночасно_і_розділ_один_на_процес()
    {
        var policy = new ImportPreviewRateLimitPolicy(new ConfigurationBuilder().Build());

        var alice = policy.GetPartition(new DefaultHttpContext());
        var bob = policy.GetPartition(new DefaultHttpContext());

        // Межа захищає пам'ять ПРОЦЕСУ: сто користувачів по одному перегляду з'їдають її
        // так само, як один зі ста. Мутація: розділ за користувачем — ключі різні.
        Assert.Equal(alice.PartitionKey, bob.PartitionKey);

        using var limiter = alice.Factory(alice.PartitionKey);
        using var one = limiter.AttemptAcquire();
        using var two = limiter.AttemptAcquire();
        using var three = limiter.AttemptAcquire();

        Assert.True(one.IsAcquired);
        Assert.True(two.IsAcquired);
        Assert.False(three.IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Перегляд_імпорту_стоїть_під_політикою()
    {
        var action = typeof(DocumentsController).GetMethod(nameof(DocumentsController.ImportPreview));
        Assert.NotNull(action);

        // Мутація: прибрати `[EnableRateLimiting]` з `ImportPreview` — тут null.
        var attribute = action.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(attribute);

        // Ім'я — ЛІТЕРАЛОМ: константа продукту рухалася б разом із перевіркою.
        Assert.Equal("import-preview", attribute.PolicyName);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Відмова_має_власний_ключ_каталогу_ECR_REQ_0429()
    {
        // Ключ — ЛІТЕРАЛОМ; рядок у сіді (en/ru/kz) тримає сторож каталогу помилок.
        Assert.Equal("err.ECR-REQ-0429.importBusy", ImportPreviewRateLimitPolicy.DetailKey);
        Assert.NotNull(new ImportPreviewRateLimitPolicy(new ConfigurationBuilder().Build()).OnRejected);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("Security:RateLimit:ImportPreviewConcurrency", "0")]
    [InlineData("Security:RateLimit:ImportPreviewQueueLimit", "-1")]
    public void Недійсна_межа_зупиняє_старт_з_ім_ям_ключа(string key, string value)
    {
        var problem = Assert.Single(EcrConfigurationValidation.Validate(Config((key, value))));
        Assert.Contains(key, problem, StringComparison.Ordinal);
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
}
