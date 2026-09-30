// tests/Ecr.Infrastructure.Tests/Persistence/EnumDefaultSentinelTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Явно задане значення <c>0</c> переліку з DEFAULT у схемі доходить до бази
/// саме як <c>0</c>, а не як DEFAULT схеми (EF <c>[20601]</c>).
/// </summary>
/// <remarks>
/// ⛔ EF вважає CLR-замовчування (<c>0</c>) властивості з DEFAULT «незаданим»
/// і на INSERT його не надсилає — тоді в рядок лягає DEFAULT схеми. Для
/// <c>MethodologyVersion.TraceLevel</c> (DEFAULT <c>ErrorsOnly</c>) і
/// <c>RegistryDef.SourceKind</c> (DEFAULT <c>Local</c>) це мовчазна підміна
/// вибору користувача; до виправлення два перші тести падали з <c>1</c> і
/// <c>2</c> відповідно.
///
/// ⚠ Для <c>Project.CurrentPeriodMode</c>, <c>TableDef.StorageMode</c>,
/// <c>FormulaDef.Dialect</c> DEFAULT схеми сам дорівнює <c>0</c>, тож
/// підміна була невидимою — ці три тести зелені й до виправлення. Вони тут
/// як сторож: зміна DEFAULT будь-якого з них на ненульовий повернула б
/// рівно ту ваду, що в перших двох.
/// </remarks>
[Collection("SqlServer")]
public sealed class EnumDefaultSentinelTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.13")]
    public async Task TraceLevel_Off_при_вставці_лишається_Off_а_не_ErrorsOnly()
    {
        var tag = Tag();
        int versionId;

        await using (var db = sql.CreateContext())
        {
            var methodology = new Methodology(EcrCode.Create($"TRC_{tag}"), Name("trace probe"));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();

            var version = new MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
            version.SetModes(NumericMode.Legacy, CalendarMode.Actual, TraceLevel.Off);

            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();
            versionId = version.Id;
        }

        await using var read = sql.CreateContext();
        var stored = await read.MethodologyVersions.AsNoTracking()
            .Where(v => v.Id == versionId).Select(v => v.TraceLevel).SingleAsync();

        Assert.Equal(TraceLevel.Off, stored);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SourceKind_External_при_вставці_лишається_External_а_не_Local()
    {
        var tag = Tag();
        int registryId;

        await using (var db = sql.CreateContext())
        {
            var registry = new RegistryDef(EcrCode.Create($"SRC_{tag}"), Name("source probe"), isTemporal: false);
            registry.SwitchSource(RegistrySourceKind.External);

            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync();
            registryId = registry.Id;
        }

        await using var read = sql.CreateContext();
        var stored = await read.RegistryDefs.AsNoTracking()
            .Where(r => r.Id == registryId).Select(r => r.SourceKind).SingleAsync();

        Assert.Equal(RegistrySourceKind.External, stored);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task CurrentPeriodMode_StorageMode_Dialect_нуль_доходить_як_нуль()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        // Будівник вставляє проєкт із `CurrentPeriodMode.Auto` і таблицю з
        // `CellStorageMode.Normalized` — обидва задані конструктором явно.
        var doc = await builder.BuildAsync();

        int formulaId;
        await using (var db = sql.CreateContext())
        {
            var formula = new FormulaDef(doc.TableDefId, FormulaScope.Column, "[A] + [B]", ExpressionDialect.Template);
            formula.AssignColumn(doc.ColumnDefIds[0]);
            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync();
            formulaId = formula.Id;
        }

        await using var read = sql.CreateContext();

        Assert.Equal(
            CurrentPeriodMode.Auto,
            await read.Projects.AsNoTracking()
                .Where(p => p.Id == doc.ProjectId).Select(p => p.CurrentPeriodMode).SingleAsync());
        Assert.Equal(
            CellStorageMode.Normalized,
            await read.TableDefs.AsNoTracking()
                .Where(t => t.Id == doc.TableDefId).Select(t => t.StorageMode).SingleAsync());
        Assert.Equal(
            ExpressionDialect.Template,
            await read.FormulaDefs.AsNoTracking()
                .Where(f => f.Id == formulaId).Select(f => f.Dialect).SingleAsync());
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
