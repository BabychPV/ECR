using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Рушій і скрипт версії методології (MS01): <c>SetScript</c>, <c>SwitchEngine</c>, <c>CloneAsDraft</c>.
/// </summary>
public sealed class MethodologyScriptTests
{
    private static readonly DateTime Now = new(2099, 2, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Hash = [1, 2, 3];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Нова_версія_за_замовчуванням_має_рушій_формул_і_порожній_скрипт()
    {
        var version = Draft();

        Assert.Equal(MethodologyEngine.Formulas, version.Engine);
        Assert.Null(version.ScriptText);
        Assert.Null(version.ScriptHash);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Конструктор_приймає_рушій()
    {
        Assert.Equal(MethodologyEngine.Script, Draft(MethodologyEngine.Script).Engine);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void SetScript_зберігає_текст_і_хеш_у_чернетці()
    {
        var version = Draft(MethodologyEngine.Script);

        version.SetScript("rows from T1;", Hash);

        Assert.Equal("rows from T1;", version.ScriptText);
        Assert.Equal(Hash, version.ScriptHash);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetScript_відхиляє_порожній_текст(string text)
    {
        var version = Draft(MethodologyEngine.Script);

        Assert.ThrowsAny<ArgumentException>(() => version.SetScript(text, Hash));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void SetScript_для_опублікованої_версії_зі_скриптом_заборонено()
    {
        var version = Draft(MethodologyEngine.Script);
        version.SetScript("rows from T1;", Hash);
        version.Publish(9, "Причина", new DateOnly(2099, 1, 1), testsPassed: true, Now);

        var error = Assert.Throws<DomainException>(() => version.SetScript("rows from T2;", Hash));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal("rows from T1;", version.ScriptText);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void SetScript_як_тіньовий_запис_дозволено_для_опублікованої_формульної_версії()
    {
        var version = Draft();
        version.Publish(9, "Причина", new DateOnly(2099, 1, 1), testsPassed: true, Now);

        version.SetScript("var a = 1;", Hash);

        Assert.Equal("var a = 1;", version.ScriptText);
        Assert.Equal(MethodologyEngine.Formulas, version.Engine);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void SwitchEngine_на_скрипт_без_тексту_відхиляється()
    {
        var version = Draft();

        var error = Assert.Throws<DomainException>(() => version.SwitchEngine(MethodologyEngine.Script));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal(MethodologyEngine.Formulas, version.Engine);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void SwitchEngine_на_скрипт_з_текстом_і_назад_працює_в_чернетці()
    {
        var version = Draft();
        version.SetScript("rows from T1;", Hash);

        version.SwitchEngine(MethodologyEngine.Script);
        Assert.Equal(MethodologyEngine.Script, version.Engine);

        version.SwitchEngine(MethodologyEngine.Formulas);
        Assert.Equal(MethodologyEngine.Formulas, version.Engine);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void SwitchEngine_для_опублікованої_версії_заборонено()
    {
        var version = Draft();
        version.SetScript("rows from T1;", Hash);
        version.Publish(9, "Причина", new DateOnly(2099, 1, 1), testsPassed: true, Now);

        var error = Assert.Throws<DomainException>(() => version.SwitchEngine(MethodologyEngine.Script));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void SwitchEngineForMigration_перемикає_опубліковану_версію_туди_й_назад_не_чіпаючи_тексту()
    {
        var version = Draft();
        version.SetScript("rows from T1;", Hash);
        version.Publish(9, "Причина", new DateOnly(2099, 1, 1), testsPassed: true, Now);

        version.SwitchEngineForMigration(MethodologyEngine.Script);
        Assert.Equal(MethodologyEngine.Script, version.Engine);
        Assert.Equal("rows from T1;", version.ScriptText);

        version.SwitchEngineForMigration(MethodologyEngine.Formulas);
        Assert.Equal(MethodologyEngine.Formulas, version.Engine);
        Assert.Equal("rows from T1;", version.ScriptText);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void SwitchEngineForMigration_на_скрипт_без_тексту_відхиляється()
    {
        var version = Draft();
        version.Publish(9, "Причина", new DateOnly(2099, 1, 1), testsPassed: true, Now);

        var error = Assert.Throws<DomainException>(() => version.SwitchEngineForMigration(MethodologyEngine.Script));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal(MethodologyEngine.Formulas, version.Engine);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void SwitchEngineForMigration_для_чернетки_відхиляється_її_перемикає_SwitchEngine()
    {
        var version = Draft();
        version.SetScript("rows from T1;", Hash);

        var error = Assert.Throws<DomainException>(() => version.SwitchEngineForMigration(MethodologyEngine.Script));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal(MethodologyEngine.Formulas, version.Engine);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void CloneAsDraft_переносить_рушій_текст_і_хеш()
    {
        var source = Draft(MethodologyEngine.Script);
        source.SetScript("rows from T1;", Hash);
        source.Publish(9, "Причина", new DateOnly(2099, 1, 1), testsPassed: true, Now);

        var clone = source.CloneAsDraft("2.0", 7, Now);

        Assert.Equal(MethodologyEngine.Script, clone.Engine);
        Assert.Equal("rows from T1;", clone.ScriptText);
        Assert.Equal(Hash, clone.ScriptHash);
        Assert.Equal(TemplateVersionStatus.Draft, clone.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void CloneAsDraft_формульної_версії_без_скрипта_лишає_скрипт_порожнім()
    {
        var clone = Draft().CloneAsDraft("2.0", 7, Now);

        Assert.Equal(MethodologyEngine.Formulas, clone.Engine);
        Assert.Null(clone.ScriptText);
        Assert.Null(clone.ScriptHash);
    }

    private static MethodologyVersion Draft(MethodologyEngine engine = MethodologyEngine.Formulas)
        => new(1, "1.0", CalculationLevel.Configuration, 7, Now, engine);
}
