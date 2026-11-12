// tests/Ecr.Infrastructure.Tests/Jobs/FlertCompositionSyncJobTests.cs
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Flert;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Синк складу газу FLERT → дзеркало ECR (<c>D-476</c>, план FLERT F0 + F2) на СПРАВЖНЬОМУ SQL Server і справжньому
/// контейнері (writer, одиниця роботи, ключі, аудит — як у проді). Підроблено лише FLERT (<see cref="IExternalTableSource"/>):
/// тестам FLERT не потрібен.
/// </summary>
/// <remarks>
/// Кожен тест — свої довідники з міткою (<see cref="FlertRegistryCodes"/>) і своє джерело: спільна база <c>EcrTest_*</c>.
/// <para>
/// Мутаційні докази (локально, не в коміті): (1) прибрати запобіжник <c>table.Rows.Count == 0</c> у <c>RunAsync</c> →
/// <c>Порожній_результат_не_видаляє_нічого_статус_Degraded</c> червоний (порожнє дзеркало вимикає всі записи);
/// (2) дописати до коду рядка складу мітку прогону (<c>Guid.NewGuid()</c>) у <c>FlertCompositionMapping.Components</c> →
/// <c>Перший_прогін_заводить_довідники_і_копію_другий_дає_нуль_змін_без_нової_ревізії</c> червоний;
/// (3) у правилі псевдонімів брати довгу назву першою → той самий тест червоний на <c>CO</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class FlertCompositionSyncJobTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 11, 23, 0, 0, DateTimeKind.Utc);
    private static readonly string[] StreamValidity = ["Stream_ValidFrom", "Stream_ValidTo"];

    private static readonly string[] TypeValidity = ["StreamType_ValidFrom", "StreamType_ValidTo"];

    private static readonly DateTime Since2022 = new(2022, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private FlertRegistryCodes Codes => new($"STREAM_{_tag}", $"STREAM_CASE_{_tag}", $"GAS_COMPOSITION_{_tag}", $"COMPONENT_{_tag}");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Перший_прогін_заводить_довідники_і_копію_другий_дає_нуль_змін_без_нової_ревізії()
    {
        var source = new FakeFlert(Snapshot());
        var dataSource = await ArrangeSourceAsync();
        await using var provider = BuildProvider();

        var first = await RunAsync(provider, source, dataSource);

        Assert.Equal(FlertCompositionSyncJob.Succeeded, first.Status);
        Assert.Null(first.Reason);
        Assert.Equal(4, first.CreatedRegistries.Count);
        Assert.Equal(FlertCompositionSchema.Components.Count, first.ComponentsAdded.Count);
        Assert.Equal(2, first.Counts!.Added["streams"]);
        Assert.Equal(3, first.Counts.Added["cases"]);
        Assert.Equal(4, first.Counts.Added["compositions"]);

        // Конфлікт псевдонімів — зауваження в підсумку, у копії — коротка назва.
        Assert.Contains(first.Issues, i => i.Kind == FlertCompositionMapping.AliasConflict && i.Subject == "C10_20220101.CO");
        Assert.Contains(first.Issues, i => i.Kind == FlertCompositionMapping.NoComposition && i.Subject == "C12_20220101");

        await using (var db = Context())
        {
            var defs = await db.RegistryDefs.AsNoTracking().Where(d => d.Code.EndsWith(_tag)).ToListAsync();
            Assert.Equal(RegistrySourceKind.External, defs.Single(d => d.Code == Codes.Stream).SourceKind);
            Assert.Equal(RegistrySourceKind.External, defs.Single(d => d.Code == Codes.Composition).SourceKind);
            Assert.Equal(RegistrySourceKind.Local, defs.Single(d => d.Code == Codes.Component).SourceKind);
            Assert.True(defs.Single(d => d.Code == Codes.Case).IsTemporal);

            var caseDef = defs.Single(d => d.Code == Codes.Case);
            var rule = await db.RegistryRuleDefs.AsNoTracking().SingleAsync(r => r.RegistryDefId == caseDef.Id);
            Assert.Equal(FlertCompositionSchema.Sum100Rule, rule.Code);
            Assert.Equal(ValidationSeverity.Warning, rule.Severity);
            Assert.Contains(Codes.Composition, rule.Expression, StringComparison.Ordinal);

            // Кожен створений довідник — рядок aud.StructureChange від svc-integration.
            var audited = 0;
            foreach (var def in defs)
            {
                audited += await db.Database
                    .SqlQuery<int>(
                        $"SELECT COUNT(*) AS [Value] FROM aud.StructureChange WHERE EntityType = N'cfg.RegistryDef' AND Operation = {FlertRegistryProvisioner.ProvisionOperation} AND EntityId = {def.Id}")
                    .SingleAsync();
            }

            Assert.Equal(4, audited);
        }

        Assert.Equal(1m, await DecimalAsync(Codes.Composition, "C10_20220101_CO", FlertCompositionSchema.RowMolPct));
        Assert.True(await BoolAsync(Codes.Case, "C12_20220101", FlertCompositionSchema.CaseNoComposition));
        Assert.Equal(new DateOnly(2022, 1, 1), (await EntryAsync(Codes.Case, "C10_20220101")).ValidFrom);

        var revisions = await RevisionsAsync();

        var second = await RunAsync(provider, source, dataSource);

        Assert.Equal(FlertCompositionSyncJob.Succeeded, second.Status);
        Assert.Empty(second.CreatedRegistries);
        Assert.Empty(second.ComponentsAdded);
        Assert.False(second.Counts!.AnyChange);
        Assert.Equal(revisions, await RevisionsAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Порожній_результат_не_видаляє_нічого_статус_Degraded()
    {
        var source = new FakeFlert(Snapshot());
        var dataSource = await ArrangeSourceAsync();
        await using var provider = BuildProvider();
        await RunAsync(provider, source, dataSource);
        var before = await ActiveCountsAsync();
        var revisions = await RevisionsAsync();

        source.Table = new ExternalTable(Snapshot().Columns, [], Truncated: false);
        var runId = await ExecuteAsync(provider, source, dataSource);

        await using var db = Context();
        var run = await db.MaintenanceRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        Assert.Equal(FlertCompositionSyncJob.Degraded, run.Status);
        Assert.Contains("\"reason\":\"emptyResult\"", run.DetailsJson, StringComparison.Ordinal);
        Assert.Equal(before, await ActiveCountsAsync());
        Assert.Equal(revisions, await RevisionsAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Зміна_у_FLERT_дає_нову_ревізію_лише_змінених_і_вимикає_зникле_а_повернення_вмикає()
    {
        var source = new FakeFlert(Snapshot());
        var dataSource = await ArrangeSourceAsync();
        await using var provider = BuildProvider();
        await RunAsync(provider, source, dataSource);
        var revisions = await RevisionsAsync();

        // Methane 97 → 96.5, H2S зник (NULL), кейс C11 зник зовсім.
        source.Table = Snapshot(methane: 96.5m, h2s: null, withSecondCase: false);
        var changed = await RunAsync(provider, source, dataSource);

        Assert.Equal(FlertCompositionSyncJob.Succeeded, changed.Status);
        Assert.Equal(1, changed.Counts!.Updated["compositions"]);
        Assert.Equal(0, changed.Counts.Updated["streams"]);
        Assert.Equal(1, changed.Counts.Deactivated["cases"]);
        Assert.Equal(2, changed.Counts.Deactivated["compositions"]);   // H2S кейсу C10 і Methane кейсу C11
        Assert.Equal(96.5m, await DecimalAsync(Codes.Composition, "C10_20220101_Methane", FlertCompositionSchema.RowMolPct));
        Assert.False((await EntryAsync(Codes.Case, "C11_20220101")).IsActive);
        Assert.False((await EntryAsync(Codes.Composition, "C10_20220101_H2S")).IsActive);

        var after = await RevisionsAsync();
        Assert.True(after[Codes.Composition] > revisions[Codes.Composition]);
        Assert.Equal(revisions[Codes.Stream], after[Codes.Stream]);

        // Повернення у FLERT вмикає записи знову, без нових кодів.
        source.Table = Snapshot();
        var back = await RunAsync(provider, source, dataSource);
        Assert.Equal(1, back.Counts!.Reactivated["cases"]);
        Assert.Equal(2, back.Counts.Reactivated["compositions"]);
        Assert.Equal(0, back.Counts.Added["compositions"]);
        Assert.True((await EntryAsync(Codes.Case, "C11_20220101")).IsActive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task FLERT_недоступний_прогін_падає_кодом_інтеграції_і_копія_незмінна()
    {
        var source = new FakeFlert(Snapshot());
        var dataSource = await ArrangeSourceAsync();
        await using var provider = BuildProvider();
        await RunAsync(provider, source, dataSource);
        var before = await ActiveCountsAsync();
        var revisions = await RevisionsAsync();

        source.Failure = new BusinessRuleException(
            "ECR-INT-0503",
            "SQL-джерело недоступне.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-INT-0503.connectFailed", ["dataSource"] = dataSource });

        var started = await LastRunIdAsync();
        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(() => ExecuteAsync(provider, source, dataSource));
        Assert.Equal("ECR-INT-0503", thrown.ErrorCode);

        await using (var db = Context())
        {
            var run = await db.MaintenanceRuns.AsNoTracking()
                .Where(r => r.JobCode == FlertCompositionSyncJob.Code && r.Id > started)
                .OrderByDescending(r => r.Id)
                .FirstAsync();
            Assert.Equal(FlertCompositionSyncJob.Failed, run.Status);
        }

        Assert.Equal(before, await ActiveCountsAsync());
        Assert.Equal(revisions, await RevisionsAsync());

        // Дрейф схеми (зникла колонка) — теж нічого не записано.
        source.Failure = null;
        var drifted = Snapshot();
        source.Table = new ExternalTable(
            [.. drifted.Columns.Where(c => c != "Sulfur")],
            [.. drifted.Rows.Select(r => (IReadOnlyList<object?>)[.. r.Where((_, i) => drifted.Columns[i] != "Sulfur")])],
            Truncated: false);
        var refused = await RunAsync(provider, source, dataSource);
        Assert.Equal(FlertCompositionSyncJob.Failed, refused.Status);
        Assert.Equal("mappingErrors", refused.Reason);
        Assert.Equal(revisions, await RevisionsAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Наявний_несумісний_довідник_не_редагується_а_синк_відмовляє_нічого_не_записавши()
    {
        await using (var db = Context())
        {
            db.RegistryDefs.Add(new Ecr.Domain.Entities.Configuration.RegistryDef(
                EcrCode.Create(Codes.Stream), new LocalizedText(new Dictionary<string, string> { ["en"] = "Streams by hand" }), isTemporal: false));
            await db.SaveChangesAsync();
        }

        var dataSource = await ArrangeSourceAsync();
        await using var provider = BuildProvider();

        var outcome = await RunAsync(provider, new FakeFlert(Snapshot()), dataSource);

        Assert.Equal(FlertCompositionSyncJob.Failed, outcome.Status);
        Assert.Equal("registryIncompatible", outcome.Reason);
        Assert.Contains(outcome.Issues, i => i.Subject.StartsWith($"{Codes.Stream}.{FlertCompositionSchema.StreamLegacyId}", StringComparison.Ordinal));

        await using var check = Context();
        var stream = await check.RegistryDefs.AsNoTracking().Include(d => d.Fields).SingleAsync(d => d.Code == Codes.Stream);
        Assert.Empty(stream.Fields);
        Assert.Equal(RegistrySourceKind.Local, stream.SourceKind);
        Assert.False(await check.RegistryDefs.AnyAsync(d => d.Code == Codes.Case || d.Code == Codes.Component));
    }

    /// <remarks>
    /// Мутаційний доказ (локально, не в коміті): <c>DeactivationRefusalAsync</c> завжди повертає <c>null</c> → цей тест
    /// червоний (20 кейсів вимкнено, зміна складу записана).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Частковий_знімок_понад_межу_вимкнення_не_пише_нічого_а_в_межах_застосовується()
    {
        var source = new FakeFlert(ManyCases(100, missing: 0, methane: 100m));
        var dataSource = await ArrangeSourceAsync();
        await using var provider = BuildProvider();
        var first = await RunAsync(provider, source, dataSource);
        Assert.Equal(100, first.Counts!.Added["cases"]);
        var before = await ActiveCountsAsync();
        var revisions = await RevisionsAsync();

        // 20 зі 100 зникли (межа max(5, 10 %) = 10) і заразом змінився склад решти — не пишеться НІЧОГО.
        source.Table = ManyCases(100, missing: 20, methane: 99m);
        var refused = await RunAsync(provider, source, dataSource);

        Assert.Equal(FlertCompositionSyncJob.Degraded, refused.Status);
        Assert.Equal("deactivationLimit", refused.Reason);
        Assert.Null(refused.Counts);
        var issue = Assert.Single(refused.Issues, i => i.Kind == "deactivationLimit");
        Assert.Contains("20 з 100", issue.Detail, StringComparison.Ordinal);
        Assert.Contains("дозволено 10", issue.Detail, StringComparison.Ordinal);
        Assert.Equal(before, await ActiveCountsAsync());
        Assert.Equal(revisions, await RevisionsAsync());
        Assert.Equal(100m, await DecimalAsync(Codes.Composition, "C1000_20220101_Methane", FlertCompositionSchema.RowMolPct));

        // 5 зі 100 — у межах: застосовується.
        source.Table = ManyCases(100, missing: 5, methane: 100m);
        var applied = await RunAsync(provider, source, dataSource);
        Assert.Equal(FlertCompositionSyncJob.Succeeded, applied.Status);
        Assert.Equal(5, applied.Counts!.Deactivated["cases"]);
    }

    /// <summary><paramref name="total"/> кейсів одного потоку (C1000…), останні <paramref name="missing"/> — відсутні.</summary>
    private static ExternalTable ManyCases(int total, int missing, decimal methane)
    {
        var full = Snapshot();
        var rows = Enumerable.Range(0, total - missing)
            .Select(i => Row(7, 1000 + i, $"Case {i}", "7D-1", new() { ["Methane"] = methane, ["H2S"] = 100m - methane }))
            .ToList();
        return new ExternalTable(
            full.Columns,
            [.. rows.Select(r => (IReadOnlyList<object?>)[.. full.Columns.Select(c => r.TryGetValue(c, out var v) ? v : null)])],
            Truncated: false);
    }

    // ─── Стенд ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Знімок FLERT: потік 1 (1D-2, суша) — кейси C10 «370 Winter» (Methane, H2S, CO=1 проти Carbon_Monoxide=2) і
    /// C11 «370 Summer» (Methane); потік 2 — C12 «No composition» (Σ = 0).
    /// </summary>
    private static ExternalTable Snapshot(decimal methane = 97m, decimal? h2s = 2m, bool withSecondCase = true)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            Row(1, 10, "370 Winter", "1D-2", new() { ["Methane"] = methane, ["H2S"] = h2s, ["CO"] = 1m, ["Carbon_Monoxide"] = 2m }),
            Row(2, 12, "No composition", "1D-3", new()),
        };

        if (withSecondCase)
        {
            rows.Insert(1, Row(1, 11, "370 Summer", "1D-2", new() { ["Methane"] = 100m }));
        }

        var columns = FlertCompositionSchema.StreamColumns
            .Concat(StreamValidity)
            .Concat(FlertCompositionSchema.CaseColumns)
            .Concat(TypeValidity)
            .Concat(FlertCompositionSchema.PropertyColumns)
            .Concat(FlertCompositionSchema.ComponentColumns.Select(c => c.Column))
            .ToList();

        return new ExternalTable(
            columns,
            [.. rows.Select(r => (IReadOnlyList<object?>)[.. columns.Select(c => r.TryGetValue(c, out var v) ? v : null)])],
            Truncated: false);
    }

    private static Dictionary<string, object?> Row(int streamId, int typeId, string caseName, string number, Dictionary<string, decimal?> components)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Stream_ID"] = streamId,
            ["StreamNumber"] = number,
            ["Stream_Name"] = $"Stream {number}",
            ["isOnshore"] = true,
            ["isSour"] = false,
            ["streamHMB"] = "370",
            ["excludeInListBox"] = false,
            ["StreamGroup"] = 1,
            ["isHMB"] = true,
            ["StreamType_ID"] = typeId,
            ["StreamType"] = caseName,
            ["GasComposition_ID"] = typeId * 10,
            ["GasComposition_ValidFrom"] = Since2022,
            ["GasComposition_ValidTo"] = null,
            ["Temperature"] = 20m,
        };

        foreach (var (column, value) in components)
        {
            row[column] = value;
        }

        return row;
    }

    private async Task<string> ArrangeSourceAsync()
    {
        await using var db = Context();
        var code = $"FLERT{_tag}";
        db.DataSources.Add(new DataSource(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = "FLERT" }),
            ExternalTransport.Sql, "Server=flert.test;Database=FLERT", "flert-reader"));
        await db.SaveChangesAsync();
        return code;
    }

    private async Task<FlertSyncOutcome> RunAsync(ServiceProvider provider, FakeFlert source, string dataSource)
    {
        await using var db = Context();
        return await Job(db, provider, source, Substitute.For<IConfiguration>())
            .RunAsync(new FlertCompositionSyncOptions(true, dataSource), Codes, CancellationToken.None);
    }

    private async Task<long> ExecuteAsync(ServiceProvider provider, FakeFlert source, string dataSource)
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        configuration[FlertCompositionSyncOptions.EnabledKey].Returns("true");
        configuration[FlertCompositionSyncOptions.DataSourceCodeKey].Returns(dataSource);

        await using var db = Context();
        await Job(db, provider, source, configuration).ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        return await LastRunIdAsync();
    }

    private FlertCompositionSyncJob Job(EcrDbContext db, ServiceProvider provider, FakeFlert source, IConfiguration configuration)
        => new(db, [source], new IntegrationActor(db, new JobActorScope()), new TestClock(Now),
            provider.GetRequiredService<IServiceScopeFactory>(), configuration)
        {
            Codes = Codes,
        };

    private async Task<long> LastRunIdAsync()
    {
        await using var db = Context();
        return await db.MaintenanceRuns.AsNoTracking()
            .Where(r => r.JobCode == FlertCompositionSyncJob.Code)
            .Select(r => (long?)r.Id)
            .MaxAsync() ?? 0;
    }

    /// <summary>Ревізія даних кожного довідника дзеркала.</summary>
    private async Task<Dictionary<string, long>> RevisionsAsync()
    {
        await using var db = Context();
        var codes = new[] { Codes.Stream, Codes.Case, Codes.Composition, Codes.Component };
        return await db.RegistryDefs.AsNoTracking()
            .Where(d => codes.Contains(d.Code))
            .ToDictionaryAsync(d => d.Code, d => (long)d.DataRevision);
    }

    /// <summary>Скільки живих увімкнених записів у кожному довіднику дзеркала.</summary>
    private async Task<Dictionary<string, int>> ActiveCountsAsync()
    {
        await using var db = Context();
        var codes = new[] { Codes.Stream, Codes.Case, Codes.Composition };
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            var id = await db.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
            result[code] = await db.RegistryEntries.CountAsync(e => e.RegistryDefId == id && !e.IsDeleted && e.IsActive);
        }

        return result;
    }

    private async Task<Ecr.Domain.Entities.Dictionaries.RegistryEntry> EntryAsync(string registry, string code)
    {
        await using var db = Context();
        var id = await db.RegistryDefs.Where(d => d.Code == registry).Select(d => d.Id).SingleAsync();
        return await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.RegistryDefId == id && e.Code == code);
    }

    private async Task<decimal?> DecimalAsync(string registry, string code, string field)
        => (await ValueAsync(registry, code, field))?.ValueNumeric;

    private async Task<bool?> BoolAsync(string registry, string code, string field)
        => (await ValueAsync(registry, code, field))?.ValueBool;

    private async Task<Ecr.Domain.Entities.Dictionaries.RegistryValue?> ValueAsync(string registry, string code, string field)
    {
        var entry = await EntryAsync(registry, code);
        await using var db = Context();
        var fieldId = await db.RegistryFieldDefs
            .Where(f => f.RegistryDefId == entry.RegistryDefId && f.Code == field)
            .Select(f => f.Id)
            .SingleAsync();
        return await db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryEntryId == entry.Id && v.RegistryFieldDefId == fieldId)
            .OrderByDescending(v => v.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>Контейнер як у проді (той самий, що в <c>RegistrySyncExecuteTests</c>).</summary>
    private ServiceProvider BuildProvider()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        var connectionStrings = Substitute.For<IConfigurationSection>();
        connectionStrings["Ecr"].Returns(sql.ConnectionString);
        configuration.GetSection("ConnectionStrings").Returns(connectionStrings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);
        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());
        return services.BuildServiceProvider();
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>FLERT, якого в тесті немає: знімок або відмова.</summary>
    private sealed class FakeFlert(ExternalTable table) : IExternalTableSource
    {
        public ExternalTable Table { get; set; } = table;

        public Exception? Failure { get; set; }

        public ExternalTransport Transport => ExternalTransport.Sql;

        public Task<ExternalTable> ReadTableAsync(int dataSourceId, string queryKey, int maxRows, CancellationToken ct)
        {
            Assert.Equal(FlertCompositionSchema.QueryKey, queryKey);
            return Failure is null ? Task.FromResult(Table) : Task.FromException<ExternalTable>(Failure);
        }
    }

    /// <summary><c>Ecr.Api.Auth.CurrentUser</c> поза HTTP-запитом: анонімний.</summary>
    private sealed class NoHttpRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => null;

        public string CorrelationId
            => throw new InvalidOperationException("ICurrentUser використано поза запитом: HttpContext немає.");

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
