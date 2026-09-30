// tests/Ecr.Application.Tests/Templates/PublishViewsBestEffortTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Рішення координатора 2026-09-30: збій генерації вʼюх <c>rpt.v_*</c> (таблиця
/// &gt; 250 колонок — 50422, зіткнення імен — 50409) НЕ відкочує публікацію версії
/// шаблону: версія лишається <c>Published</c>, причина — у журналі й у
/// <see cref="IReportViewStatus"/> (звідти її бере <c>/health/ready</c>), а повтор
/// прибирає стан.
/// </summary>
/// <remarks>
/// ⚠ Справжня СУБД і справжній обробник публікації. Мутація (прогнано): повернути
/// виклик <c>reportViews.GenerateAsync</c> у <c>PublishLockedAsync</c> (усередині
/// транзакції) → публікація кидає й відкочується, обидва тести червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishViewsBestEffortTests(SqlServerFixture sql)
{
    private const int Actor = 9;
    private static readonly DateTime Now = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Таблиця_понад_250_колонок_публікацію_не_відкочує_а_повтор_після_виправлення_чистить_стан()
    {
        var code = $"W{_tag}";
        var versionId = await ArrangeDraftAsync(code, "S", "T", columns: 251);
        var status = new ReportViewStatus();
        var log = new CapturingLogger();

        await PublishAsync(versionId, status, log);

        // Публікація пройшла: версія Published, подія публікації записана.
        Assert.Equal((byte)TemplateVersionStatus.Published, await VersionStatusAsync(versionId));
        Assert.Equal(1, await PublicationEventsAsync(versionId));

        // Вʼюхи немає; причина — з кодом і назвою шаблону (для health і журналу).
        Assert.Empty(await ViewsLikeAsync(code));
        var failure = Assert.Single(status.Snapshot());
        Assert.Equal(versionId, failure.TemplateVersionId);
        Assert.Equal(50422, failure.Code);
        Assert.Contains(code, failure.Message, StringComparison.Ordinal);
        Assert.Contains("251", failure.Message, StringComparison.Ordinal);

        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(code, warning.Message, StringComparison.Ordinal);
        Assert.Contains("50422", warning.Message, StringComparison.Ordinal);

        // Виправлення (колонок лишилось 250) і повтор — як старт чи EXEC процедури.
        await ShrinkAsync(versionId);

        await using (var db = Context())
        {
            await new ReportViewGenerator(db, status).GenerateAsync(versionId, CancellationToken.None);
        }

        Assert.Empty(status.Snapshot());
        Assert.Single(await ViewsLikeAsync(code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Зіткнення_імен_вʼюх_публікацію_не_відкочує_і_фіксується_з_кодом_50409()
    {
        // `Q_X`/`S`/`T` і `Q`/`X_S`/`T` дають одне ім'я `v_Q_X_S_T_v1`.
        var q = $"Q{_tag}";
        var first = await ArrangeDraftAsync($"{q}_X", "S", "T", columns: 1);
        var second = await ArrangeDraftAsync(q, "X_S", "T", columns: 1);
        var status = new ReportViewStatus();

        await PublishAsync(first, status, new CapturingLogger());
        Assert.Empty(status.Snapshot());

        var log = new CapturingLogger();
        await PublishAsync(second, status, log);

        Assert.Equal((byte)TemplateVersionStatus.Published, await VersionStatusAsync(second));
        var failure = Assert.Single(status.Snapshot());
        Assert.Equal(50409, failure.Code);
        Assert.Equal(second, failure.TemplateVersionId);
        Assert.Contains($"v_{q}_X_S_T_v1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("50409", Assert.Single(log.Entries).Message, StringComparison.Ordinal);
    }

    // ── підготовка ───────────────────────────────────────────────────────────

    private async Task PublishAsync(int versionId, IReportViewStatus status, ILogger<PublishTemplateVersionHandler> log)
    {
        await using var db = Context();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        _user.UserId.Returns(Actor);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }
                .Permission(PublishTemplateVersionHandler.Permission).Permission("Template.View").Build());

        var handler = new PublishTemplateVersionHandler(
            new Repository<TemplateVersion, int>(db),
            new TemplateVersionStore(db),
            new RealFormulaEngine(),
            new CalculationBindingStore(db),
            new MetadataCache(memory, db),
            new UnitCatalog(db),
            _access,
            _user,
            new AuditWriter(db),
            new UnitOfWork(db),
            new TestClock(Now),
            new ReportViewGenerator(db, status),
            log,
            status);

        await handler.PublishAsync(versionId, Actor, "best-effort", CancellationToken.None);
    }

    private async Task<int> ArrangeDraftAsync(string template, string sheet, string table, int columns)
    {
        await using var db = Context();
        var t = new Template(EcrCode.Create(template), Text(template), 1, Now);
        db.Templates.Add(t);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(t.Id, "1", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var s = new SheetDef(version.Id, EcrCode.Create(sheet), Text(sheet), 1);
        db.SheetDefs.Add(s);
        await db.SaveChangesAsync();

        var td = new TableDef(
            s.Id, EcrCode.Create(table), Text(table), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        db.TableDefs.Add(td);
        await db.SaveChangesAsync();

        for (var i = 1; i <= columns; i++)
        {
            db.ColumnDefs.Add(new ColumnDef(td.Id, EcrCode.Create($"C{i}"), Text($"C{i}"), i, CellDataType.Decimal));
        }

        await db.SaveChangesAsync();
        return version.Id;
    }

    private async Task<byte> VersionStatusAsync(int versionId)
        => (byte)(await ScalarAsync("SELECT Status FROM cfg.TemplateVersion WHERE Id = @id", ("@id", versionId)))!;

    private async Task<int> PublicationEventsAsync(int versionId)
        => (int)(await ScalarAsync(
            "SELECT COUNT(*) FROM aud.PublicationEvent WHERE EntityType = N'TemplateVersion' AND EntityId = @id",
            ("@id", versionId)))!;

    private async Task<List<string>> ViewsLikeAsync(string templateCode)
    {
        var result = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.views WHERE schema_id = SCHEMA_ID(N'rpt') AND name LIKE @p";
        command.Parameters.AddWithValue("@p", $"v[_]{templateCode}[_]%");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private async Task<object?> ScalarAsync(string text, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteScalarAsync();
    }

    /// <summary>Зменшує таблицю версії до 250 колонок (для повтору генерації).</summary>
    private async Task ShrinkAsync(int versionId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE cd SET IsDeleted = 1
              FROM cfg.ColumnDef cd
              JOIN cfg.TableDef td ON td.Id = cd.TableDefId
              JOIN cfg.SheetDef sd ON sd.Id = td.SheetDefId
             WHERE sd.TemplateVersionId = @tv AND cd.Code = N'C251';
            """;
        command.Parameters.AddWithValue("@tv", versionId);
        await command.ExecuteNonQueryAsync();
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(60))
            .Options);

    private sealed class CapturingLogger : ILogger<PublishTemplateVersionHandler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
