using Ecr.Application.Templates;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ✎ R-3 (аудит 2026-11, D-478): родина типу зберігання колонки має ДВІ реалізації — у
/// <c>rpt.usp_GenerateStableViews</c> (<c>05-rpt-views.sql</c>, конфлікт → <c>nvarchar</c>) і в
/// <see cref="PublishChecks.StorageFamily"/> (заборона публікації). Розійдуться — публікація пропускатиме зміну, що
/// ламає стабільну вʼюху, або блокуватиме безпечну.
/// </summary>
/// <remarks>Мутація: змінити родину будь-якого типу в одному місці — тест червоніє.</remarks>
public sealed class StableViewTypeFamilyTests
{
    private const string RptFile = "src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Родина_типу_публікації_збігається_з_родиною_стабільної_вʼюхи()
    {
        var sql = File.ReadAllText(Path.Combine(SourceTree.Root, RptFile.Replace('/', Path.DirectorySeparatorChar)));

        // Окремі родини — усі, що дають не 1; решта (ELSE) — 1.
        var own = Enum.GetValues<CellDataType>()
            .Where(t => PublishChecks.StorageFamily(t) != 1)
            .OrderBy(t => (byte)t)
            .Select(t => $"WHEN {(byte)t} THEN {PublishChecks.StorageFamily(t)}");
        var expected = $"CASE cd.DataType {string.Join(' ', own)} ELSE 1 END";

        Assert.Contains(expected, sql, StringComparison.Ordinal);
        Assert.Equal(
            [CellDataType.Int, CellDataType.Decimal, CellDataType.Formula, CellDataType.Calculated],
            Enum.GetValues<CellDataType>().Where(t => PublishChecks.StorageFamily(t) == 1).OrderBy(t => (byte)t));
    }
}
