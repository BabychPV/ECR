// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyConstantCapTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Стеля констант версії у <see cref="MethodologyStore.GetConstantsAsync"/>
/// відмовляє, а не обрізає мовчки (аудит P1, продовження).
/// </summary>
/// <remarks>
/// ⛔ Чому це важливо саме тепер. Прогін читає константи ВСІЄЇ версії одним
/// запитом і вибирає кандидата в пам'яті. <c>Take(стеля)</c> без перевірки
/// відкинув би хвіст, і відкинуті константи для формул «не існували б»: тихий
/// <c>#REF</c> без жодної причини в журналі. Стеля тут мала — 2 — через
/// конструктор сховища; продукт бере <c>10 000</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyConstantCapTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Констант_більше_за_стелю_відмова_а_не_обрізаний_список()
    {
        var versionId = await SeedVersionAsync(constantCount: 3);
        await using var db = sql.CreateContext();

        // ⛔ Мутація, що валить тест: прибрати перевірку `Count > стеля` —
        // сховище поверне дві константи з трьох, і виняток не прозвучить.
        var error = await Assert.ThrowsAsync<DomainException>(
            () => new MethodologyStore(db, constantCap: 2).GetConstantsAsync(versionId, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.constantsOverCap", error.Details!["messageKey"]);
        Assert.Equal("2", error.Details["cap"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рівно_стеля_констант_читається_повністю()
    {
        // Межа: `стеля + 1` у запиті не сміє перетворитися на відмову там, де
        // констант рівно стільки, скільки дозволено.
        var versionId = await SeedVersionAsync(constantCount: 3);
        await using var db = sql.CreateContext();

        var constants = await new MethodologyStore(db, constantCap: 3)
            .GetConstantsAsync(versionId, CancellationToken.None);

        Assert.Equal(3, constants.Count);
    }

    /// <summary>Чернетка версії з <paramref name="constantCount"/> числовими константами.</summary>
    private async Task<int> SeedVersionAsync(int constantCount)
    {
        await using var db = sql.CreateContext();

        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var methodology = new Methodology(
            EcrCode.Create($"CAP_{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"Cap {tag}" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        for (var i = 0; i < constantCount; i++)
        {
            db.MethodologyConstants.Add(version.AddNumericConstant(EcrCode.Create($"k{i}"), i, unit));
        }

        await db.SaveChangesAsync();
        return version.Id;
    }
}
