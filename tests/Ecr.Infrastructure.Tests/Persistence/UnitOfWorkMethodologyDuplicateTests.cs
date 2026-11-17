// tests/Ecr.Infrastructure.Tests/Persistence/UnitOfWorkMethodologyDuplicateTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B158 (a): програш гонки двох однакових запитів конфігуратора методологій на унікальному індексі —
/// та сама відмова, що й послідовний шлях (409), а не сирий <c>DbUpdateException</c> (500 <c>ECR-SYS-0500</c>).
/// </summary>
/// <remarks>
/// Детермінований зонд у стилі B150: переможець зберігає, переможений зберігає той самий ключ з іншого контексту.
/// Мутації: перейменувати індекс в арму <c>MethodologyDuplicateKeys</c> — відповідний тест червоний (сирий виняток бази).
/// </remarks>
[Collection("SqlServer")]
public sealed class UnitOfWorkMethodologyDuplicateTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 1, 15, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task Дубль_коду_методології_дає_409_codeTaken_з_ідентифікатором_переможця()
    {
        var code = Code("B158M");
        try
        {
            int winnerId;
            await using (var winner = Context())
            {
                var methodology = NewMethodology(code);
                winner.Methodologies.Add(methodology);
                await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
                winnerId = methodology.Id;
            }

            await using var loser = Context();
            loser.Methodologies.Add(NewMethodology(code));

            var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
                () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

            Assert.Equal("ECR-CALC-0409", thrown.ErrorCode);
            Assert.Equal("err.ECR-CALC-0409.codeTaken", thrown.Details!["messageKey"]);
            Assert.Equal(code, thrown.Details["code"]);
            Assert.Equal(winnerId.ToString(System.Globalization.CultureInfo.InvariantCulture), thrown.Details["existingId"]);
        }
        finally
        {
            await CleanupAsync(code);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task Дубль_номера_версії_через_сховище_чернеток_дає_409_versionNumberTaken()
    {
        var code = Code("B158V");
        try
        {
            int methodologyId;
            await using (var setup = Context())
            {
                var methodology = NewMethodology(code);
                setup.Methodologies.Add(methodology);
                await setup.SaveChangesAsync();
                methodologyId = methodology.Id;
            }

            // Переможений читає агрегат ДО того, як переможець закомітив версію, — знімок без неї.
            await using var loser = Context();
            var loserStore = new MethodologyDraftStore(loser);
            var stale = await loserStore.FindAsync(methodologyId, CancellationToken.None);

            await using (var winner = Context())
            {
                var store = new MethodologyDraftStore(winner);
                var fresh = await store.FindAsync(methodologyId, CancellationToken.None);
                var draft = new MethodologyVersion(methodologyId, "1.0", CalculationLevel.Configuration, 1, Now);
                fresh!.AddVersion(draft);
                await store.SaveDraftAsync(draft, copyFromVersionId: null, CancellationToken.None);
            }

            var loserDraft = new MethodologyVersion(methodologyId, "1.0", CalculationLevel.Configuration, 1, Now);
            stale!.AddVersion(loserDraft);

            var thrown = await Assert.ThrowsAsync<DomainException>(
                () => loserStore.SaveDraftAsync(loserDraft, copyFromVersionId: null, CancellationToken.None));

            Assert.Equal("ECR-CALC-0409", thrown.ErrorCode);
            Assert.Equal("err.ECR-CALC-0409.versionNumberTaken", thrown.Details!["messageKey"]);
            Assert.Equal("1.0", thrown.Details["version"]);
            Assert.Equal(code, thrown.Details["code"]);
        }
        finally
        {
            await CleanupAsync(code);
        }
    }

    [Theory]
    [InlineData("formula")]
    [InlineData("constant")]
    [InlineData("rule")]
    [InlineData("requiredInput")]
    [InlineData("output")]
    [InlineData("testCase")]
    [InlineData("category")]
    [InlineData("categoryRule")]
    [InlineData("substance")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task Дубль_складу_версії_при_upsert_дає_409_concurrentChange(string part)
    {
        var code = Code("B158U");
        try
        {
            int versionId;
            await using (var setup = Context())
            {
                var methodology = NewMethodology(code);
                setup.Methodologies.Add(methodology);
                await setup.SaveChangesAsync();
                var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
                setup.MethodologyVersions.Add(version);
                await setup.SaveChangesAsync();
                versionId = version.Id;
            }

            var refs = await RefsAsync(part, code);

            await using (var winner = Context())
            {
                await AddPartAsync(winner, versionId, part, refs);
                await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
            }

            await using var loser = Context();
            await AddPartAsync(loser, versionId, part, refs);

            var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

            Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
            Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);
        }
        finally
        {
            await CleanupAsync(code);
        }
    }

    /// <summary>
    /// Справжня паралель (a1): два однакові <c>POST /methodologies</c> через обробник, кожен у своєму контексті.
    /// Один створює, другий — та сама відмова, що й послідовний шлях; жодного сирого винятку бази.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task Два_одночасні_створення_методології_дають_одну_і_відмову_codeTaken()
    {
        var code = Code("B158P");
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 1 }.Permission(CreateMethodologyHandler.Permission).Build());
        var name = new Dictionary<string, string> { ["en"] = "B158 parallel" };

        try
        {
            using var barrier = new Barrier(2);
            async Task<Exception?> RunAsync()
            {
                await using var db = Context();
                var handler = new CreateMethodologyHandler(
                    new MethodologyDraftStore(db), new UnitOfWork(db), access, new TestUser());
                await Task.Yield();
                barrier.SignalAndWait();
                try
                {
                    await handler.HandleAsync(code, name, MethodologyKind.DataDriven, group: null, CancellationToken.None);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            var results = await Task.WhenAll(Task.Run(RunAsync), Task.Run(RunAsync));

            Assert.Single(results, r => r is null);
            var refusal = Assert.IsType<BusinessRuleException>(Assert.Single(results, r => r is not null));
            Assert.Equal("ECR-CALC-0409", refusal.ErrorCode);
            Assert.Equal("err.ECR-CALC-0409.codeTaken", refusal.Details!["messageKey"]);
            Assert.Equal(code, refusal.Details["code"]);
        }
        finally
        {
            await CleanupAsync(code);
        }
    }

    private static string Code(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    private static Methodology NewMethodology(string code)
        => new(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = "B158" }));

    /// <summary>Зовнішні ключі складу, яких потребує частина (одиниця, колонка шаблону, речовина).</summary>
    private async Task<(int UnitId, int ColumnDefId, long SubstanceEntryId)> RefsAsync(string part, string code)
    {
        await using var db = Context();
        var unitId = part == "output"
            ? await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync()
            : 0;
        var substanceId = 0L;
        if (part == "substance")
        {
            // Власний довідник речовин тесту (у тестовій базі записів довідників немає); прибирає CleanupAsync.
            var def = new RegistryDef(EcrCode.Create(SubstanceRegistryCode(code)), Name(), isTemporal: false);
            db.RegistryDefs.Add(def);
            await db.SaveChangesAsync();
            var entry = new RegistryEntry(def.Id, EcrCode.Create("S1"), Name(), 1, Now);
            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            substanceId = entry.Id;
        }

        var columnDefId = part == "requiredInput"
            ? (await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(209911)).ColumnDefIds[0]
            : 0;
        return (unitId, columnDefId, substanceId);
    }

    /// <summary>Те саме, що робить гілка «нового» в upsert-обробнику: знайти версію, додати частину складу.</summary>
    private static async Task AddPartAsync(
        EcrDbContext db, int versionId, string part, (int UnitId, int ColumnDefId, long SubstanceEntryId) refs)
    {
        var version = await db.MethodologyVersions.SingleAsync(v => v.Id == versionId);
        switch (part)
        {
            case "formula":
                db.Add(version.AddFormula(EcrCode.Create("F1"), "1", FormulaResultType.Number, null));
                break;
            case "constant":
                db.Add(version.AddTextConstant(EcrCode.Create("C1"), "t", ConstantKind.Text));
                break;
            case "rule":
                db.Add(version.AddRule(EcrCode.Create("R1"), "{}", 100));
                break;
            case "requiredInput":
                db.Add(version.AddRequiredInput(refs.ColumnDefId, RequiredInputSeverity.Block, null));
                break;
            case "output":
                db.Add(version.AddOutput(EcrCode.Create("O1"), refs.UnitId, 0));
                break;
            case "testCase":
                db.Add(version.AddTestCase("T1", "{}", "{}", 0m));
                break;
            case "category":
                db.Add(version.AddCategory("CAT1", 1));
                break;
            case "categoryRule":
                db.Add(version.SetCategoryRule(null, "'CAT1'", Now));
                break;
            case "substance":
                db.Add(version.AddSubstance(refs.SubstanceEntryId, 0));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(part), part, null);
        }
    }

    /// <summary>Прибирає методологію тесту з усім складом (зовнішні ключі — Restrict).</summary>
    private async Task CleanupAsync(string code)
    {
        await using var db = Context();
        var ids = await db.MethodologyVersions
            .Where(v => db.Methodologies.Any(m => m.Id == v.MethodologyId && m.Code == code))
            .Select(v => v.Id)
            .ToListAsync();

        await db.MethodologyFormulas.Where(x => ids.Contains(x.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyConstants.Where(x => ids.Contains(x.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyRules.Where(x => ids.Contains(x.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyRequiredInputs.Where(x => ids.Contains(x.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyOutputs.Where(x => ids.Contains(x.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyTestCases.Where(x => ids.Contains(x.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyCategories.Where(x => ids.Contains(x.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyCategoryRules.Where(x => ids.Contains(x.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologySubstances.Where(x => ids.Contains(x.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyVersions.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
        await db.Methodologies.Where(m => m.Code == code).ExecuteDeleteAsync();

        var registryCode = SubstanceRegistryCode(code);
        await db.RegistryEntries
            .Where(e => db.RegistryDefs.Any(d => d.Id == e.RegistryDefId && d.Code == registryCode))
            .ExecuteDeleteAsync();
        await db.RegistryDefs.Where(d => d.Code == registryCode).ExecuteDeleteAsync();
    }

    private static string SubstanceRegistryCode(string methodologyCode) => $"S{methodologyCode}";

    private static LocalizedText Name() => new(new Dictionary<string, string> { ["en"] = "B158" });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private sealed class TestUser : ICurrentUser
    {
        public int? UserId => 1;

        public string? UserName => "b158";

        public string CorrelationId => "b158";

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
