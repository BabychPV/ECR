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
