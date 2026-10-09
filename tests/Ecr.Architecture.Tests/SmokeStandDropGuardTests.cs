using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// L10-13: <c>smoke.ps1</c> і <c>e2e-stand.ps1</c> (як і <c>setup-dev-db.ps1</c> та <c>verify-sql-scripts.ps1</c>, див. нижче) відмовляються розгортати стенд поверх
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

    /// <remarks>
    /// L10-13 (аудит 2026-10-09): сам <c>setup-dev-db.ps1</c> і <c>verify-sql-scripts.ps1</c> теж безумовно роблять
    /// <c>DROP DATABASE</c> за іменем. Тепер наявна база без позначки (<c>Ecr_DevDb</c> / <c>Ecr_SmallFiles</c>,
    /// яку скрипт ставить сам одразу після <c>CREATE DATABASE</c>) — відмова ДО <c>DROP</c>, якщо не передано
    /// <c>-Force</c>. Порядок у тексті: перевірка → відмова → DROP → позначка. Мутація (CI): прибрати блок
    /// <c>if (-not $Force)</c> зі скрипта → тест червоний. Запит — той самий, що в smoke/e2e; на живому SQL Server цим тестом не проганяється.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("setup-dev-db.ps1", "Ecr_DevDb", "DROP DATABASE [$Database];")]
    [InlineData("verify-sql-scripts.ps1", "Ecr_SmallFiles", "IF DB_ID('$Database') IS NOT NULL DROP DATABASE [$Database]; CREATE DATABASE")]
    public void Скрипт_що_створює_базу_відмовляє_за_наявної_бази_без_позначки_до_DROP(string script, string marker, string drop)
    {
        var text = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", script));

        Assert.Contains("[switch] $Force", text, StringComparison.Ordinal);
        var force = text.IndexOf("if (-not $Force)", StringComparison.Ordinal);
        var guard = text.IndexOf($"name = N''{marker}''", StringComparison.Ordinal);
        var dropAt = text.IndexOf(drop, StringComparison.Ordinal);

        Assert.True(force > 0 && guard > force, $"{script}: перевірку маркера {marker} не загорнуто в if (-not $Force)");
        Assert.True(dropAt > guard, $"{script}: DROP DATABASE стоїть до перевірки маркера");

        var refusal = text.IndexOf("не має позначки " + marker, guard, StringComparison.Ordinal);
        Assert.True(refusal > guard && refusal < dropAt, $"{script}: після перевірки немає відмови (throw) до DROP");

        // Позначка, яку скрипт ставить після власного CREATE, — інакше він відмовився б перестворювати власну базу.
        Assert.True(
            text.IndexOf($"@name = N'{marker}'", dropAt, StringComparison.Ordinal) > dropAt,
            $"{script}: після CREATE DATABASE не ставиться позначка {marker}");
    }

    /// <remarks>
    /// Smoke і e2e самі перевіряють свою тимчасову базу за власним маркером ДО виклику <c>setup-dev-db.ps1</c>, тож
    /// передають йому <c>-Force</c>; без цього перестворення їхньої ж бази відмовлялось би.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("smoke.ps1")]
    [InlineData("e2e-stand.ps1")]
    public void Стенд_передає_Force_у_setup_dev_db_після_власної_перевірки_маркера(string script)
    {
        var text = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", script));

        Assert.Contains("$setupExtra += '-Force'", text, StringComparison.Ordinal);
        Assert.Contains("@setupExtra", text, StringComparison.Ordinal);
    }
}

