// tests/Ecr.Application.Tests/Templates/PublishRowDefaultsTests.cs
using Ecr.Application.Templates;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ✎ B175: повторна перевірка дефолтів комірок рядка на публікації (<see cref="PublishChecks.CheckStructure"/>) — код
/// колонки, якого вже немає (видалено / «перейменовано»), відхиляє публікацію окремим <c>messageKey</c>.
/// </summary>
/// <remarks>Мутація: прибрати <c>CheckRowDefaults</c> із <c>CheckStructure</c> — тест червоний.</remarks>
public sealed class PublishRowDefaultsTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "B175")]
    public void Публікація_відмовляє_коли_код_дефолту_рядка_більше_не_розв_язується_після_перейменування_колонки()
    {
        // «Перейменування» колонки в чернетці — видалити стару й завести нову з іншим кодом (коду сеттер не дає).
        var builder = new TemplateBuilder { TemplateVersionId = 2 };
        var table = builder.Table(builder.Sheet("Land"), "T7b");
        var oldColumn = builder.Column(table, "Qty");
        builder.Row(table, "R1").SetCellDefaults(new Dictionary<string, string> { ["Qty"] = "2" });
        var version = builder.Version();

        Assert.DoesNotContain(PublishChecks.CheckStructure(version), d => d.MessageKey == "err.ECR-TMPL-0422.rowDefaultColumnMissing");

        oldColumn.SoftDelete(9, Now);
        builder.Column(table, "Quantity");

        var diagnostic = Assert.Single(
            PublishChecks.CheckStructure(version), d => d.MessageKey == "err.ECR-TMPL-0422.rowDefaultColumnMissing");
        Assert.Equal("ECR-TMPL-0422", diagnostic.Code);
        Assert.Equal("Qty", diagnostic.MessageParams!["columnCode"]);
        Assert.Equal("R1", diagnostic.MessageParams["rowKey"]);
    }
}
