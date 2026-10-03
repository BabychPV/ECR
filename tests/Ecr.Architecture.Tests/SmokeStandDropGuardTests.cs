using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// L10-13: <c>smoke.ps1</c> і <c>e2e-stand.ps1</c> відмовляються розгортати стенд поверх
/// наявної бази без СВОГО маркера ДО виклику <c>setup-dev-db.ps1</c>.
/// </summary>
/// <remarks>
/// ⛔ Предмет. Маркер (<c>Ecr_Smoke_Temp</c> / <c>Ecr_E2E_Temp</c>) ставиться вже ПІСЛЯ
/// розгортання й захищає лише фінальне прибирання, а <c>setup-dev-db.ps1</c>
/// безумовно робить <c>DROP DATABASE</c> за іменем. Тож
/// <c>smoke.ps1 -Database EcrDev</c> стирав dev-базу ще на кроці 1. Сторож перевіряє
/// ПОРЯДОК у тексті скрипта (як <c>DeployWorkerModeTests</c>): перевірка маркера
/// стоїть раніше за запуск розгортання і веде до <c>Fail</c>. Логіку запиту доведено
/// вручну на тимчасовій базі (немає бази → 0, є без маркера → 1, є з маркером → 0).
/// Мутація (прогнано): прибрати блок перевірки з <c>smoke.ps1</c> → тест червоний.
/// </remarks>
public sealed class SmokeStandDropGuardTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("smoke.ps1", "Ecr_Smoke_Temp")]
    [InlineData("e2e-stand.ps1", "Ecr_E2E_Temp")]
    public void Наявна_база_без_маркера_відхиляється_до_розгортання(string script, string marker)
    {
        var text = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", script));

        var guard = text.IndexOf($"name = N''{marker}''", StringComparison.Ordinal);
        var deploy = text.IndexOf("'setup-dev-db.ps1')", StringComparison.Ordinal);

        Assert.True(guard > 0, $"{script}: немає перевірки маркера {marker} перед розгортанням");
        Assert.True(deploy > guard, $"{script}: розгортання (setup-dev-db.ps1) стоїть до перевірки маркера");

        var refusal = text.IndexOf("не має позначки " + marker, guard, StringComparison.Ordinal);
        Assert.True(refusal > guard && refusal < deploy, $"{script}: після перевірки немає відмови (Fail) до розгортання");
    }
}
