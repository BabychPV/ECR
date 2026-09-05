using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Правила доступу до періоду — заміна кнопки <c>Protect</c> (ФВ-2.15).
/// Фікстура задає аркуш `Waste_08` доступним лише в періодах 1–3.
/// </summary>
public sealed class PeriodAccessRuleTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(12, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.15")]
    [Trait("Requirement", "ФВ-2.18")]
    public void Правило_діє_лише_для_періодів_у_заданому_діапазоні(byte sequence, bool applies)
    {
        var rule = Rule(from: 1, to: 3);

        Assert.Equal(applies, rule.AppliesTo(sequence));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.15")]
    [Trait("Requirement", "ФВ-2.16")]
    public void Правило_без_меж_діє_для_всіх_періодів()
    {
        var rule = Rule(from: null, to: null);

        // Порожня межа означає «без обмеження», а не «жодного періоду».
        // Протилежне прочитання зробило б правило без меж кнопкою «сховати все».
        foreach (byte sequence in new byte[] { 1, 6, 12 })
        {
            Assert.True(rule.AppliesTo(sequence));
        }
    }

    /// <summary>Правило з межами; поля закриті, тому виставляються рефлексією.</summary>
    private static PeriodAccessRuleDef Rule(byte? from, byte? to)
    {
        var rule = new PeriodAccessRuleDef(templateVersionId: 1, OutOfWindowBehavior.ReadOnly);
        Set(rule, nameof(PeriodAccessRuleDef.FromSequence), from);
        Set(rule, nameof(PeriodAccessRuleDef.ToSequence), to);
        return rule;
    }

    private static void Set(object target, string name, object? value)
        => target.GetType().GetProperty(name)!.SetValue(target, value);
}
