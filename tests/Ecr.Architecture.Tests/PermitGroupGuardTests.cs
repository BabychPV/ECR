using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сторожі груп типів дозволів (Permits, PRMG): прихований реєстр <c>PRMG_&lt;КОД&gt;</c> керується лише групою, а порт
/// <c>IPermitGroupResolver</c>/<c>ILookupTargetResolver</c> кличеться тільки після <c>EnsureFreshAsync</c> і має одну реалізацію.
/// </summary>
/// <remarks>
/// ⚠ Сторожі по тексту джерел крихкі (падають на переносі рядка) - тому кожен спирається на ім'я типу чи методу, а не на форму
/// виразу. Мутації: (1) прибрати <c>RequireNotManaged</c> з писача записів - червоніє «Шляхи_запису_довідників…»; (2) додати літерал
/// <c>"PRMG_"</c> у будь-який інший файл - «Префікс_PRMG…»; (3) прибрати <c>EnsureFreshAsync</c> з піклера групи - «Споживач_порту…»;
/// (4) додати другий клас, що реалізує порт, - «Порт_резолвера…».
/// </remarks>
public sealed partial class PermitGroupGuardTests
{
    /// <summary>Файли, яким дозволено знати префікс <c>PRMG_</c>: усе інше йде через <c>PermitGroupRegistryGuard</c>.</summary>
    private static readonly string[] PrefixOwners =
    [
        "src/Ecr.Application/Permits/Groups/PermitGroupErrors.cs",
        "src/Ecr.Application/Permits/Groups/PermitGroupRegistryGuard.cs",
        "src/Ecr.Infrastructure/Persistence/UnitOfWork.cs",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Префікс_PRMG_живе_лише_у_файлах_власниках()
    {
        var offenders = SourceTree.Production()
            .Where(f => !PrefixOwners.Contains(f.Path))
            .Where(f => PrefixLiteral().IsMatch(WithoutLineComments(f.Text)))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Шляхи_запису_довідників_викликають_сторож_прихованого_реєстру_групи()
    {
        var expectations = new Dictionary<string, (string Needle, int Min)>
        {
            ["src/Ecr.Application/Registries/RegistryEntryWriter.cs"] = ("PermitGroupRegistryGuard.RequireNotManaged", 1),
            ["src/Ecr.Application/Registries/RegistryDefinitionHandlers.cs"] = ("PermitGroupRegistryGuard.RequireNotManaged", 1),
            ["src/Ecr.Application/Registries/RegistryDefinitionDraftHandlers.cs"] = ("PermitGroupRegistryGuard.RequireNotManaged", 3),
            ["src/Ecr.Application/Registries/RegistryAdminHandlers.cs"] = ("PermitGroupRegistryGuard.RequireNotManaged", 1),
            ["src/Ecr.Application/Registries/CreateRegistryHandler.cs"] = ("PermitGroupRegistryGuard.RequireNotReserved", 1),
            ["src/Ecr.Application/Calculations/MethodologyAuthoringHandlers.cs"] = ("PermitGroupRegistryGuard.RequireNotRowActivityTarget", 1),
            ["src/Ecr.Application/Templates/PublishTemplateVersionHandler.cs"] = ("PublishChecks.CheckGroupLookupFormulas", 1),
        };

        var files = SourceTree.Production("Ecr.Application").ToDictionary(f => f.Path);
        var failures = expectations
            .Where(e => !files.TryGetValue(e.Key, out var file)
                        || Regex.Count(WithoutLineComments(file.Text), Regex.Escape(e.Value.Needle)) < e.Value.Min)
            .Select(e => $"{e.Key}: потрібно ≥ {e.Value.Min} викликів {e.Value.Needle}")
            .ToList();

        Assert.Empty(failures);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Споживач_порту_груп_кличе_EnsureFreshAsync_до_читання_знімка()
    {
        var consumers = SourceTree.Production()
            .Where(f => !f.Path.StartsWith("src/Ecr.Application/Permits/Groups/", StringComparison.Ordinal))
            .Where(f => !f.Path.EndsWith("/DependencyInjection.cs", StringComparison.Ordinal))
            .Where(f => ResolverMention().IsMatch(WithoutLineComments(f.Text)))
            .ToList();

        // Порт має споживачів: сторож не повинен зеленіти на порожньому наборі.
        Assert.NotEmpty(consumers);

        var offenders = consumers
            .Where(f => !WithoutLineComments(f.Text).Contains("EnsureFreshAsync(", StringComparison.Ordinal))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Порт_резолвера_груп_має_одну_реалізацію()
    {
        var implementations = SourceTree.Production()
            .Where(f => ImplementsPort().IsMatch(WithoutLineComments(f.Text)))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["src/Ecr.Application/Permits/Groups/PermitGroupResolver.cs"], implementations);
    }

    private static string WithoutLineComments(string text)
        => LineComment().Replace(text, string.Empty);

    [GeneratedRegex(@"""PRMG_")]
    private static partial Regex PrefixLiteral();

    [GeneratedRegex(@"\b(IPermitGroupResolver|ILookupTargetResolver)\b")]
    private static partial Regex ResolverMention();

    [GeneratedRegex(@"class\s+\w+[^{;=]*:\s*[^{;]*\b(IPermitGroupResolver|ILookupTargetResolver)\b")]
    private static partial Regex ImplementsPort();

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();
}
