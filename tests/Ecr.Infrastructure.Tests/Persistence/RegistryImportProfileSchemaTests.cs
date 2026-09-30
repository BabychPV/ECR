// tests/Ecr.Infrastructure.Tests/Persistence/RegistryImportProfileSchemaTests.cs
using System.Data.Common;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>RK05RegistryImportProfile</c> на РЕАЛЬНОМУ SQL Server (RT-06,
/// FEATURE-REGISTRY-TABLES §3.2, §4.6; <c>D-170</c>, <c>D-196</c>): таблиця
/// <c>cfg.RegistryImportProfile</c>.
/// </summary>
/// <remarks>
/// ⛔ Синтаксис JSON перевіряється вставкою ПОВЗ домен (сирий SQL): конструктор
/// <see cref="RegistryImportProfile"/> зламаного JSON і не пропустить, тож
/// перевірка через сутність довела б лише домен. База тримає інваріант для
/// скриптів і перенесення.
///
/// Мутаційний доказ (RT-06, §9.2): прибрати <c>CK_RegImpProfile_Json</c> з
/// міграції — червоніє <see cref="Профіль_з_невалідним_JSON_відхиляється"/>
/// («No exception was thrown»).
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryImportProfileSchemaTests(SqlServerFixture sql)
{
    private const string Spec = """{"sheet":"streams Offshore","orientation":"Columns","duplicateSources":{"CO":"Carbon_Monoxide"}}""";

    private static readonly DateTime Now = new(2026, 9, 28, 9, 30, 15, 123, DateTimeKind.Utc);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-06")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("""{"sheet": "a",}""")]
    [InlineData("")]
    public async Task Профіль_з_невалідним_JSON_відхиляється(string spec)
    {
        var registryId = await RegistryAsync();
        await using var db = sql.CreateContext();

        var error = await Assert.ThrowsAnyAsync<DbException>(() => InsertRawAsync(db, registryId, "BAD_JSON", spec));
        Assert.Contains("CK_RegImpProfile_Json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-06")]
    public async Task Валідний_JSON_вставляється_повз_домен()
    {
        var registryId = await RegistryAsync();
        await using var db = sql.CreateContext();

        // Та сама вставка, що й вище: CHECK не вужчий за «валідний JSON».
        Assert.Equal(1, await InsertRawAsync(db, registryId, "GOOD_JSON", Spec));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-06")]
    public async Task Профіль_зберігається_як_задано()
    {
        var registryId = await RegistryAsync();
        int profileId;

        await using (var db = sql.CreateContext())
        {
            var profile = new RegistryImportProfile(
                registryId, EcrCode.Create("HYSYS_OFFSHORE"), Name("HYSYS offshore export"), Spec, 7, Now);
            db.RegistryImportProfiles.Add(profile);
            await db.SaveChangesAsync();
            profileId = profile.Id;
        }

        await using var read = sql.CreateContext();
        var stored = await read.RegistryImportProfiles.AsNoTracking().SingleAsync(p => p.Id == profileId);

        Assert.Equal((registryId, "HYSYS_OFFSHORE", Spec, 7, Now, "HYSYS offshore export"),
            (stored.RegistryDefId, stored.Code, stored.SpecJson, stored.UpdatedByUserId, stored.UpdatedAt,
             stored.NameL10n.Values["en"]));
        Assert.Equal(8, stored.RowVersion.Length);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-06")]
    public async Task RowVersion_змінюється_при_оновленні()
    {
        var registryId = await RegistryAsync();
        int profileId;
        byte[] before;

        await using (var db = sql.CreateContext())
        {
            var profile = new RegistryImportProfile(registryId, EcrCode.Create("P_ROWVER"), Name("P"), Spec, 7, Now);
            db.RegistryImportProfiles.Add(profile);
            await db.SaveChangesAsync();
            profileId = profile.Id;
            before = profile.RowVersion;
        }

        await using (var db = sql.CreateContext())
        {
            var profile = await db.RegistryImportProfiles.SingleAsync(p => p.Id == profileId);
            profile.Update(Name("P"), """{"orientation":"Rows"}""", 9, Now.AddMinutes(1));
            await db.SaveChangesAsync();
        }

        await using var read = sql.CreateContext();
        var after = await read.RegistryImportProfiles.AsNoTracking()
                              .Where(p => p.Id == profileId).Select(p => p.RowVersion).SingleAsync();

        Assert.NotEmpty(before);
        Assert.NotEqual(before, after);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-06")]
    public async Task Зміна_зі_старим_RowVersion_відхиляється_як_конфлікт()
    {
        var registryId = await RegistryAsync();
        int profileId;

        await using (var db = sql.CreateContext())
        {
            var profile = new RegistryImportProfile(registryId, EcrCode.Create("P_CONFLICT"), Name("P"), Spec, 7, Now);
            db.RegistryImportProfiles.Add(profile);
            await db.SaveChangesAsync();
            profileId = profile.Id;
        }

        // Дві людини відкрили той самий профіль; перша зберегла.
        await using var first = sql.CreateContext();
        await using var second = sql.CreateContext();
        var mine = await first.RegistryImportProfiles.SingleAsync(p => p.Id == profileId);
        var theirs = await second.RegistryImportProfiles.SingleAsync(p => p.Id == profileId);

        mine.Update(Name("P"), """{"orientation":"Rows"}""", 8, Now.AddMinutes(1));
        await first.SaveChangesAsync();

        // Друга зберігає поверх, маючи старий токен: мовчки перезаписати не можна.
        theirs.Update(Name("P"), """{"orientation":"Columns"}""", 9, Now.AddMinutes(2));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-06")]
    public async Task Код_профілю_унікальний_у_межах_довідника()
    {
        var registryId = await RegistryAsync();
        var otherRegistryId = await RegistryAsync();

        await using (var db = sql.CreateContext())
        {
            db.RegistryImportProfiles.AddRange(
                new RegistryImportProfile(registryId, EcrCode.Create("SAME_CODE"), Name("A"), Spec, 7, Now),
                // Той самий код в іншому довіднику — дозволено.
                new RegistryImportProfile(otherRegistryId, EcrCode.Create("SAME_CODE"), Name("B"), Spec, 7, Now));
            await db.SaveChangesAsync();
        }

        await using var again = sql.CreateContext();
        again.RegistryImportProfiles.Add(
            new RegistryImportProfile(registryId, EcrCode.Create("SAME_CODE"), Name("C"), Spec, 7, Now));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync());
        Assert.Contains("UQ_RegistryImportProfile", error.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-06")]
    public async Task Профіль_не_може_вказувати_на_неіснуючий_довідник()
    {
        await using var db = sql.CreateContext();

        var error = await Assert.ThrowsAnyAsync<DbException>(() => InsertRawAsync(db, -1, "NO_REGISTRY", Spec));
        Assert.Contains("FK_RegImpProfile_Def", error.Message, StringComparison.Ordinal);
    }

    private static Task<int> InsertRawAsync(Ecr.Infrastructure.Persistence.EcrDbContext db, int registryId, string code, string spec)
        => db.Database.ExecuteSqlInterpolatedAsync(
            $$"""
            INSERT INTO cfg.RegistryImportProfile (RegistryDefId, Code, NameL10n, SpecJson, UpdatedAt, UpdatedByUserId)
            VALUES ({{registryId}}, {{code}}, N'{"en":"Raw"}', {{spec}}, SYSUTCDATETIME(), 1)
            """);

    private async Task<int> RegistryAsync()
    {
        await using var db = sql.CreateContext();
        var registry = new RegistryDef(
            EcrCode.Create($"RI_{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            Name("Import profile probe"),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return registry.Id;
    }

    private static LocalizedText Name(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
