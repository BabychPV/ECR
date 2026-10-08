// tests/Ecr.Application.Tests/Templates/PublishDuplicateColumnBindingTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// P2-1: публікація версії відмовляє, коли на одну колонку дві активні прив'язки з однаковим
/// предикатом (<see cref="PublishChecks.CheckDuplicateColumnBindings"/>); різні предикати законні.
/// </summary>
public sealed class PublishDuplicateColumnBindingTests
{
    /// <remarks>Мутація: прибрати порівняння предикатів — перший випадок перестає давати зауваження.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Однаковий_предикат_на_одній_колонці_дає_одне_зауваження_з_ключем()
    {
        var diagnostics = PublishChecks.CheckDuplicateColumnBindings(
        [
            new ActiveColumnBinding(10, "T1", "EMISSION", "M1", "EMISSION", "{}"),
            new ActiveColumnBinding(10, "T1", "EMISSION", "M2", "GSEC", "{ }"),
            new ActiveColumnBinding(11, "T1", "OTHER", "M1", "X", "{}"),
        ]);

        var one = Assert.Single(diagnostics);
        Assert.Equal("ECR-TMPL-0422", one.Code);
        Assert.Equal("err.ECR-TMPL-0422.bindingColumnConflict", one.MessageKey);
    }

    /// <remarks>
    /// RC14 (Land): методології з правилами вибору на тій самій колонці не блокують публікацію,
    /// а потрапляють у попередження. Мутація: ігнорувати <c>HasSelectionRules</c> — тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Методології_з_правилами_вибору_не_блокують_а_дають_попередження()
    {
        ActiveColumnBinding[] land =
        [
            new(10, "T1", "EMISSION", "M51", "EMISSION", "{}", true),
            new(10, "T1", "EMISSION", "M52", "EMISSION", "{}", true),
            new(10, "T1", "EMISSION", "M77", "EMISSION", "{}", false),
        ];

        Assert.Empty(PublishChecks.CheckDuplicateColumnBindings(land));
        var warning = Assert.Single(PublishChecks.FindRuleSelectedColumnConflicts(land));
        Assert.Contains("T1.EMISSION", warning, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Без_жодних_правил_вибору_блокує_і_попередження_немає()
    {
        ActiveColumnBinding[] plain =
        [
            new(10, "T1", "EMISSION", "M1", "EMISSION", "{}"),
            new(10, "T1", "EMISSION", "M2", "EMISSION", "{}"),
        ];

        Assert.Single(PublishChecks.CheckDuplicateColumnBindings(plain));
        Assert.Empty(PublishChecks.FindRuleSelectedColumnConflicts(plain));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Різні_предикати_на_одній_колонці_законні()
    {
        var diagnostics = PublishChecks.CheckDuplicateColumnBindings(
        [
            new ActiveColumnBinding(10, "T1", "EMISSION", "M1", "EMISSION", "{\"1\":\"a\"}"),
            new ActiveColumnBinding(10, "T1", "EMISSION", "M2", "GSEC", "{\"1\":\"b\"}"),
        ]);

        Assert.Empty(diagnostics);
    }
}
