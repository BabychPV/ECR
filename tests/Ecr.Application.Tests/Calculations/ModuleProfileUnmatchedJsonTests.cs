// tests/Ecr.Application.Tests/Calculations/ModuleProfileUnmatchedJsonTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// D14: рядки без правила потрапляють у <c>ModulesProfileJson</c> (кількість і до 10 ключів на документ) і читаються
/// назад по документу; старий формат (масив модулів) і зіпсований JSON не ламають читання.
/// </summary>
/// <remarks>
/// Мутація: повернути <c>ToJson</c> до масиву модулів — червоніють усі тести «читається назад».
/// </remarks>
public sealed class ModuleProfileUnmatchedJsonTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Профіль_зберігає_кількість_і_не_більше_десяти_ключів_на_документ()
    {
        var profile = new ModuleProfile();
        profile.Record("M1", TimeSpan.FromMilliseconds(5), 3);
        profile.RecordUnmatched(Enumerable.Range(1, 12).Select(i => new UnmatchedRow(500, i, $"R{i:000}", 7)));

        var read = ModuleProfile.ReadUnmatched(profile.ToJson(), 7);

        Assert.NotNull(read);
        Assert.Equal(12, read.Count);
        Assert.Equal(10, read.Rows.Count);
        Assert.Equal(new UnmatchedRow(500, 1, "R001", 7), read.Rows[0]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Профіль_проєкту_віддає_документу_лише_його_рядки_а_модулі_лишаються_в_modules()
    {
        var profile = new ModuleProfile();
        profile.Record("M1", TimeSpan.FromMilliseconds(5), 3);
        profile.RecordUnmatched([new UnmatchedRow(500, 2, "A", 7), new UnmatchedRow(600, 1, "B", 8)]);

        var json = profile.ToJson();

        Assert.Equal(1, ModuleProfile.ReadUnmatched(json, 7)!.Count);
        Assert.Equal("B", ModuleProfile.ReadUnmatched(json, 8)!.Rows.Single().RowKey);
        Assert.Null(ModuleProfile.ReadUnmatched(json, 9));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("M1", doc.RootElement.GetProperty("modules")[0].GetProperty("module").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Злиття_профілів_переносить_рядки_без_правила()
    {
        var first = new ModuleProfile();
        first.RecordUnmatched([new UnmatchedRow(500, 2, "A", 7)]);
        var second = new ModuleProfile();
        second.RecordUnmatched([new UnmatchedRow(600, 1, "B", 8)]);

        first.Merge(second);

        Assert.Equal(1, ModuleProfile.ReadUnmatched(first.ToJson(), 8)!.Count);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[{\"module\":\"M1\",\"ms\":\"1.0\",\"rows\":1,\"calls\":1}]")]
    [InlineData("{\"modules\":[]}")]
    [InlineData("not json")]
    [InlineData("{\"unmatched\":[{\"documentId\":7,\"count\":\"x\"}]}")]
    public void Без_діагностики_старий_формат_і_зіпсований_профіль_дають_null(string? json)
        => Assert.Null(ModuleProfile.ReadUnmatched(json, 7));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Профіль_без_рядків_без_правила_не_має_ключа_unmatched()
        => Assert.DoesNotContain("unmatched", new ModuleProfile().ToJson(), StringComparison.Ordinal);
}