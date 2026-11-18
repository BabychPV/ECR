using System.Globalization;
using Ecr.Application.Calculations.Scripting;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Версія бібліотеки на дату: старий рушій (<see cref="MethodologyStore.ResolveImportsAsync"/> — його кличе
/// <c>GenericCalculationModule</c>, а за ним і <c>ScriptProgramLoader</c> скриптового прогону) і скриптова компіляція
/// (<see cref="ScriptSourceCompiler"/> — публікація, аналіз, перегляд) обирають ту саму версію: і серед ВИВЕДЕНИХ З ОБІГУ
/// (<c>Deprecated</c>), і за датою; чернетка не береться. Прив'язки (лише <c>Published</c>) — окреме правило й сюди не входять.
/// </summary>
/// <remarks>
/// Мутації: у <c>MethodologyStore.ResolveImportsAsync</c> лишити лише <c>Published</c> — червоніє старий шлях;
/// у <c>Methodology.VersionOn</c> лишити лише <c>Published</c> — червоніє скриптовий.
/// </remarks>
[Collection("SqlServer")]
public sealed class LibraryVersionResolutionParityTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("2023-12-31", null)]
    [InlineData("2024-01-01", "deprecated")]
    [InlineData("2024-06-30", "deprecated")]
    [InlineData("2025-01-01", "published")]
    [InlineData("2099-01-01", "published")]
    public async Task Обидва_шляхи_обирають_ту_саму_версію_бібліотеки_з_Deprecated_серед_кандидатів(string onDate, string? expected)
    {
        var seed = await SeedAsync();
        var date = DateOnly.Parse(onDate, CultureInfo.InvariantCulture);
        await using var db = sql.CreateContext();

        var old = Assert.Single(await new MethodologyStore(db).ResolveImportsAsync(seed.CallerVersionId, date, CancellationToken.None));
        var script = Assert.Single(await new ScriptSourceCompiler(new MethodologyDraftStore(db), new MethodologyStore(db))
            .ResolveTextImportsAsync($"import {seed.LibraryCode};", NumericMode.Strict, isLibrary: false, date, CancellationToken.None));

        int? expectedId = expected switch
        {
            "deprecated" => seed.DeprecatedId,
            "published" => seed.PublishedId,
            _ => null,
        };
        Assert.Equal(expectedId, old.MethodologyVersionId);
        Assert.Equal(expectedId, script.MethodologyVersionId);
    }

    private sealed record Seeded(string LibraryCode, int DeprecatedId, int PublishedId, int CallerVersionId);

    /// <summary>
    /// Бібліотека: 1.0 — виведена з обігу (чинна від 2024-01-01), 2.0 — опублікована (від 2025-01-01), 3.0 — чернетка.
    /// Викликач імпортує її.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var db = sql.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var library = new Methodology(EcrCode.Create($"LIBD_{tag}"), Name($"Library {tag}"));
        library.SetKind(MethodologyKind.Library);
        var caller = new Methodology(EcrCode.Create($"LIBI_{tag}"), Name($"Importer {tag}"));
        db.Methodologies.AddRange(library, caller);
        await db.SaveChangesAsync();

        var first = new MethodologyVersion(library.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        var second = new MethodologyVersion(library.Id, "2.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        var draft = new MethodologyVersion(library.Id, "3.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        var callerVersion = new MethodologyVersion(caller.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.AddRange(first, second, draft, callerVersion);
        await db.SaveChangesAsync();
        db.MethodologyImports.Add(new MethodologyImport(callerVersion.Id, library.Id, caller.Id));
        await db.SaveChangesAsync();

        var aggregate = await db.Methodologies.Include(m => m.Versions).FirstAsync(m => m.Id == library.Id);
        aggregate.PublishVersion(
            aggregate.Versions.Single(v => v.Id == first.Id), 2, "Перша редакція", new DateOnly(2024, 1, 1), true, Now);
        aggregate.PublishVersion(
            aggregate.Versions.Single(v => v.Id == second.Id), 2, "Друга редакція", new DateOnly(2025, 1, 1), true, Now);
        aggregate.Versions.Single(v => v.Id == first.Id).Deprecate();
        await db.SaveChangesAsync();

        return new Seeded(library.Code, first.Id, second.Id, callerVersion.Id);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
