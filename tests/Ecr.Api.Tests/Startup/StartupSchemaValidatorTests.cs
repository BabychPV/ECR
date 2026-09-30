using System.Net;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Startup;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ФВ-7.9 на СПРАВЖНЬОМУ старті: <c>Program.cs</c> →
/// <c>StartupSequence.RunEcrStartupSequenceAsync</c> → <c>SchemaValidator</c>.
/// </summary>
/// <remarks>
/// ⛔ Перевіряється саме старт застосунку, а не валідатор напряму: доки
/// <c>SchemaValidator</c> ніхто не викликав, його власні тести були зелені, а
/// вимога — не виконана. Тут тест червоніє, щойно крок старту перестає його
/// звати.
///
/// Мутаційний доказ: (1) прибрати виклик <c>validator.ValidateAsync</c> у
/// <c>StartupSequence</c> — застосунок стартує на несумісному середовищі, і
/// тести зупинки червоніють; (2) зробити вимкнений RCSI помилкою в
/// <c>SchemaValidator.ValidateRuntimeOptionsAsync</c> — старт падає, і тест
/// Critical-у для RCSI червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class StartupSchemaValidatorTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.9")]
    public async Task Справна_схема_старт_піднімається()
    {
        using var factory = new EcrApiFactory(sql);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(factory.ServerLog, l => l.Contains("Старт: схема відповідає моделі.", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.9")]
    public void Непідтримувана_редакція_зупиняє_старт_з_кодом()
    {
        // SQL Server 2014 Standard: партиціонування, columnstore й компресія
        // там лише в Enterprise — модель архівації не працює в принципі.
        var failure = StartWith(new FakeCapabilities(SqlEditionMode.Standard, Major: 12, Rcsi: true));

        var reason = Incompatible(failure);
        Assert.Equal(ErrorCodes.StartupSchemaIncompatible, reason.ErrorCode);
        Assert.StartsWith("ECR-SYS-5031: ", reason.Message, StringComparison.Ordinal);
        Assert.Contains("партиціонування", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.9")]
    public async Task База_новіша_за_збірку_зупиняє_старт_з_кодом()
    {
        // Відкат версії застосунку на вже мігровану базу: у історії є
        // міграція, якої збірка не знає.
        const string Ghost = "29991231235959_FromTheFuture";
        await ExecuteAsync(
            "INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@id, N'99.0.0')",
            ("@id", Ghost));
        try
        {
            var failure = StartWith(capabilities: null);

            var reason = Incompatible(failure);
            Assert.Equal(ErrorCodes.StartupSchemaIncompatible, reason.ErrorCode);
            Assert.Contains(Ghost, reason.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync("DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = @id", ("@id", Ghost));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.9")]
    [Trait("Requirement", "D-102")]
    public async Task Вимкнений_RCSI_старт_піднімається_з_Critical_у_журналі()
    {
        // ⚠ Не зупинка: вмикання RCSI — операція DBA у вікні обслуговування,
        // застосунок сам цього виправити не може (а розгортання виконує 06-rcsi.sql
        // ДО старту). Але й не «попередження» (було до D-102/ФВ-7.9): рівень Critical
        // потрапляє в ServerErrors (Error і вище), а /health/ready дає 503 —
        // окремий тест DatabaseHealthRcsiOffTests.
        using var baseFactory = new EcrApiFactory(sql);
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(
            s => Replace(s, new FakeCapabilities(SqlEditionMode.Enterprise, Major: 16, Rcsi: false))));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(baseFactory.ServerLog,
            l => l.Contains("Старт (критично): RCSI вимкнено", StringComparison.Ordinal)
                 && l.Contains("06-rcsi.sql", StringComparison.Ordinal));

        // Той самий рядок — серед помилок (рівень >= Error), а не серед попереджень.
        Assert.Contains(baseFactory.ServerErrors,
            l => l.Contains("Старт (критично): RCSI вимкнено", StringComparison.Ordinal));
        Assert.DoesNotContain(baseFactory.ServerLog,
            l => l.Contains("Старт (попередження): RCSI", StringComparison.Ordinal));
    }

    /// <summary>Піднімає застосунок і повертає виняток старту (або <c>null</c>).</summary>
    private Exception? StartWith(FakeCapabilities? capabilities)
    {
        var baseFactory = new EcrApiFactory(sql);
        var factory = capabilities is null
            ? baseFactory
            : baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s => Replace(s, capabilities)));
        try
        {
            return Record.Exception(() => factory.CreateClient().Dispose());
        }
        finally
        {
            // Хост, що не стартував, може кинути й на звільненні — це не
            // предмет тесту.
            _ = Record.Exception(factory.Dispose);
            _ = Record.Exception(baseFactory.Dispose);
        }
    }

    private static SchemaIncompatibleException Incompatible(Exception? failure)
    {
        Assert.True(failure is not null, "Застосунок стартував на несумісному середовищі.");

        var reason = Flatten(failure!).OfType<SchemaIncompatibleException>().FirstOrDefault();
        Assert.True(reason is not null, $"Старт зупинено не перевіркою сумісності; отримано: {failure}");
        return reason!;
    }

    private static void Replace(IServiceCollection services, ISqlCapabilities capabilities)
    {
        services.RemoveAll<ISqlCapabilities>();
        services.AddSingleton(capabilities);
    }

    private static IEnumerable<Exception> Flatten(Exception root)
    {
        var stack = new Stack<Exception>([root]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    stack.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                stack.Push(inner);
            }
        }
    }

    private async Task ExecuteAsync(string text, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Можливості СУБД без звернення до сервера.</summary>
    /// <remarks>
    /// Підміняються саме вони, а не сервер: підняти SQL Server 2014 Standard
    /// заради тесту неможливо, а перевіряється рішення старту, не вміння СУБД.
    /// </remarks>
    private sealed record FakeCapabilities(SqlEditionMode Mode, int Major, bool Rcsi) : ISqlCapabilities
    {
        public SqlEditionMode EffectiveMode => Mode;

        public string EditionName => Mode.ToString();

        public int ProductMajorVersion => Major;

        public bool IsReadCommittedSnapshotOn => Rcsi;

        public bool SupportsOnlineIndexRebuild => Mode == SqlEditionMode.Enterprise;

        public bool SupportsResourceGovernor => Mode == SqlEditionMode.Enterprise;

        public int ArchiveBatchSize => Mode == SqlEditionMode.Enterprise ? 2_000_000 : 500_000;
    }
}
