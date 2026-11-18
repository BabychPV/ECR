using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Application.Calculations.Scripting.Migration;
using Ecr.Infrastructure.Persistence;
using Ecr.MethodologyScriptMigration.Cli;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.MethodologyScriptMigration.Loading;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>
/// Бібліотека, перевидана всередині життя версії-імпортера: скрипт несе склад імпортів (відомі категорії, маркери
/// <c>Name?</c>) одним текстом, а старий рушій бере його з версії бібліотеки кожного періоду. Якщо склад змінився, точний
/// перенос неможливий — читання версії дає помилку <c>importLibraryVersionsDiffer</c> (конвертер і apply відмовляють).
/// </summary>
/// <remarks>
/// Мутації: прибрати перевірку у <c>VersionSourceReader</c> — червоніє
/// <see cref="Нова_версія_бібліотеки_з_іншими_категоріями_у_житті_імпортера_відхиляється_з_діагностикою"/>; зробити
/// <c>Divergences</c> завжди порожнім — те саме; порівнювати завжди «різне» — червоніє контроль
/// <see cref="Нова_версія_бібліотеки_з_тим_самим_складом_не_заважає"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class ImportLibraryVersionsTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly List<int> _methodologyIds = [];

    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static void Constant(EcrDbContext db, int versionId, string code, decimal value, int unitId, string category)
    {
        var constant = new MethodologyConstant(versionId, EcrCode.Create(code), value, unitId);
        constant.SetScope(category, null);
        db.MethodologyConstants.Add(constant);
    }

    /// <summary>
    /// Імпортер (2024-01-01, без наступної версії) і бібліотека з двома версіями: V1 (2024-01-01, категорія A) і V2
    /// (2025-01-01, категорії з <paramref name="secondCategories"/>).
    /// </summary>
    private async Task<int> SeedAsync(params string[] secondCategories)
    {
        await using var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false);
        var unit = (await db.Units.FirstAsync(u => u.Code == "kg").ConfigureAwait(false)).Id;
        var suffix = Suffix();

        var library = new Methodology(EcrCode.Create("MSL" + suffix), Name("library"));
        library.SetKind(MethodologyKind.Library);
        var caller = new Methodology(EcrCode.Create("MSC" + suffix), Name("caller"));
        db.Methodologies.AddRange(library, caller);
        await db.SaveChangesAsync().ConfigureAwait(false);
        _methodologyIds.AddRange([library.Id, caller.Id]);

        var libV1 = new MethodologyVersion(library.Id, "V1", CalculationLevel.Configuration, 1, Now);
        var libV2 = new MethodologyVersion(library.Id, "V2", CalculationLevel.Configuration, 1, Now);
        var callerV1 = new MethodologyVersion(caller.Id, "V1", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.AddRange(libV1, libV2, callerV1);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var version in new[] { libV1, libV2 })
        {
            var dens = new MethodologyFormula(version.Id, EcrCode.Create("Dens"), "CST.K * 2");
            dens.SetEvaluationOrder(1);
            db.MethodologyFormulas.Add(dens);
        }

        Constant(db, libV1.Id, "K", 1.5m, unit, "A");
        var index = 0;
        foreach (var category in secondCategories)
        {
            Constant(db, libV2.Id, "K", 2.5m + index++, unit, category);
        }

        db.MethodologyImports.Add(new MethodologyImport(callerV1.Id, library.Id, caller.Id));
        var emission = new MethodologyFormula(callerV1.Id, EcrCode.Create("Emission"), "@Qty * !Dens");
        emission.SetEvaluationOrder(1);
        db.MethodologyFormulas.Add(emission);
        db.MethodologyOutputs.Add(new MethodologyOutput(callerV1.Id, EcrCode.Create("Emission"), unit, 1));
        await db.SaveChangesAsync().ConfigureAwait(false);

        libV1.Publish(1, "seed", new DateOnly(2024, 1, 1), true, Now);
        libV2.Publish(1, "seed", new DateOnly(2025, 1, 1), true, Now);
        callerV1.Publish(1, "seed", new DateOnly(2024, 1, 1), true, Now);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return callerV1.Id;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Прибирає засіяне: база спільна з іншими тестами (звіт convert рахує всі версії).</summary>
    public async Task DisposeAsync()
    {
        if (_methodologyIds.Count == 0)
        {
            return;
        }

        await using var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false);
        var ids = _methodologyIds;
        var versionIds = await db.MethodologyVersions.Where(v => ids.Contains(v.MethodologyId)).Select(v => v.Id).ToListAsync();
        await db.MethodologyConstants.Where(c => versionIds.Contains(c.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyOutputs.Where(o => versionIds.Contains(o.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyFormulas.Where(f => versionIds.Contains(f.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyImports.Where(i => versionIds.Contains(i.MethodologyVersionId)).ExecuteDeleteAsync();
        await db.MethodologyVersions.Where(v => versionIds.Contains(v.Id)).ExecuteDeleteAsync();
        await db.Methodologies.Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync();
    }

    private async Task<LoadedVersion> LoadAsync(int versionId)
    {
        await using var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: true);
        return await new VersionSourceLoader(db, MigrationCli.CreateFormulaEngine()).LoadAsync(versionId, default);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нова_версія_бібліотеки_з_іншими_категоріями_у_житті_імпортера_відхиляється_з_діагностикою()
    {
        var versionId = await SeedAsync("A", "B");

        var loaded = await LoadAsync(versionId);

        var issue = Assert.Single(loaded.Issues, i => i.Code == "importLibraryVersionsDiffer");
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("2025-01-01", issue.Message, StringComparison.Ordinal);
        Assert.True(loaded.HasErrors);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нова_версія_бібліотеки_з_тим_самим_складом_не_заважає()
    {
        var versionId = await SeedAsync("A");

        var loaded = await LoadAsync(versionId);

        Assert.DoesNotContain(loaded.Issues, i => i.Code == "importLibraryVersionsDiffer");
    }

    [Fact]
    public void Divergences_порівнює_категорії_й_маркери_без_урахування_порядку_і_регістру_маркерів()
    {
        var baseline = new[] { new SourceImport("Lib", ["X"], ["B", "A"], ["Idle", "Spare"]) };

        Assert.Empty(ImportClosureSource.Divergences(baseline, [new SourceImport("Lib", ["Y"], ["A", "B"], ["spare", "IDLE"])]));
        Assert.Equal(["Lib"], ImportClosureSource.Divergences(baseline, [new SourceImport("Lib", ["X"], ["A"], ["Idle", "Spare"])]));
        Assert.Equal(["Lib"], ImportClosureSource.Divergences(baseline, [new SourceImport("Lib", ["X"], ["A", "B"], ["Idle"])]));
        Assert.Equal(["Lib"], ImportClosureSource.Divergences(baseline, []));
    }
}
