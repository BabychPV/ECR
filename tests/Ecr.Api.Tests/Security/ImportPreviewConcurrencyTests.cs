// tests/Ecr.Api.Tests/Security/ImportPreviewConcurrencyTests.cs

using System.Reflection;
using System.Runtime.CompilerServices;
using Ecr.Api.Controllers;
using Ecr.Api.Options;
using Ecr.Api.Security;
using Ecr.Application.Errors;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// P1-04 = S1-02 (AUDIT-2026-10-09b): перегляд імпорту книги розбирає її повністю в пам'яті,
/// тож одночасних розборів не більше за межу, понад чергу — відмова 429.
/// </summary>
/// <remarks>
/// ⛔ AN-123 (R1-04 = R2-02, AUDIT-2026-10-09c): місце береться в ДІЇ, після прив'язки
/// <c>IFormFile</c> (<see cref="ImportPreviewGate"/>), а не в <c>RateLimitingMiddleware</c>
/// до неї — повільне вивантаження більше не займає місця розбору.
///
/// ⚠ Без бази й без годинника: «зайнятість» тримає незвільнена оренда — результат
/// детермінований.
/// </remarks>
public sealed class ImportPreviewConcurrencyTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Третій_одночасний_розбір_отримує_429_а_другий_чекає_першого()
    {
        using var gate = new ImportPreviewGate(Config(
            (ImportPreviewRateLimitPolicy.PermitKey, "1"),
            (ImportPreviewRateLimitPolicy.QueueLimitKey, "1")));

        var first = await gate.EnterAsync(CancellationToken.None);
        Assert.True(first.IsAcquired);

        // Другий стає в чергу: розбір першого ще триває.
        var second = gate.EnterAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);

        // Третій — понад чергу: відмова одразу, а не очікування.
        var refusal = await Assert.ThrowsAsync<BusinessRuleException>(() => gate.EnterAsync(CancellationToken.None));
        Assert.Equal("ECR-REQ-0429", refusal.ErrorCode);
        Assert.Equal("err.ECR-REQ-0429.importBusy", refusal.Details?["messageKey"]);

        // Перший закінчив — другий отримує дозвіл.
        first.Dispose();
        using var secondLease = await second;
        Assert.True(secondLease.IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Без_ключів_два_розбори_одночасно_і_черга_чотири()
    {
        var options = new ImportPreviewRateLimitPolicy(new ConfigurationBuilder().Build()).LimiterOptions();

        Assert.Equal(2, options.PermitLimit);
        Assert.Equal(4, options.QueueLimit);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Межа_перегляду_береться_в_дії_а_не_в_проміжному_ПЗ_до_прив_язки_тіла()
    {
        var action = typeof(DocumentsController).GetMethod(nameof(DocumentsController.ImportPreview));
        Assert.NotNull(action);

        // ⛔ Мутація: повернути `[EnableRateLimiting]` — оренда знову береться ДО прив'язки
        // `IFormFile`, і вивантаження тіла займає місце розбору.
        Assert.Null(action.GetCustomAttribute<EnableRateLimitingAttribute>());

        // Межа — служба, яку дія отримує вже з прив'язаним файлом.
        var gate = Assert.Single(action.GetParameters(), p => p.ParameterType == typeof(ImportPreviewGate));
        Assert.NotNull(gate.GetCustomAttribute<FromServicesAttribute>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Межа_вичерпана_дія_відповідає_429_не_торкаючись_розбору()
    {
        using var gate = new ImportPreviewGate(Config(
            (ImportPreviewRateLimitPolicy.PermitKey, "1"),
            (ImportPreviewRateLimitPolicy.QueueLimitKey, "0")));
        using var busy = await gate.EnterAsync(CancellationToken.None);

        // ⚠ Контролер без залежностей: до обробника перегляду дія дійти не має права.
        var controller = (DocumentsController)RuntimeHelpers.GetUninitializedObject(typeof(DocumentsController));
        var file = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "book.xlsx");

        // ⛔ Мутація: прибрати `EnterAsync` з дії — тут NullReferenceException обробника, не 429.
        var refusal = await Assert.ThrowsAsync<BusinessRuleException>(
            () => controller.ImportPreview(1, file, gate, CancellationToken.None));
        Assert.Equal("ECR-REQ-0429", refusal.ErrorCode);
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
