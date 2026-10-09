// tests/Ecr.Architecture.Tests/HumanOriginSqlLiteralTests.cs
using Ecr.Application.Documents;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D2-01 (аудит 09.10b): сторожі «правка людини» в сирому SQL над <c>aud.CellChange</c> беруть
/// перелік людських походжень із <see cref="CellChangeOrigins.HumanOriginsSql"/>, а не пишуть
/// <c>N'UserEdit'</c> літералом.
/// </summary>
/// <remarks>
/// ⛔ Три такі літерали (<c>IntegrationCellPatcher.ManualCellsAsync</c>,
/// <c>SourceEventSyncJob.RecheckRemovalAsync</c>, <c>ManualRowKeysAsync</c>) не бачили імпорту
/// книги (<c>Import</c>): інтеграція перезаписувала імпортоване, синк жорстко видаляв рядки.
/// Новий запит із <c>N'UserEdit'</c> повторив би ту саму помилку.
/// <para>
/// ⚠ Текст джерел, а не IL: SQL живе в рядкових літералах, і саме їх треба читати. Коментарі
/// теж скануються — <c>N'UserEdit'</c> у коментарі <c>src/</c> не потрібен.
/// </para>
/// </remarks>
public sealed class HumanOriginSqlLiteralTests
{
    private const string Literal = "N'UserEdit'";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Finding", "D2-01")]
    public void Сирий_SQL_не_пише_UserEdit_літералом()
    {
        var violations = SourceTree.Production()
            .SelectMany(file => file.Text.Split('\n')
                .Select((line, index) => (file.Path, Line: index + 1, Text: line))
                .Where(x => x.Text.Contains(Literal, StringComparison.Ordinal)))
            .Select(x => $"{x.Path}:{x.Line}: {x.Text.Trim()}")
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"Знайдено {Literal} у src/: людські походження — це і UserEdit, і Import (D2-01). "
            + "Пиши Origin IN ({CellChangeOrigins.HumanOriginsSql}):"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Finding", "D2-01")]
    public void Перелік_людських_походжень_містить_сітку_й_імпорт()
    {
        Assert.Equal("N'UserEdit', N'Import'", CellChangeOrigins.HumanOriginsSql);
    }
}
