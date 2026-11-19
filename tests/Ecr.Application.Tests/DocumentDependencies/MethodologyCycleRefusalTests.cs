// tests/Ecr.Application.Tests/DocumentDependencies/MethodologyCycleRefusalTests.cs
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.DocumentDependencies;

/// <summary>
/// MS x B179: відмова публікації (чи перемикання) методології, чиї ребра замкнули НОВИЙ цикл залежностей документів, —
/// як L3 для шаблонів: шлях кодами, без блокування того, що вже лежало на циклі, і без хибних відмов.
/// </summary>
/// <remarks>
/// Мутації: у <c>RefuseMethodologiesAsync</c> прибрати <c>!alreadyOnCycle.Contains(id)</c> - червоніє тест про наявний
/// цикл; прибрати кидок - червоніє тест про шлях; брати <c>GetAsync</c> замість <c>GetFreshAsync</c> - NSubstitute-тест
/// про свіжий граф червоний.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait("Directive", "B179")]
public sealed class MethodologyCycleRefusalTests
{
    private static readonly int[] Reader = [8];

    /// <summary>
    /// HSE400 (10) наповнює склад (100); методологія C (7) читає склад і пише в Land (20); скрипт S (8) читає РЕЗУЛЬТАТИ C
    /// й пише в шаблон-джерело (10 або 30). Якщо S пише в 10 - цикл 10 - склад - C - S - 10.
    /// </summary>
    private static DependencyGraphSnapshot Graph(int readerWritesInto)
        => DependencyGraphAssembler.Assemble(new DependencyGraphInputs(
            "r",
            new Dictionary<int, string> { [10] = "HSE400", [20] = "LAND", [30] = "REPORT" },
            new Dictionary<int, string> { [100] = "COMPOSITION" },
            new Dictionary<int, string> { [7] = "C", [8] = "S" },
            [new SourceBindingRow(1, 10, 100, DocumentSourceState.Any)],
            [],
            [new MethodologyScopedRead(7, 70, 100, "F1", PeriodScope.Year)],
            [new MethodologyTemplateWrite(7, 20, 900, "t"), new MethodologyTemplateWrite(8, readerWritesInto, 901, "t")],
            [],
            [])
        {
            MethodologyResultReads = [new MethodologyResultRead(8, 7)],
        });

    private static IDependencyGraphProvider Provider(DependencyGraphSnapshot fresh, DependencyGraphSnapshot? before = null)
    {
        var provider = Substitute.For<IDependencyGraphProvider>();
        provider.GetFreshAsync(Arg.Any<CancellationToken>()).Returns(before ?? fresh, fresh);
        return provider;
    }

    [Fact]
    public async Task Читання_результатів_що_замикає_цикл_відмовляє_зі_шляхом_кодами()
    {
        var provider = Provider(Graph(readerWritesInto: 10), Graph(readerWritesInto: 30));
        var before = await DependencyCyclePublishCheck.MethodologiesOnCycleAsync(provider, Reader, default);
        Assert.Empty(before);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => DependencyCyclePublishCheck.RefuseMethodologiesAsync(provider, Reader, before, default));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal(DependencyCyclePublishCheck.MessageKey, error.Details!["messageKey"]);
        Assert.Equal("S → HSE400 → COMPOSITION → C → S", error.Details["cycle"]);
    }

    [Fact]
    public async Task Без_циклу_відмови_немає()
    {
        var provider = Provider(Graph(readerWritesInto: 30));
        var before = await DependencyCyclePublishCheck.MethodologiesOnCycleAsync(provider, Reader, default);

        await DependencyCyclePublishCheck.RefuseMethodologiesAsync(provider, Reader, before, default);
    }

    [Fact]
    public async Task Методологія_що_вже_лежала_на_циклі_не_блокується()
    {
        var provider = Provider(Graph(readerWritesInto: 10));
        var before = await DependencyCyclePublishCheck.MethodologiesOnCycleAsync(provider, Reader, default);
        Assert.Contains(8, before);

        await DependencyCyclePublishCheck.RefuseMethodologiesAsync(provider, Reader, before, default);
    }

    [Fact]
    public async Task Без_графа_перевірки_немає()
    {
        var before = await DependencyCyclePublishCheck.MethodologiesOnCycleAsync(null, Reader, default);
        await DependencyCyclePublishCheck.RefuseMethodologiesAsync(null, Reader, before, default);
    }
}
