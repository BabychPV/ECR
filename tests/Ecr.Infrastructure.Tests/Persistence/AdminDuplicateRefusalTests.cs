using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B158 (лінія d): програш гонки на індексах довідників, профілів імпорту, версій звітів, групових призначень і ролей —
/// та сама відмова, що й послідовна перевірка обробника (або загальний 409, де її будує обробник), а не сирий
/// <c>DbUpdateException</c> (500). Переможець зберігає першим, переможений — той самий ключ з іншого контексту, повз
/// перевірку (саме так його бачить база, коли обидва пройшли «чи вже є?»).
/// </summary>
/// <remarks>
/// Мутації: вимкнути арм у <c>DuplicateRefusalsAdmin</c> — відповідний тест червоний (сирий виняток бази).
/// </remarks>
[Collection("SqlServer")]
public sealed class AdminDuplicateRefusalTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d1_Дубль_коду_довідника_дає_загальний_409_а_не_сирий_виняток_бази()
    {
        // `{id}` переможця база не каже: повну відмову будує CreateRegistryHandler (HTTP-тест d1).
        var code = NewCode("RD");
        var thrown = await LoseRaceAsync<ConcurrencyConflictException>(
            db => db.RegistryDefs.Add(new RegistryDef(EcrCode.Create(code), Name(code), isTemporal: false)));

        AssertConcurrent(thrown);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d2_Дубль_рівня_ієрархії_дає_загальний_409_а_не_сирий_виняток_бази()
    {
        var registryId = await NewRegistryAsync();
        var thrown = await LoseRaceAsync<ConcurrencyConflictException>(
            db => db.RegistryLevelDefs.Add(new RegistryLevelDef(registryId, 1, EcrCode.Create("SITE"), Name("Site"), isOptional: false)));

        AssertConcurrent(thrown);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d3_Дубль_значення_запису_дає_загальний_409_а_не_сирий_виняток_бази()
    {
        var registryId = await NewRegistryAsync();
        int fieldId;
        long entryId;
        await using (var db = Context())
        {
            var field = new RegistryFieldDef(registryId, EcrCode.Create("NOTE"), Name("Note"), Domain.Enums.CellDataType.String, 1);
            db.RegistryFieldDefs.Add(field);
            var entry = new RegistryEntry(registryId, EcrCode.Create("E1"), Name("E1"), await AnyUserIdAsync(db), Now);
            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            (fieldId, entryId) = (field.Id, entry.Id);
        }

        var thrown = await LoseRaceAsync<ConcurrencyConflictException>(db => db.RegistryValues.Add(new RegistryValue(entryId, fieldId)));

        AssertConcurrent(thrown);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d4_Дубль_профілю_імпорту_дає_profileCodeTaken_а_не_сирий_виняток_бази()
    {
        var registryId = await NewRegistryAsync();
        int userId;
        await using (var db = Context())
        {
            userId = await AnyUserIdAsync(db);
        }

        var code = NewCode("PF");
        var thrown = await LoseRaceAsync<ConcurrencyConflictException>(db => db.RegistryImportProfiles.Add(
            new RegistryImportProfile(registryId, EcrCode.Create(code), Name(code), "{}", userId, Now)));

        Assert.Equal("ECR-REG-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-REG-0409.profileCodeTaken", thrown.Details!["messageKey"]);
        Assert.Equal(code, thrown.Details["code"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d5_Друга_перша_чернетка_опису_дає_загальний_409_а_не_сирий_виняток_бази()
    {
        // `{rowVersion}` чернетки переможця база не каже: повну відмову будує SaveRegistryDefinitionDraftHandler (HTTP-тест d5).
        var registryId = await NewRegistryAsync();
        int userId;
        await using (var db = Context())
        {
            userId = await AnyUserIdAsync(db);
        }

        var thrown = await LoseRaceAsync<ConcurrencyConflictException>(db => db.RegistryDefinitionDrafts.Add(
            new RegistryDefinitionDraft(registryId, 1, "{}", "B158d", userId, Now)));

        AssertConcurrent(thrown);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d6_Дубль_номера_версії_звіту_дає_version_а_не_сирий_виняток_бази()
    {
        int reportDefId;
        await using (var db = Context())
        {
            var report = new ReportDef(EcrCode.Create(NewCode("RV")), Name("B158d"), isRegulatory: false);
            db.ReportDefs.Add(report);
            await db.SaveChangesAsync();
            reportDefId = report.Id;
        }

        var thrown = await LoseRaceAsync<BusinessRuleException>(
            db => db.ReportVersions.Add(new ReportVersion(reportDefId, "2.0", "[]", "{}", Now)));

        Assert.Equal("ECR-RPT-4091", thrown.ErrorCode);
        Assert.Equal("err.ECR-RPT-4091.version", thrown.Details!["messageKey"]);
        Assert.Equal("2.0", thrown.Details["version"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d7_Дубль_ролі_групи_дає_загальний_409_а_не_roleCodeTaken_чи_сирий_виняток_бази()
    {
        // Код ролі рядок призначення не несе: повну відмову будує AssignGroupRoleHandler (HTTP-тест d7). ⚠ `UQ_Role` — префікс
        // `UQ_RoleAssignment_Sid`: арм d9 (лапки в імені) не має перехоплювати цей індекс.
        var roleId = await NewRoleAsync(NewCode("RG"));
        var sid = $"S-1-5-21-{Random.Shared.Next(100_000_000, 999_999_999)}-1-2-{Random.Shared.Next(10_000, 99_999)}";
        try
        {
            var thrown = await LoseRaceAsync<ConcurrencyConflictException>(db => db.RoleAssignments.Add(new RoleAssignment(roleId, null, sid)));

            AssertConcurrent(thrown);
        }
        finally
        {
            await using var db = Context();
            await db.RoleAssignments.Where(a => a.PrincipalSid == sid).ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d9_Перейменування_ролі_в_зайнятий_код_дає_roleCodeTaken_а_не_сирий_виняток_бази()
    {
        var taken = NewCode("RT");
        await NewRoleAsync(taken);
        var other = await NewRoleAsync(NewCode("RO"));

        await using var loser = Context();
        var role = await loser.Roles.SingleAsync(r => r.Id == other);
        role.Rename(EcrCode.Create(taken), null);

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(() => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-SEC-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-SEC-0409.roleCodeTaken", thrown.Details!["messageKey"]);
        Assert.Equal(taken, thrown.Details["code"]);
    }

    // ───────────────────────────── стенд ─────────────────────────────

    private static void AssertConcurrent(ConcurrencyConflictException thrown)
    {
        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);
    }

    /// <summary>Переможець зберігає через одиницю роботи першим; переможений — те саме з іншого контексту.</summary>
    private async Task<TException> LoseRaceAsync<TException>(Action<EcrDbContext> add)
        where TException : Exception
    {
        await using (var winner = Context())
        {
            add(winner);
            await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
        }

        await using var loser = Context();
        add(loser);

        return await Assert.ThrowsAsync<TException>(() => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));
    }

    private async Task<int> NewRegistryAsync()
    {
        await using var db = Context();
        var registry = new RegistryDef(EcrCode.Create(NewCode("RG")), Name("B158d"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return registry.Id;
    }

    private async Task<int> NewRoleAsync(string code)
    {
        await using var db = Context();
        var role = new Role(EcrCode.Create(code), Name("B158d"));
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    private static Task<int> AnyUserIdAsync(EcrDbContext db) => db.Users.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

    private static string NewCode(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
