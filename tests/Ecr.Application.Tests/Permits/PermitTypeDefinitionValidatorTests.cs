// tests/Ecr.Application.Tests/Permits/PermitTypeDefinitionValidatorTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits;
using Ecr.Application.Permits.Dto;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>Перевірка складу типу дозволу до збереження чернетки: кожен випадок має свій <c>messageKey</c> (PM-03).</summary>
public sealed class PermitTypeDefinitionValidatorTests
{
    [Fact]
    public void Коректний_склад_проходить()
        => PermitTypeDefinitionValidator.Validate("AIR", Content(Table("Emissions", Key("Substance"), Col("Limit", "Decimal"))), null);

    [Fact]
    public void Таблиця_без_ключового_стовпця_відхиляється()
        => Refused("tableWithoutKey", Content(Table("Emissions", Col("Limit", "Decimal"))));

    [Fact]
    public void Повторні_коди_таблиць_стовпців_і_перевірок_відхиляються()
    {
        Refused("tableDuplicate", Content(Table("T", Key("A")), Table("t", Key("A"))));
        Refused("columnDuplicate", Content(Table("T", Key("A"), Col("a", "String"))));
        Refused("ruleDuplicate", Content(Table("T", Key("A")) with { Rules = [Rule("R"), Rule("r")] }));
    }

    [Theory]
    [InlineData("1abc", "tableCodeInvalid")]
    [InlineData("a b", "tableCodeInvalid")]
    public void Недопустимий_код_таблиці_відхиляється(string code, string key)
        => Refused(key, Content(Table(code, Key("A"))));

    [Fact]
    public void Код_стовпця_PERMIT_зарезервовано_а_недопустимий_відхиляється()
    {
        Refused("columnCodeReserved", Content(Table("T", Key("A"), Col("permit", "String"))));
        Refused("columnCodeInvalid", Content(Table("T", Key("A"), Col("1x", "String"))));
    }

    [Fact]
    public void Задовгий_код_реєстру_таблиці_відхиляється()
        => Refused("registryCodeInvalid", Content(Table(new string('T', 62), Key("A"))));

    [Fact]
    public void Невідомий_тип_стовпця_відхиляється()
        => Refused("columnTypeUnknown", Content(Table("T", Key("A"), Col("B", "Formula"))));

    [Theory]
    [InlineData("String", (byte)10, null)]
    [InlineData("Decimal", (byte)39, null)]
    [InlineData("Decimal", (byte)5, (byte)6)]
    [InlineData("Decimal", null, (byte)2)]
    public void Некоректні_точність_і_масштаб_відхиляються(string type, byte? precision, byte? scale)
        => Refused("numericFormat", Content(Table("T", Key("A"), Col("B", type) with { Precision = precision, Scale = scale })));

    [Fact]
    public void Узгоджені_списки_трьох_областей_проходять()
    {
        var content = Content(
            Table("Places", Key("Place")),
            Table(
                "Limits",
                Key("Place") with { DataType = "Lookup", ListScope = "SamePermitTable", SiblingTableCode = "Places" },
                Col("Kind", "Lookup") with { ListScope = "TypeOnly", Choices = ["Hazardous", "Safe"] },
                Col("Site", "Lookup") with { ListScope = "SharedRegistry", ListRegistryCode = "SITES" }));

        PermitTypeDefinitionValidator.Validate("WASTE", content, null);
    }

    [Fact]
    public void Суперечливий_список_відхиляється_у_кожній_області()
    {
        Refused("listInconsistent", Content(Table("T", Key("A"), Col("B", "Lookup")))); // без області
        Refused("listInconsistent", Content(Table("T", Key("A"), Col("B", "Lookup") with { ListScope = "SharedRegistry" })));
        Refused("listInconsistent", Content(Table("T", Key("A"), Col("B", "Lookup") with { ListScope = "SharedRegistry", ListRegistryCode = "X", Choices = ["a"] })));
        Refused("listInconsistent", Content(Table("T", Key("A"), Col("B", "Lookup") with { ListScope = "SamePermitTable" })));
        Refused("listInconsistent", Content(Table("T", Key("A"), Col("B", "Lookup") with { ListScope = "7" })));
        Refused("listForNonLookup", Content(Table("T", Key("A"), Col("B", "String") with { Choices = ["a"] })));
    }

