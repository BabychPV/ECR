using Ecr.Setup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests.Setup;

/// <summary>
/// X4-05 (аудит R6): текст відмови кроку 2 <c>deploy-ecr.ps1</c> радить <c>-SkipOtherNodesCheck</c>, а майстер
/// його передати не вмів. Тепер — лише за явною позначкою на кроці бази й лише коли схему змінюють.
/// </summary>
/// <remarks>
/// Мутації: <c>SkipOtherNodesCheck</c> без позначки → червоний <see cref="Без_позначки_не_передає_SkipOtherNodesCheck"/>;
/// не прибрати позначку за <c>SkipSchema</c> → червоний <see cref="Позначка_за_SkipSchema_не_діє"/>; прибрати
/// додавання параметра в <c>DeployArguments</c> → червоний <see cref="З_позначкою_передає_SkipOtherNodesCheck_перемикачем"/>.
/// </remarks>
public sealed class SetupWizardOtherNodesTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(true)]
    [InlineData(false)]
    public void З_позначкою_передає_SkipOtherNodesCheck_перемикачем(bool firstDeployment)
    {
        var state = new WizardState
        {
            Mode = firstDeployment ? WizardMode.FirstDeployment : WizardMode.Update,
            OtherNodesStoppedAccepted = true,
        };

        var skip = DeployArguments.Build(state).Single(a => a.Key == "SkipOtherNodesCheck");

        Assert.Null(skip.Value);
        Assert.True(state.SkipOtherNodesCheck);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Без_позначки_не_передає_SkipOtherNodesCheck()
    {
        var state = new WizardState { Mode = WizardMode.Update };

        Assert.False(state.SkipOtherNodesCheck);
        Assert.DoesNotContain("SkipOtherNodesCheck", DeployArguments.Build(state).Select(a => a.Key));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Позначка_за_SkipSchema_не_діє()
    {
        // З -SkipSchema крок 2 не виконується, тож перевірки інших вузлів там немає — обходити нічого.
        var state = new WizardState { Mode = WizardMode.Update, SkipSchema = true, OtherNodesStoppedAccepted = true };

        Assert.False(state.SkipOtherNodesCheck);
        Assert.DoesNotContain("SkipOtherNodesCheck", DeployArguments.Build(state).Select(a => a.Key));
    }

    /// <remarks>
    /// Ім'я параметра, яке передає майстер, існує в скрипті, а крок бази записує позначку в стан
    /// (лише коли схему не пропущено).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Параметр_є_у_скрипті_а_крок_бази_пише_позначку_у_стан()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));
        var step = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "Ecr.Setup", "Steps", "DatabaseStep.cs"));

        Assert.Contains("[switch] $SkipOtherNodesCheck", script, StringComparison.Ordinal);
        Assert.Contains("state.OtherNodesStoppedAccepted = !state.SkipSchema && _otherNodesAcceptCheckBox!.Checked;", step, StringComparison.Ordinal);
    }
}
