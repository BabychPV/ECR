// tests/Ecr.Infrastructure.Tests/Jobs/FlaringRegistryProvisionerTests.cs
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Flert;
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
/// ✎ SGF-flaring (DESIGN-sg-flaring §5.7, H5/H7/H10): довідники факелів заводить код разом зі складом — з рядками, потоками за
/// Id FLERT і назвами кейсів SOR/EOR; другий виклик нічого не змінює; закріплення без потоку чи з неоднозначним кейсом — поле
/// порожнє й зауваження (скрипт відхилить рядок, а не дасть 0).
/// </summary>
[Collection("SqlServer")]
public sealed class FlaringRegistryProvisionerTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static readonly int[] StreamIds = [19, 21, 146, 313, 316, 317, 361, 573, 574, 575];

    private FlertRegistryCodes Gas => new($"STREAM_{_tag}", $"STREAM_CASE_{_tag}", $"GAS_COMPOSITION_{_tag}", $"COMPONENT_{_tag}");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "SGF-FLARING-REGISTRIES")]
    public async Task Довідники_факелів_заводяться_з_рядками_а_другий_виклик_нічого_не_змінює()
    {
        await using var provider = BuildProvider();
        var codes = FlertCompositionSyncJob.FlaringCodes(Gas);
        Assert.Equal($"FLARE_LOCATION_{_tag}", codes.Location);

        var first = await EnsureAsync(provider, codes);

        Assert.Equal([codes.Location, codes.Split, codes.Pin], first.Created);
        Assert.Equal(5 + 6 + 15, first.Rows);
        Assert.Contains(first.Warnings, w => w.Contains("LEGACY_ID 572", StringComparison.Ordinal));
        Assert.Contains(first.Warnings, w => w.Contains("417/Train 1", StringComparison.Ordinal) && w.Contains("2 SOR cases", StringComparison.Ordinal));

        await using (var db = Context())
        {
            var values = await ValuesAsync(db, codes.Pin, "P481_TRAIN1");
            Assert.Equal(await EntryIdAsync(db, Gas.Stream, "S574"), values["STREAM"].ValueRefEntryId);
            Assert.Equal("481A SOR", values["CASE_SOR"].ValueString);
            Assert.Equal("481A EOR", values["CASE_EOR"].ValueString);

            var t2 = await ValuesAsync(db, codes.Pin, "P481_TRAIN2");
            Assert.Equal(await EntryIdAsync(db, Gas.Stream, "S573"), t2["STREAM"].ValueRefEntryId);

            var ambiguous = await ValuesAsync(db, codes.Pin, "P417_TRAIN1");
            Assert.False(ambiguous.ContainsKey("CASE_SOR") && ambiguous["CASE_SOR"].ValueString is not null);
            Assert.Equal("417 EOR", ambiguous["CASE_EOR"].ValueString);

            var slot = await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Code == "S1_2024" && db.RegistryDefs.Any(d => d.Id == e.RegistryDefId && d.Code == codes.Split));
            Assert.Equal(new DateOnly(2024, 1, 1), slot.ValidFrom);
            Assert.Equal(new DateOnly(2025, 1, 1), slot.ValidTo);
            var slotValues = await ValuesAsync(db, codes.Split, "S3_2025");
            Assert.Equal(0.2m, slotValues["COEF"].ValueNumeric);
            Assert.Equal(await EntryIdAsync(db, Gas.Stream, "S146"), slotValues["STREAM"].ValueRefEntryId);

            var location = await ValuesAsync(db, codes.Location, "D_ISLAND_LP");
            Assert.Equal("D Island - LP Flare", location["AF_NAME"].ValueString);
            Assert.False(location["IS_ONSHORE"].ValueBool);
            Assert.Equal(302m, location["TYPE_REPORT"].ValueNumeric);
        }

        var second = await EnsureAsync(provider, codes);
        Assert.Empty(second.Created);
        Assert.Equal(0, second.Rows);
    }

    /// <summary>Огляд M4: наявний ПОРОЖНІЙ довідник заповнюється; наявний з даними — не чіпається.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "SGF-FLARING-REVIEW-M4")]
    public async Task Наявний_порожній_заповнюється__наявний_з_даними_не_чіпається()
    {
        await using var provider = BuildProvider();
        var codes = FlertCompositionSyncJob.FlaringCodes(Gas);

        var report = await EnsureAsync(provider, codes, async (services, ct) =>
        {
            var flert = services.GetRequiredService<FlertRegistryProvisioner>();
            await flert.CreateAsync(codes.Location, "loc", "loc", isTemporal: false, RegistrySourceKind.Local,
                [("AF_NAME", "a", "a", CellDataType.String, true), ("IS_ONSHORE", "o", "o", CellDataType.Bool, true),
                 ("TYPE_REPORT", "t", "t", CellDataType.Int, true), ("FLARE_UNIT", "u", "u", CellDataType.String, false)],
                _ => null, 11, ct);
            var manual = await flert.CreateAsync(codes.Split, "split", "split", isTemporal: true, RegistrySourceKind.Local,
                [("SLOT", "s", "s", CellDataType.Int, true), ("COEF", "c", "c", CellDataType.Decimal, true)], _ => null, 11, ct);
            await services.GetRequiredService<RegistryEntryWriter>().WriteAsync(new RegistryEntryWriteBatch(manual.Id,
                [new RegistryEntryWrite("ADMIN", new Dictionary<string, object?> { ["SLOT"] = 1, ["COEF"] = 1m })]), ct);
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
        });

        Assert.Equal([codes.Pin], report.Created);
        Assert.Equal(5 + 15, report.Rows);
        await using var db = Context();
        Assert.Equal(5, await db.RegistryEntries.CountAsync(e => db.RegistryDefs.Any(d => d.Id == e.RegistryDefId && d.Code == codes.Location)));
        Assert.Equal(["ADMIN"], await db.RegistryEntries.Where(e => db.RegistryDefs.Any(d => d.Id == e.RegistryDefId && d.Code == codes.Split))
            .Select(e => e.Code).ToListAsync());
    }

    /// <summary>Огляд M4: рядок, який відхилив writer, — відмова й відкат, а не напівзаповнений довідник із зауваженням.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "SGF-FLARING-REVIEW-M4")]
    public async Task Помилка_запису_рядка__відмова_і_відкат()
    {
        await using var provider = BuildProvider();
        var codes = FlertCompositionSyncJob.FlaringCodes(Gas);

        await Assert.ThrowsAnyAsync<Exception>(() => EnsureAsync(provider, codes, async (services, ct) =>
            _ = await services.GetRequiredService<FlertRegistryProvisioner>().CreateAsync(codes.Pin, "pin", "pin", isTemporal: true,
                RegistrySourceKind.Local, [("COLUMN", "c", "c", CellDataType.Int, true)], _ => null, 11, ct)));

        await using var db = Context();
        Assert.False(await db.RegistryDefs.AnyAsync(d => d.Code == codes.Location || d.Code == codes.Pin));
    }

    private async Task<FlaringRegistryReport> EnsureAsync(
        ServiceProvider provider, FlaringRegistryCodes codes, Func<IServiceProvider, CancellationToken, Task>? prepare = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var author = await services.GetRequiredService<IntegrationActor>().EnterAsync(CancellationToken.None);
        FlaringRegistryReport? report = null;
        await services.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
            async tx =>
            {
                var set = await services.GetRequiredService<FlertRegistryProvisioner>().EnsureAsync(Gas, tx);
                var writer = services.GetRequiredService<RegistryEntryWriter>();
                var uow = services.GetRequiredService<IUnitOfWork>();
                await writer.WriteAsync(new RegistryEntryWriteBatch(set.Stream!.Id, [.. StreamIds.Select(s => new RegistryEntryWrite(
                    $"S{s}", new Dictionary<string, object?> { [FlertCompositionSchema.StreamLegacyId] = s }))]), tx);
                await uow.SaveChangesAsync(tx);

                var ids = await services.GetRequiredService<EcrDbContext>().RegistryEntries
                    .Where(e => e.RegistryDefId == set.Stream.Id).ToDictionaryAsync(e => e.Code, e => e.Id, tx);
                var cases = new (string Stream, string Name)[]
                {
                    ("S574", "481A SOR"), ("S574", "481A EOR"), ("S573", "481B SOR"), ("S573", "481B EOR"),
                    ("S575", "417 SOR"), ("S575", "417 SOR Rev2"), ("S575", "417 EOR"), ("S317", "Units 331-332 - SOR Rev D04"),
                };
                await writer.WriteAsync(new RegistryEntryWriteBatch(set.Case!.Id, [.. cases.Select((c, i) => new RegistryEntryWrite(
                    $"C{i}", new Dictionary<string, object?>
                    {
                        [FlertCompositionSchema.CaseStream] = ids[c.Stream],
                        [FlertCompositionSchema.CaseName] = c.Name,
                    })
                {
                    Validity = new ValidityWindow(new DateOnly(2022, 1, 1), null),
                })]), tx);
                await uow.SaveChangesAsync(tx);

                if (prepare is not null)
                {
                    await prepare(services, tx);
                }

                report = await services.GetRequiredService<FlaringRegistryProvisioner>().EnsureAsync(codes, set.Stream.Id, set.Case.Id, tx);
            },
            CancellationToken.None);
        return report!;
    }

    private static async Task<long> EntryIdAsync(EcrDbContext db, string registry, string code)
        => await db.RegistryEntries.AsNoTracking()
            .Where(e => e.Code == code && db.RegistryDefs.Any(d => d.Id == e.RegistryDefId && d.Code == registry))
            .Select(e => e.Id).SingleAsync();

    private static async Task<Dictionary<string, Domain.Entities.Dictionaries.RegistryValue>> ValuesAsync(EcrDbContext db, string registry, string code)
    {
        var id = await EntryIdAsync(db, registry, code);
        return await (
                from v in db.RegistryValues.AsNoTracking()
                where v.RegistryEntryId == id
                join f in db.RegistryFieldDefs.AsNoTracking() on v.RegistryFieldDefId equals f.Id
                select new { f.Code, v })
            .ToDictionaryAsync(x => x.Code, x => x.v);
    }

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
