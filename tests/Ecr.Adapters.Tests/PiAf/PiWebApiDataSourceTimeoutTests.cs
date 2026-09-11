using System.Diagnostics;
using Ecr.Adapters.PiAf;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// `HttpClient` для <see cref="PiWebApiDataSource"/> має набагато коротший
/// таймаут за дефолтні 100 с, і `GetAsync` дійсно завершується — не чекає
/// «напівживого» джерела нескінченно (Q-250).
/// </summary>
/// <remarks>
/// ⚠ Символ дефекту: без явного <c>ConfigureHttpClient(c =&gt; c.Timeout = …)</c>
/// у <see cref="DependencyInjection.AddPiAfAdapters"/> клієнт живе за
/// дефолтним <see cref="HttpClient.Timeout"/> — сто секунд. `GetAsync`
/// повторює відповідь 5xx/таймаут до трьох разів із паузами 2 с і 4 с
/// (<see cref="PiWebApiDataSource.MaxAttempts"/>/<see cref="PiWebApiDataSource.RetryDelay"/>),
/// тож одна лише повільна відповідь джерела розтягувала одну спробу збору до
/// ~306 с — і `CollectionRunner.RunAsync` це число ще й множить, ідучи по
/// інтервалах наздоганяння та атрибутах ПОСЛІДОВНО.
/// </remarks>
public sealed class PiWebApiDataSourceTimeoutTests
{
    /// <summary>
    /// `AddPiAfAdapters` реєструє `HttpClient` для <see cref="PiWebApiDataSource"/>
    /// із таймаутом набагато коротшим за дефолтні сто секунд.
    /// </summary>
    /// <remarks>
    /// ⚠ Тест на САМЕ ЦЕ значення, а не «якийсь ліміт узагалі»: до фіксу DI
    /// не викликає <c>ConfigureHttpClient</c> взагалі, і `HttpClientFactory`
    /// віддає клієнта з <see cref="HttpClient.Timeout"/> за замовчуванням —
    /// рівно сто секунд (<c>Assert.Equal(30s, …)</c> провалюється на коді ДО
    /// фіксу: там `client.Timeout == TimeSpan.FromSeconds(100)`).
    /// </remarks>
    [Fact]
    public void AddPiAfAdapters_РеєструєНабагатоКоротшийТаймаутНіжДефолтні100с()
    {
        var services = new ServiceCollection();
        services.AddPiAfAdapters();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // ⚠ Ім'я іменованого клієнта для типізованого `AddHttpClient<T>()` —
        // коротке ім'я типу (без простору імен): так побудований
        // `Microsoft.Extensions.Http` (`TypeNameHelper.GetTypeDisplayName`,
        // `fullName: false`), і саме під цим ім'ям `IHttpClientFactory`
        // застосовує `ConfigureHttpClient` із `DependencyInjection.cs`.
        var client = factory.CreateClient(nameof(PiWebApiDataSource));

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        Assert.NotEqual(TimeSpan.FromSeconds(100), client.Timeout);
    }

    /// <summary>
    /// Джерело, що ніколи не відповідає (з'єднання приймається, тіла не
    /// віддає), не тримає <see cref="PiWebApiDataSource.ReadAsync"/> сотні
    /// секунд — воно завершується (винятком) у межах невеликого, наперед
    /// відомого вікна.
    /// </summary>
    /// <remarks>
    /// ⚠ Таймаут клієнта тут — 300 мс, а не прод-30 с: тест доводить САМ
    /// МЕХАНІЗМ (ретрай + `HttpClient.Timeout` дійсно скасовує запит, що
    /// завис), не конкретне число з DI (те доводить попередній тест) — і не
    /// чекає реальних секунд у CI. `[Fact(Timeout = …)]` ловить регресію
    /// швидко: якби механізм не спрацював (клієнт узагалі не має таймауту),
    /// виклик не завершився б НІКОЛИ, і xunit провалить тест за 15 с, а не
    /// зависне назавжди.
    /// </remarks>
    [Fact(Timeout = 15000)]
    public async Task ReadAsync_ДжерелоНіколиНеВідповідає_КидаєНеЧекаючиДефолтних100с()
    {
        using var handler = new NeverRespondingHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(300) };

        var store = Substitute.For<ICollectionStore>();
        var dataSource = new DataSource(
            EcrCode.Create("PIAF"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
            ExternalTransport.PiWebApi,
            "https://pi.example",
            "PiAf.Primary");
        store.FindDataSourceAsync(1, Arg.Any<CancellationToken>()).Returns(dataSource);

        var secrets = Substitute.For<ISecretProvider>();
        secrets.Find(Arg.Any<string>()).Returns((string?)null);

        var sut = new PiWebApiDataSource(http, store, secrets);
        var request = new CollectionRequest(
            1, 42, "tag", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, MaxPoints: 5_000);

        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ReadAsync(request, CancellationToken.None));

        stopwatch.Stop();

        // Три спроби по ~300 мс плюс паузи ретраю (2 с + 4 с) — разом
        // помітно менше за десять секунд, а не ~306 с, які дав би дефолтний
        // 100-секундний `HttpClient.Timeout` (симптом Q-250).
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"мало завершитися набагато швидше за 100с-дефолт: минуло {stopwatch.Elapsed}.");
    }

    /// <summary>Обробник, що ніколи сам не завершує запит — лише за скасуванням токена.</summary>
    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // ⚠ Саме так поводиться «напівживе» джерело з симптому Q-250:
            // TCP-з'єднання прийняте, відповідь не приходить. Завершує цей
            // виклик лише `HttpClient.Timeout` — власним скасуванням `ct`.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException("Unreachable.");
        }
    }
}
