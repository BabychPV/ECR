// tests/Ecr.Infrastructure.Tests/Jobs/MonthOwnerRoutingTests.cs
using Ecr.Application.DocumentDependencies;
using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Binding;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ W15z M1 (репетиція на справжніх даних, b11 @c2f9e18a7): граф Land для зв'язки HSE400_LAND_COMPOSITION мав ребро
/// <c>Year/0, formula=True, methodology=True</c> — старі опубліковані версії шаблону (1.0.8–1.0.18, PerMonthCopies) читають
/// довідник поза власником місяця (обсяг «рік»), а методології 15/16/23/40/42 — обсягом «місяць». Рішення «по шаблону»
/// (AND по всіх версіях, будь-яка методологія — увесь рік) будило p202611 при правці M10. Тепер: формули — по версіях
/// (<see cref="TemplateDependency.OwnedVersions"/>), методології — окремо (<see cref="TemplateDependency.MethodologyScope"/>):
/// «місяць» сумісний зі звуженням, «рік»/«усе»/читач результатів — увесь рік.
/// </summary>
/// <remarks>
/// Мутації: (1) рішення «по шаблону» (<c>RefusalReason</c> відмовляє на <c>!MonthOwned</c>) — червоний «форма Land»;
/// (2) будь-яка методологія — відмова — червоний «форма Land»; (3) обсяг методології не перевіряється — червоні «рік» і
/// «читач результатів».
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait("Finding", "PERF-06b")]
public sealed class MonthOwnerRoutingTests
{
    private const int Source = 10;
    private const int Consumer = 20;
    private const int Registry = 100;
    private const int OldVersion = 2;
    private const int NewVersion = 23;

    [Fact]
    public void Форма_Land_стара_версія_й_методологія_місяця_звуження_діє_для_нової_версії()
    {
        var edges = Edges(PeriodScope.Month);

        var edge = Assert.Single(edges);
        Assert.Equal(PeriodScopeKind.Year, edge.Scope.Kind);
        Assert.Equal(0, edge.Scope.MonthMask);
        Assert.False(edge.MonthOwned);
        Assert.Equal([NewVersion], edge.OwnedVersions);
        Assert.Equal(PeriodScopeKind.Month, edge.MethodologyScope?.Kind);

        Assert.Null(MonthOwnerRouting.RefusalReason(edges));
        Assert.Equal([NewVersion], MonthOwnerRouting.OwnedVersions(edges));
    }

    [Fact]
    public void Методологія_обсягом_рік_увесь_рік()
        => Assert.NotNull(MonthOwnerRouting.RefusalReason(Edges(PeriodScope.Year)));

    [Fact]
    public void Читач_результатів_методології_увесь_рік()
        => Assert.NotNull(MonthOwnerRouting.RefusalReason(Edges(PeriodScope.Month, resultReader: true)));

    [Fact]
    public void Лише_стара_версія_увесь_рік()
    {
        var edges = Edges(methodologyScope: null, newVersion: false);
        Assert.Empty(MonthOwnerRouting.OwnedVersions(edges));
        Assert.NotNull(MonthOwnerRouting.RefusalReason(edges));
    }

    private static List<TemplateDependency> Edges(PeriodScope? methodologyScope, bool resultReader = false, bool newVersion = true)
    {
        var reads = new List<TemplateRegistryRead>
        {
            new(Consumer, OldVersion, Registry, PeriodScope.Year, 1, "formulaDependency", "old"),
        };
        if (newVersion)
        {
            reads.Add(new(Consumer, NewVersion, Registry, PeriodScope.FixedMonth(10), 2, "formulaDependency", "M10", MonthOwned: true));
            reads.Add(new(Consumer, NewVersion, Registry, PeriodScope.FixedMonth(11), 3, "formulaDependency", "M11", MonthOwned: true));
        }

        var snapshot = DependencyGraphAssembler.Assemble(new DependencyGraphInputs(
            "r",
            new Dictionary<int, string> { [Source] = "HSE400", [Consumer] = "LAND" },
            new Dictionary<int, string> { [Registry] = "COMPOSITION" },
            new Dictionary<int, string> { [7] = "M7", [8] = "M8" },
            [new SourceBindingRow(1, Source, Registry, DocumentSourceState.Any)],
            reads,
            methodologyScope is { } scope ? [new MethodologyScopedRead(7, 70, Registry, "F", scope)] : [],
            methodologyScope is null ? [] : [new MethodologyTemplateWrite(resultReader ? 8 : 7, Consumer, 900, "t")],
            [],
            [])
        {
            MethodologyResultReads = resultReader ? [new MethodologyResultRead(8, 7)] : [],
        });

        return [.. snapshot.TemplateDependencies.Where(d => d.BindingId == 1 && d.ConsumerTemplateId == Consumer)];
    }
}
