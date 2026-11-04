using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): назва аркуша — «&lt;Назва&gt; N»; перейменування групи міняє назву в усіх.
/// </summary>
public sealed class SheetCopyNamingTests
{
    private static LocalizedText Text(params (string Lang, string Value)[] items)
        => new(items.ToDictionary(i => i.Lang, i => i.Value));

    [Theory]
    [InlineData("Stacks", 1, false, "Stacks")]
    [InlineData("Stacks", 1, true, "Stacks 1")]
    [InlineData("Stacks", 2, true, "Stacks 2")]
    [InlineData("Штабелі", 12, true, "Штабелі 12")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Decision", "RS-1103")]
    public void Назва_з_номером_лише_коли_в_групі_є_копії(string name, short ordinal, bool hasCopies, string expected)
        => Assert.Equal(expected, SheetCopyNaming.Display(name, ordinal, hasCopies));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Decision", "RS-1103")]
    public void Перейменування_групи_міняє_назву_кожної_мови_у_всіх_аркушів_групи()
    {
        var template = Text(("en", "Stacks"), ("ru", "Штабели"), ("kz", "Үйінділер"));
        var renamed = SheetCopyNaming.Effective(template, Text(("en", "Piles")));
        var group = SheetCopyNaming.Effective(template, null);

        // Мова без перекладу в назві документа береться з шаблону; задана — з документа.
        Assert.Equal("Piles", renamed.Get("en"));
        Assert.Equal("Штабели", renamed.Get("ru"));
        Assert.Same(template, group);

        var first = SheetCopyNaming.Display(renamed, 1, groupHasCopies: true);
        var third = SheetCopyNaming.Display(renamed, 3, groupHasCopies: true);
        Assert.Equal("Piles 1", first.Get("en"));
        Assert.Equal("Штабели 1", first.Get("ru"));
        Assert.Equal("Piles 3", third.Get("en"));
        Assert.Equal("Үйінділер 3", third.Get("kz"));

        // Без копій — назва як є.
        Assert.Equal("Piles", SheetCopyNaming.Display(renamed, 1, groupHasCopies: false).Get("en"));
    }
}
