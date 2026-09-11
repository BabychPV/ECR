using System.Diagnostics;
using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// <see cref="PiSqlClientDataSource"/> раніше не повторювала жодної
/// транзитивної відмови — ні відкриття з'єднання, ні читання (`Q-251`,
/// аудит). Сусідній <see cref="PiWebApiDataSource"/> уже це робив
/// (<c>MaxAttempts</c>, подвоєння затримки); тут той самий цикл виносить у
/// <see cref="PiSqlClientDataSource.RetryAsync{T}"/>.
/// </summary>
/// <remarks>
/// ⛔ Живого PI SQL Client (RTQP) у контурі розробки немає, а
/// <c>System.Data.Odbc.OdbcException</c> ззовні збірки не сконструюєш —
/// обидва конструктори внутрішні для <c>System.Data.Odbc</c>. Тому тут два
/// незалежні докази:
/// <list type="number">
/// <item>прямі тести самого циклу повторів — підставним <c>Func&lt;Task&lt;T&gt;&gt;</c>
/// і класифікатором винятку, без жодного ODBC;</item>
/// <item>наскрізний тест через публічний <see cref="PiSqlClientDataSource.DiscoverAsync"/>
/// з навмисно неіснуючим ODBC-драйвером — це РЕАЛЬНА відмова від справжньої
/// підсистеми ODBC Windows (вона є в кожному контурі незалежно від PI), а не
/// підміна; вимірюється, що спроби справді рознесені в часі затримками.</item>
/// </list>
/// </remarks>
public sealed class PiSqlClientDataSourceRetryTests
{
    [Fact]
    public async Task RetryAsync_повторює_транзитивну_відмову_і_зрештою_встигає()
    {
        var attempts = 0;

        Task<int> Operation()
        {
            attempts++;
            return attempts < 2
                ? throw new InvalidOperationException("транзитивна")
                : Task.FromResult(42);
        }

        var result = await PiSqlClientDataSource.RetryAsync(
            Operation, static _ => true, CancellationToken.None);

        Assert.Equal(42, result);

        // ⛔ Мутаційна перевірка: код без ретраю кидав би на першій же спробі
        // (attempts == 1) і сюди взагалі не дійшов би — Assert.Equal(42, ...)
        // вище впав би першим на винятку з операції.
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RetryAsync_не_повторює_нетранзитивну_відмову_накшталт_автентифікації()
    {
        var attempts = 0;

        Task<int> Operation()
        {
            attempts++;
            throw new InvalidOperationException("автентифікація відмовлена — так само, як SQLSTATE 28000");
        }

        // ⚠ isTransient повертає false — так само, як IsTransientOdbcFailure
        // для OdbcException з SQLSTATE 28000 у виробничому коді.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PiSqlClientDataSource.RetryAsync(Operation, static _ => false, CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Contains("автентифікація", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryAsync_припиняє_повтори_рівно_після_MaxAttempts_спроб()
    {
        var attempts = 0;

        Task<int> Operation()
        {
            attempts++;
            throw new InvalidOperationException($"спроба {attempts}");
        }

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PiSqlClientDataSource.RetryAsync(Operation, static _ => true, CancellationToken.None));

        Assert.Equal(PiSqlClientDataSource.MaxAttempts, attempts);
        Assert.Equal($"спроба {PiSqlClientDataSource.MaxAttempts}", thrown.Message);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task DiscoverAsync_ретраїть_справжню_відмову_ODBC_перш_ніж_здатися()
    {
        // ⚠ Драйвера з такою назвою не існує в жодному контурі — це
        // СПРАВЖНЯ відмова менеджера ODBC-драйверів Windows (SQLSTATE
        // "IM002", не "28000"), а не підмінений транспорт. Вона класифікується
        // як транзитивна (не автентифікація) і тому ретраїться.
        var store = Substitute.For<ICollectionStore>();
        var secrets = Substitute.For<ISecretProvider>();

        var dataSource = new DataSource(
            EcrCode.Create("Q251_NO_DRIVER"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "Неіснуючий драйвер" }),
            ExternalTransport.PiSqlClient,
            "Driver={Ecr-Q251-Неіснуючий-Драйвер};Server=nonexistent;",
            "Q251.Secret");

        store.FindDataSourceAsync(1, Arg.Any<CancellationToken>()).Returns(dataSource);
        secrets.Find("Q251.Secret").Returns((string?)null);

        var sut = new PiSqlClientDataSource(store, secrets);

        var stopwatch = Stopwatch.StartNew();

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => sut.DiscoverAsync(1, CancellationToken.None));

        stopwatch.Stop();

        Assert.Equal(ErrorCodes.SourceUnavailable, thrown.ErrorCode);

        // ⛔ Мутаційна перевірка (RED без фіксу, GREEN із фіксом): без ретраю
        // ODBC відмовляє на неіснуючому драйвері за мілісекунди — жодної
        // затримки в циклі немає. Із фіксом код проходить принаймні один
        // цикл затримки (RetryDelay = 2s) перш ніж кинути.
        Assert.True(
            stopwatch.Elapsed >= PiSqlClientDataSource.RetryDelay,
            $"Очікували принаймні одну затримку ретраю (~{PiSqlClientDataSource.RetryDelay}), " +
            $"минуло лише {stopwatch.Elapsed}.");
    }
}
