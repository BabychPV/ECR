// tests/Ecr.Expressions.Tests/Parsing/ReferenceParsingTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>Чотири скорочені форми посилання, плейсхолдер місяця і крос-період.</summary>
public sealed class ReferenceParsingTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("[Jan]")]
    [InlineData("[7001001].[Jan]")]
    [InlineData("[Main].[7001001].[Jan]")]
    [InlineData("[Water_07].[Main].[7001001].[Jan]")]
    public void Чотири_форми_посилання_розбираються(string expression)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Плейсхолдер_Month_підставляє_колонку_місяця()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Крос_період_розбирається_з_від_ємним_і_додатним_зсувом()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вихід_за_межі_проєкту_дає_null_а_не_помилку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діапазон_рядків_розбирається_у_список_ключів()
        => Assert.Fail("not implemented");
}
