using Ecr.Bootstrap.Excel;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Bootstrap.Tests;

/// <summary>Коди з тексту книги: детерміновані, унікальні в межах батька, валідні для домену.</summary>
public sealed class CodeGeneratorTests
{
    [Fact]
    public void Кирилиця_транслітерується()
        => Assert.Equal("VYKYDY_NOX", new CodeGenerator().Next("Викиди NOx", "C", 1));

    [Fact]
    public void Збіг_розводиться_суфіксом()
    {
        var codes = new CodeGenerator();

        Assert.Equal("MASA", codes.Next("Маса", "C", 1));
        Assert.Equal("MASA_2", codes.Next("маса", "C", 2));
        Assert.Equal("MASA_3", codes.Next("Маса!", "C", 3));
    }

    [Fact]
    public void Порожній_текст_дає_запасний_код_за_позицією()
        => Assert.Equal("C3", new CodeGenerator().Next("—", "C", 3));

    [Fact]
    public void Код_не_починається_з_цифри()
        => Assert.Equal("R_1_1_KOTEL", new CodeGenerator().Next("1.1 Котел", "R", 1));

    [Theory]
    [InlineData("Сумарний валовий викид забруднюючих речовин в атмосферне повітря від стаціонарних джерел")]
    [InlineData("Ґрунт їжак щука є")]
    [InlineData("Қазақ тілі")]
    [InlineData("123")]
    [InlineData("  ")]
    public void Будь_який_текст_дає_валідний_EcrCode_і_RowKey(string text)
    {
        var code = new CodeGenerator().Next(text, "C", 7);

        Assert.True(EcrCode.TryCreate(code, out _), code);
        Assert.True(RowKey.TryCreate(code, out _), code);
        Assert.True(code.Length <= CodeGenerator.MaxLength, code);
    }
}
