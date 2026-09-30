// tests/Ecr.Domain.Tests/Calculations/CalculationRunRegistryAsOfTests.cs
using Ecr.Domain.Entities.Calculations;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Момент знімка довідників прогону (RT-05, FEATURE-REGISTRY-TABLES §3.6,
/// <c>D-158</c>): ставиться на старті й більше не змінюється.
/// </summary>
/// <remarks>
/// Мутаційний доказ (§9.2): не ставити <c>RegistryAsOfUtc</c> у старті прогону —
/// червоніє <see cref="Прогін_фіксує_момент_знімка"/>.
/// </remarks>
public sealed class CalculationRunRegistryAsOfTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 30, 15, 123, DateTimeKind.Utc);

    [Fact]
    [Trait("Directive", "RT-05")]
    public void Прогін_фіксує_момент_знімка()
    {
        var run = new CalculationRun(projectId: 7, periodKey: 202609, triggeredByUserId: 3, Now);

        Assert.Equal(Now, run.RegistryAsOfUtc);
        Assert.Equal(DateTimeKind.Utc, run.RegistryAsOfUtc!.Value.Kind);
        Assert.Equal(run.StartedAt, run.RegistryAsOfUtc);
    }

    [Fact]
    [Trait("Directive", "RT-05")]
    public void Завершення_й_перемикання_актуальності_момент_не_зсувають()
    {
        var run = new CalculationRun(projectId: 7, periodKey: null, triggeredByUserId: null, Now);

        // Прогін триває хвилини: якби момент брався на завершенні, частина
        // прив'язок читала б довідник до правки, частина — після.
        run.Complete("Succeeded", Now.AddMinutes(12), profileJson: null, errorMessage: null);
        run.MakeCurrent();
        run.Supersede();

        Assert.Equal(Now, run.RegistryAsOfUtc);
    }

    [Fact]
    [Trait("Directive", "RT-05")]
    public void Момент_відкидає_частку_мілісекунди_а_не_округлює_вперед()
    {
        // 0.9 мс після рівної мілісекунди: округлення дало б момент ПІЗНІШЕ
        // старту, і правка довідника в цю мілісекунду потрапила б у знімок.
        var start = Now.AddTicks(9_999);

        var run = new CalculationRun(projectId: 7, periodKey: 202609, triggeredByUserId: null, start);

        Assert.Equal(Now, run.RegistryAsOfUtc);
    }

    [Fact]
    [Trait("Directive", "RT-05")]
    public void Місцевий_час_переводиться_в_UTC_невизначений_вважається_UTC()
    {
        var local = new CalculationRun(1, null, null, Now.ToLocalTime());
        var unspecified = new CalculationRun(1, null, null, DateTime.SpecifyKind(Now, DateTimeKind.Unspecified));

        Assert.Equal(Now, local.RegistryAsOfUtc);
        Assert.Equal(DateTimeKind.Utc, local.RegistryAsOfUtc!.Value.Kind);
        Assert.Equal(Now, unspecified.RegistryAsOfUtc);
        Assert.Equal(DateTimeKind.Utc, unspecified.RegistryAsOfUtc!.Value.Kind);
    }
}