    [Fact]
    public void Список_лише_для_типу_без_значень_чи_з_повторами_відхиляється()
    {
        Refused("typeOnlyNoChoices", Content(Table("T", Key("A"), Col("B", "Lookup") with { ListScope = "TypeOnly", Choices = [] })));
        Refused("choicesInvalid", Content(Table("T", Key("A"), Col("B", "Lookup") with { ListScope = "TypeOnly", Choices = ["a", "A"] })));
        Refused("choicesInvalid", Content(Table("T", Key("A"), Col("B", "Lookup") with { ListScope = "TypeOnly", Choices = ["a", " "] })));
    }

    [Fact]
    public void Сестринська_таблиця_має_існувати_бути_іншою_і_без_кола()
    {
        Refused("siblingUnknown", Content(Table("T", Key("A"), Col("B", "Lookup") with { ListScope = "SamePermitTable", SiblingTableCode = "Nope" })));
        Refused("siblingUnknown", Content(Table("T", Key("A"), Col("B", "Lookup") with { ListScope = "SamePermitTable", SiblingTableCode = "T" })));

        var circle = Content(
            Table("A", Key("K"), Col("L", "Lookup") with { ListScope = "SamePermitTable", SiblingTableCode = "B" }),
            Table("B", Key("K"), Col("L", "Lookup") with { ListScope = "SamePermitTable", SiblingTableCode = "A" }));
        Refused("siblingCycle", circle);
    }

    [Fact]
    public void Перевірки_мають_вираз_і_відомий_рівень()
    {
        Refused("ruleExpressionRequired", Content(Table("T", Key("A")) with { Rules = [Rule("R") with { Expression = " " }] }));
        Refused("ruleSeverityUnknown", Content(Table("T", Key("A")) with { Rules = [Rule("R") with { Severity = "Error" }] }));
        Refused("ruleCodeInvalid", Content(Table("T", Key("A"))) with { Rules = [Rule("1R")] });
    }

    [Fact]
    public void Назва_і_категорія_обов_язкові()
    {
        var bad = Content(Table("T", Key("A")));
        Refused("nameRequired", bad with { NameL10n = new() { ["en"] = " " } });
        Refused("categoryUnknown", bad with { Category = "Soil" });
        Refused("categoryUnknown", bad with { Category = "1" });
    }

    [Fact]
    public void Опублікований_склад_не_можна_зламати()
    {
        var published = Content(Table("T", Key("A"), Col("B", "String")), Table("U", Key("A")));

        Refused("tableRemoved", Content(Table("T", Key("A"), Col("B", "String"))), published);
        Refused("columnRemoved", Content(Table("T", Key("A")), Table("U", Key("A"))), published);
        Refused("columnRetyped", Content(Table("T", Key("A"), Col("B", "Int")), Table("U", Key("A"))), published);
        Refused("keyChanged", Content(Table("T", Key("A"), Col("B", "String") with { Key = true }), Table("U", Key("A"))), published);

        // Додати стовпець і таблицю можна.
        PermitTypeDefinitionValidator.Validate(
            "AIR", Content(Table("T", Key("A"), Col("B", "String"), Col("C", "Int")), Table("U", Key("A")), Table("V", Key("A"))), published);
    }

    [Fact]
    public void Публікація_без_таблиць_і_з_короткою_причиною_відхиляється()
    {
        Refused2("noTables", () => PermitTypeDefinitionValidator.ValidateForPublish(Content()));
        Refused2("reasonTooShort", () => PermitTypeDefinitionValidator.ValidateReason("коротко"));
        PermitTypeDefinitionValidator.ValidateReason("достатньо довга причина");
    }

    private static void Refused(string key, PermitTypeContent content, PermitTypeContent? published = null)
        => Refused2(key, () => PermitTypeDefinitionValidator.Validate("AIR", content, published));

    private static void Refused2(string key, Action act)
    {
        var error = Assert.Throws<BusinessRuleException>(act);
        Assert.Equal("ECR-PRM-0422", error.ErrorCode);
        Assert.Equal("err.ECR-PRM-0422." + key, error.Details!["messageKey"]);
    }

    private static PermitTypeContent Content(params PermitTypeTableDto[] tables)
        => new(Name("Air"), "Air", tables, []);

    private static PermitTypeTableDto Table(string code, params PermitTypeColumnDto[] columns)
        => new(code, Name(code), 1, columns, []);

    private static PermitTypeColumnDto Key(string code) => Col(code, "String") with { Key = true, Required = true };

    private static PermitTypeColumnDto Col(string code, string type)
        => new(code, Name(code), type, null, false, false, null, null, null, null, null, null, null, 1);

    private static PermitTypeRuleDto Rule(string code) => new(code, Name(code), "[A] > 0", "Warn");

    private static Dictionary<string, string> Name(string en) => new() { ["en"] = en };
}
