using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Зв'язок між таблицями (<c>ФВ-2.12</c>) — опційний механізм із трьома
/// перевірками, кожна з яких закриває мовчазну ваду.
/// </summary>
/// <remarks>
/// ⚠ Перевірки живуть у домені, а не у формі, бо значення приходять із мережі
/// (<c>ФВ-2.13</c>): зв'язки налаштовуються у вебі. Форма, яка була б єдиною
/// перевіркою, впала б від першого прямого запиту.
/// </remarks>
public sealed class TableRelationDefTests
{
    private static TableRelationDef Relation()
        => new(EcrCode.Create("WaterRollup"), sourceTableDefId: 5, targetTableDefId: 6,
               TableRelationKind.Rollup, """{"by":"RowKey"}""");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-2.12")]
    public void Зв_язок_описує_джерело_приймач_і_вид()
    {
        var relation = Relation();

        Assert.Equal("WaterRollup", relation.Code);
        Assert.Equal(5, relation.SourceTableDefId);
        Assert.Equal(6, relation.TargetTableDefId);
        Assert.Equal(TableRelationKind.Rollup, relation.RelationKind);

        // Створений зв'язок діє: вимкнення — окрема дія, а не стан за
        // замовчуванням.
        Assert.True(relation.IsActive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-2.12")]
    public void Таблиця_не_може_бути_пов_язана_сама_із_собою()
    {
        // ⛔ Те саме, що обмеження `CK_Rel_NotSelf` у схемі. Без доменної
        // перевірки користувач діставав би не відмову, а помилку бази.
        var error = Assert.Throws<DomainException>(() =>
            new TableRelationDef(EcrCode.Create("Self"), 5, 5, TableRelationKind.Mirror, "{}"));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-2.12")]
    public void Зв_язок_без_зіставлення_рядків_відхиляється(string matchJson)
    {
        // ⚠ Найтихіша вада механізму: зв'язок виглядає налаштованим і не
        // з'єднує жодного рядка.
        var error = Assert.Throws<DomainException>(() =>
            new TableRelationDef(EcrCode.Create("Empty"), 5, 6, TableRelationKind.Rollup, matchJson));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-2.12")]
    public void Невідома_реакція_на_зміну_джерела_відхиляється()
    {
        var relation = Relation();

        // ⛔ Колонка `tinyint` без обмеження: база прийняла б будь-яке
        // значення до 255, і невідомий код потім мовчки читався б як «нічого
        // не робити» — зміна джерела перестала б перераховувати приймач.
        var error = Assert.Throws<DomainException>(() =>
            relation.Update(5, 6, TableRelationKind.Rollup, "{}", null, onSourceChange: 7, isActive: true));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);

        // Стан не зіпсовано: відхилена правка не лишає половини змін.
        Assert.Equal(TableRelationDef.OnSourceChangeRecalc, relation.OnSourceChange);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-2.12")]
    public void Правка_перезаписує_налаштування_і_не_чіпає_коду()
    {
        var relation = Relation();

        relation.Update(6, 5, TableRelationKind.Mirror, """{"by":"Code"}""",
                        """{"CO":"CO_MASS"}""", TableRelationDef.OnSourceChangeBlock, isActive: false);

        Assert.Equal("WaterRollup", relation.Code);
        Assert.Equal(6, relation.SourceTableDefId);
        Assert.Equal(5, relation.TargetTableDefId);
        Assert.Equal(TableRelationKind.Mirror, relation.RelationKind);
        Assert.Equal("""{"CO":"CO_MASS"}""", relation.MapJson);
        Assert.Equal(TableRelationDef.OnSourceChangeBlock, relation.OnSourceChange);
        Assert.False(relation.IsActive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-2.12")]
    public void Порожній_мапінг_колонок_зводиться_до_null()
    {
        var relation = Relation();

        relation.Update(5, 6, TableRelationKind.Rollup, "{}", "   ",
                        TableRelationDef.OnSourceChangeRecalc, isActive: true);

        // «Перенесення немає» і «перенесення описане порожнім рядком» — різні
        // стани, і другий не має сенсу.
        Assert.Null(relation.MapJson);
    }
}
