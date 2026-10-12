// tests/Ecr.Api.Tests/SubstanceCodeFromFieldStoreTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// LP-07 форма B: код речовини для суфікса `{ключ}_{код}` береться з поля SUBSTANCE_CODE запису довідника
/// (числовий код не може бути RegistryEntry.Code: EcrCode вимагає літери першою); без поля - код запису.
/// </summary>
/// <remarks>Мутація: повернути `g.First().Code` без `fromField` - перший тест червоний (E301 замість 301).</remarks>
[Collection("SqlServer")]
public sealed class SubstanceCodeFromFieldStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Код_речовини_береться_з_поля_SUBSTANCE_CODE_а_без_поля_з_коду_запису()
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;
        await using var db = new EcrDbContext(options);
        var now = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        static LocalizedText Text(string v) => new(new Dictionary<string, string> { ["en"] = v });

        var withField = new RegistryDef(EcrCode.Create($"SCF{Guid.NewGuid():N}"[..20]), Text("with field"), isTemporal: false);
        var withoutField = new RegistryDef(EcrCode.Create($"SCN{Guid.NewGuid():N}"[..20]), Text("without field"), isTemporal: false);
        db.RegistryDefs.AddRange(withField, withoutField);
        await db.SaveChangesAsync();

        var field = new RegistryFieldDef(withField.Id, EcrCode.Create("SUBSTANCE_CODE"), Text("code"), CellDataType.String, 1);
        db.RegistryFieldDefs.Add(field);
        await db.SaveChangesAsync();

        var e301 = new RegistryEntry(withField.Id, EcrCode.Create("E301"), Text("301"));
        var e1325 = new RegistryEntry(withField.Id, EcrCode.Create("E1325"), Text("1325"));
        var plain = new RegistryEntry(withoutField.Id, EcrCode.Create("CO2"), Text("co2"));
        db.RegistryEntries.AddRange(e301, e1325, plain);
        await db.SaveChangesAsync();

        var v301 = new RegistryValue(e301.Id, field.Id);
        v301.Set(CellDataType.String, "301", unitId: null);
        db.RegistryValues.Add(v301);

        var methodology = new Methodology(EcrCode.Create($"SCM{Guid.NewGuid():N}"[..20]), Text("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();
        db.MethodologySubstances.Add(new MethodologySubstance(version.Id, e301.Id, 0));
        db.MethodologySubstances.Add(new MethodologySubstance(version.Id, e1325.Id, 1));   // поле є, значення немає -> код запису
        db.MethodologySubstances.Add(new MethodologySubstance(version.Id, plain.Id, 2));   // довідник без поля -> код запису
        await db.SaveChangesAsync();

        var codes = await new MethodologyStore(db, constantCap: 1000).GetSubstanceCodesAsync(version.Id, CancellationToken.None);

        Assert.Equal("301", codes[e301.Id]);
        Assert.Equal("E1325", codes[e1325.Id]);
        Assert.Equal("CO2", codes[plain.Id]);
    }
}