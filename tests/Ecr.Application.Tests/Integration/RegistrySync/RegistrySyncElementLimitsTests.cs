// tests/Ecr.Application.Tests/Integration/RegistrySync/RegistrySyncElementLimitsTests.cs
using Ecr.Application.Integration.RegistrySync;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Dictionaries;
using Xunit;

namespace Ecr.Application.Tests.Integration.RegistrySync;

/// <summary>
/// Задовгий ідентифікатор чи шлях елемента джерела не доїжджає до <c>dic.RegistryExternalKey</c>
/// (аудит 2026-10-03, L4-03): знімок синку відкидає його з відмовою, а не валить прогін.
/// </summary>
/// <remarks>
/// Мутаційний доказ (2026-10-03, локальний worktree): у <see cref="RegistrySyncElementLimits.Fit"/>
/// прибрати гілку шляху → <see cref="Задовгий_шлях_не_оновлюється_і_дає_відмову"/> червоний;
/// гілку ідентифікатора → <see cref="Задовгий_ідентифікатор_виключає_елемент_зі_знімка"/> червоний.
/// </remarks>
public sealed class RegistrySyncElementLimitsTests
{
    [Fact]
    public void Елемент_у_межах_колонок_проходить_без_змін()
    {
        var element = new SourceElement("guid-1", "Stack1", new string('p', RegistryExternalKey.MaxExternalPathLength), "addr");

        var (fit, rejection) = RegistrySyncElementLimits.Fit(element);

        Assert.Same(element, fit);
        Assert.Null(rejection);
    }

    [Fact]
    public void Задовгий_шлях_не_оновлюється_і_дає_відмову()
    {
        var element = new SourceElement("guid-1", "Stack1", new string('p', RegistryExternalKey.MaxExternalPathLength + 1), "addr");

        var (fit, rejection) = RegistrySyncElementLimits.Fit(element);

        Assert.NotNull(fit);
        Assert.Null(fit!.Path);
        Assert.Equal(("guid-1", "addr"), (fit.ExternalId, fit.ReadAddress));
        Assert.Contains("error=externalPathTooLong", rejection, StringComparison.Ordinal);
        Assert.Contains("length=401", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void Задовгий_ідентифікатор_виключає_елемент_зі_знімка()
    {
        var element = new SourceElement(new string('g', RegistryExternalKey.MaxExternalIdLength + 1), "Stack1", @"\\AF\Stack1", "addr");

        var (fit, rejection) = RegistrySyncElementLimits.Fit(element);

        Assert.Null(fit);
        Assert.Contains("error=externalIdTooLong", rejection, StringComparison.Ordinal);
        Assert.Contains("length=201", rejection, StringComparison.Ordinal);
    }
}
