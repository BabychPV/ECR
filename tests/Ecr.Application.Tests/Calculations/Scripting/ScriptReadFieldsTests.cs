using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// ✎ D-568: поля власної таблиці, які читає скрипт (<see cref="ScriptReadFields.Of"/>), і їхній запис при публікації — за ними
/// прохід читача знаходить скрипти, що читають число іншого місяця.
/// </summary>
public sealed class ScriptReadFieldsTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D-568")]
    [InlineData("rows from T1; var a = row.Hours[period.Month]; output O1 : t = a;", false)]
    [InlineData("rows from T1; var a = row.Hours[3]; output O1 : t = a;", true)]
    [InlineData("rows from T1; var a = row.Hours[period.Month - 1]; output O1 : t = a;", true)]
    [InlineData("rows from T1; var a = row.Hours[period.Month] + row.Hours[1]; output O1 : t = a;", true)]
    public void Місячне_поле_за_інший_місяць_позначається_а_свій_місяць_ні(string text, bool otherMonths)
    {
        var script = ScriptTestKit.Compile(text).Script!;

        var field = Assert.Single(ScriptReadFields.Of(script));

        Assert.Equal("Hours", field.FieldCode, ignoreCase: true);
        Assert.Equal(otherMonths, field.OtherMonths);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D-568")]
    public void Не_місячне_поле_записується_без_ознаки_і_кожне_поле_окремо()
    {
        var script = ScriptTestKit.Compile(
            "rows from T1; var a = row.Volume + row.Hours[period.Month] + row.Fuel[2]; output O1 : t = a;").Script!;

        var fields = ScriptReadFields.Of(script).ToDictionary(f => f.FieldCode, f => f.OtherMonths, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(3, fields.Count);
        Assert.False(fields["Volume"]);
        Assert.False(fields["Hours"]);
        Assert.True(fields["Fuel"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D-568")]
    public async Task Публікація_записує_поля_читання_версії()
    {
        var store = ScriptTestKit.Store();
        var bindings = Substitute.For<ICalculationBindingStore>();
        bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CalculationBinding>>([]));
        var materializer = new ScriptMaterializer(store, Substitute.For<IMethodologyDraftStore>(), ScriptTestKit.Catalog(), bindings);
        var script = ScriptTestKit.Compile("rows from T1; var a = row.Hours[3]; output O1 : t = a;").Script!;

        await materializer.MaterializeAsync(
            50, 5, script, new ScriptEnvironment([], [ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours")])], []),
            new ScriptMaterializeOptions(Publish: true), default);

        await store.Received(1).ReplaceScriptReadFieldsAsync(
            50,
            Arg.Is<IReadOnlyList<ScriptReadField>>(f => f.Count == 1 && f[0].OtherMonths
                                                         && string.Equals(f[0].FieldCode, "Hours", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D-568")]
    public async Task Збереження_чернетки_поля_читання_не_пише()
    {
        var store = ScriptTestKit.Store();
        var materializer = new ScriptMaterializer(
            store, Substitute.For<IMethodologyDraftStore>(), ScriptTestKit.Catalog(), Substitute.For<ICalculationBindingStore>());
        var script = ScriptTestKit.Compile("rows from T1; var a = row.Hours[3]; output O1 : t = a;").Script!;

        await materializer.MaterializeAsync(
            50, 5, script, new ScriptEnvironment([], [], []), new ScriptMaterializeOptions(Publish: false), default);

        await store.DidNotReceiveWithAnyArgs().ReplaceScriptReadFieldsAsync(default, default!, default);
    }
}
