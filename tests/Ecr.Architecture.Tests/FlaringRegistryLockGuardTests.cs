// tests/Ecr.Architecture.Tests/FlaringRegistryLockGuardTests.cs
using Ecr.Application.Registries;
using Ecr.TestKit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ✎ DESIGN-sg-flaring §5.7 (C4(b), D6): кожен ЗАГАЛЬНИЙ писач записів довідника кличе запобіжник факелів
/// <see cref="ExternalRegistryGuard.EnsureFlaringEditAllowedAsync"/> — інакше власник <c>Registry.EditData</c> змінив би
/// <c>LP_SOUR_SPLIT</c>/<c>TOX_GAS_STREAM</c>/<c>FLARE_LOCATION</c> у закритому місяці повз <c>GasComposition.Edit</c>.
/// </summary>
/// <remarks>
/// ⚠ D6: лише загальні писачі довідників (простори <c>Ecr.Application.Registries</c>, <c>.Rows</c>, <c>.Hierarchy</c>), а
/// не дозволи/НАЕ (<c>Permits.*</c>, <c>Registries.Nae.*</c>): ті пишуть інші довідники, і запобіжник там нічого не
/// захищає. IL, а не текст: виклик сидить у машині станів (згортається до типу верхнього рівня).
/// </remarks>
public sealed class FlaringRegistryLockGuardTests
{
    private const string GuardType = "Ecr.Application.Registries.ExternalRegistryGuard";

    /// <summary>Загальні писачі записів довідника (C4(b) рецензії): upsert, пакет, CSV, видалення, вікно, ієрархія, відновлення/очищення.</summary>
    private static readonly string[] GenericWriters =
    [
        "Ecr.Application.Registries.UpsertRegistryEntryHandler",
        "Ecr.Application.Registries.Rows.RegistryBatchHandler",
        "Ecr.Application.Registries.ImportRegistryEntriesHandler",
        "Ecr.Application.Registries.SetEntryValidityHandler",
        "Ecr.Application.Registries.DeleteRegistryEntryHandler",
        "Ecr.Application.Registries.Hierarchy.MoveRegistryEntryHandler",
        "Ecr.Application.Registries.Hierarchy.RetireRegistryEntryHandler",
        "Ecr.Application.Registries.Hierarchy.UnretireRegistryEntryHandler",
        "Ecr.Application.Registries.Hierarchy.PurgeRegistryEntryHandler",
    ];

    /// <summary>Простори загальних писачів (D6); НАЕ — свій писач свого довідника.</summary>
    private static readonly string[] GenericNamespaces =
    [
        "Ecr.Application.Registries",
        "Ecr.Application.Registries.Rows",
        "Ecr.Application.Registries.Hierarchy",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "SGF-C4")]
    public void Кожен_загальний_писач_кличе_запобіжник_факелів()
    {
        var violations = Violations(Calls().ToList());

        Assert.True(
            violations.Count == 0,
            "Загальний писач записів довідника кличе EnsureManualEditAllowed, але не EnsureFlaringEditAllowedAsync "
            + "(DESIGN-sg-flaring §5.7, C4/D6): " + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Сито бачить усі дев'ять писачів — зламаний пошук дав би зелений тест.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "SGF-C4")]
    public void Сито_бачить_усіх_загальних_писачів()
    {
        var calls = Calls().ToList();
        foreach (var writer in GenericWriters)
        {
            Assert.Contains(calls, c => c.Owner == writer && c.Kind == Kind.Manual);
            Assert.Contains(calls, c => c.Owner == writer && c.Kind == Kind.Flaring);
        }
    }

    /// <summary>D6: дозволи й НАЕ запобіжника не кличуть і не мусять — правило їх не зачіпає.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "SGF-D6")]
    public void Дозволи_й_НАЕ_поза_правилом()
    {
        var calls = Calls().ToList();
        Assert.Contains(calls, c => c.Owner.StartsWith("Ecr.Application.Permits.", StringComparison.Ordinal) && c.Kind == Kind.Manual);
        Assert.DoesNotContain(Violations(calls), v => v.Contains(".Permits.", StringComparison.Ordinal) || v.Contains(".Nae.", StringComparison.Ordinal));
    }

    /// <summary>Мутаційний доказ правила: ті самі дані без виклику запобіжника в одному писачі — червоні.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "SGF-C4")]
    public void Правило_червоніє_без_запобіжника_в_писачі()
    {
        foreach (var writer in GenericWriters)
        {
            var without = Calls().Where(c => !(c.Owner == writer && c.Kind == Kind.Flaring)).ToList();
            Assert.Contains(Violations(without), v => v.Contains(writer, StringComparison.Ordinal));
        }
    }

    private static List<string> Violations(IReadOnlyList<Call> calls)
    {
        var flaring = calls.Where(c => c.Kind == Kind.Flaring).Select(c => c.Owner).ToHashSet(StringComparer.Ordinal);
        return [.. calls
            .Where(c => c.Kind == Kind.Manual && IsGeneric(c.Owner) && !flaring.Contains(c.Owner))
            .Select(c => "  " + c.Owner)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    private static bool IsGeneric(string owner)
    {
        var dot = owner.LastIndexOf('.');
        return dot > 0 && GenericNamespaces.Contains(owner[..dot], StringComparer.Ordinal);
    }

    private enum Kind
    {
        Manual,
        Flaring,
    }

    private sealed record Call(string Owner, Kind Kind);

    private static IEnumerable<Call> Calls()
    {
        using var module = ModuleDefinition.ReadModule(typeof(ExternalRegistryGuard).Assembly.Location);
        foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            var owner = Owner(method.DeclaringType);
            if (owner == GuardType)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt)
                    || instruction.Operand is not MethodReference target
                    || target.DeclaringType.FullName != GuardType)
                {
                    continue;
                }

                if (target.Name == nameof(ExternalRegistryGuard.EnsureManualEditAllowed))
                {
                    yield return new Call(owner, Kind.Manual);
                }
                else if (target.Name == nameof(ExternalRegistryGuard.EnsureFlaringEditAllowedAsync))
                {
                    yield return new Call(owner, Kind.Flaring);
                }
            }
        }
    }

    private static string Owner(TypeDefinition type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type.FullName;
    }
}
