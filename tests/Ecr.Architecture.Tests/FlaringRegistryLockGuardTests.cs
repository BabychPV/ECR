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

    /// <summary>
    /// ✎ SGF-flaring AC-5 (огляд b13, F1-5): прив'язка й відв'язка зовнішнього ключа запису — теж писачі запису довідника, але
    /// <c>EnsureManualEditAllowed</c> не кличуть (зв'язок, а не дані AF), тож загальне сито їх не бачить. Окреме правило: обидва
    /// кличуть запобіжник факелів.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "SGF-FLARING-AC-5")]
    public void Прив_язка_й_відв_язка_зовнішнього_ключа_кличуть_запобіжник_факелів()
    {
        var calls = Calls().ToList();

        Assert.Empty(ExternalKeyViolations(calls));

        // Мутаційний доказ правила: без виклику в одному з обробників — червоне.
        foreach (var writer in ExternalKeyWriters)
        {
            var without = calls.Where(c => !(c.Owner == writer && c.Kind == Kind.Flaring)).ToList();
            Assert.Contains(writer, ExternalKeyViolations(without));
        }
    }

    private static readonly string[] ExternalKeyWriters =
    [
        "Ecr.Application.Registries.BindRegistryExternalKeyHandler",
        "Ecr.Application.Registries.UnbindRegistryExternalKeyHandler",
    ];

    private static List<string> ExternalKeyViolations(IReadOnlyList<Call> calls)
        => [.. ExternalKeyWriters.Where(w => !calls.Any(c => c.Owner == w && c.Kind == Kind.Flaring))];

    private const string DefinitionHandler = "Ecr.Application.Registries.SaveRegistryDefinitionHandler";

    /// <summary>
    /// ✎ F1-1 (приймання b13): опис довідника застосовує ЄДИНИЙ метод <c>SaveRegistryDefinitionHandler.ApplyAsync</c>
    /// (збереження й публікація чернетки), і саме в ньому стоїть запобіжник факелів; внутрішнє застосування
    /// (<c>ApplyInTransactionAsync</c>) кличе лише він — обійти запобіжник іншим обробником не можна.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "SGF-F1-1")]
    public void Застосування_опису_довідника_кличе_запобіжник_і_не_обходиться()
    {
        var calls = DefinitionCalls().ToList();

        Assert.Contains(calls, c => c.Origin == "ApplyAsync" && c.Target == nameof(ExternalRegistryGuard.EnsureFlaringEditAllowedAsync));
        Assert.All(
            calls.Where(c => c.Target == "ApplyInTransactionAsync"),
            c => Assert.True(c.Owner == DefinitionHandler && c.Origin == "ApplyAsync", $"{c.Owner}.{c.Origin} застосовує опис повз ApplyAsync"));

        // Сито бачить обидва шляхи: збереження й публікацію.
        Assert.Contains(calls, c => c.Owner == DefinitionHandler && c.Target == "ApplyAsync");
        Assert.Contains(calls, c => c.Owner == "Ecr.Application.Registries.PublishRegistryDefinitionHandler" && c.Target == "ApplyAsync");
    }

    private sealed record DefinitionCall(string Owner, string Origin, string Target);

    private static IEnumerable<DefinitionCall> DefinitionCalls()
    {
        using var module = ModuleDefinition.ReadModule(typeof(ExternalRegistryGuard).Assembly.Location);
        foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            var owner = Owner(method.DeclaringType);
            var origin = OriginOf(method);
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt) || instruction.Operand is not MethodReference target)
                {
                    continue;
                }

                var type = target.DeclaringType.FullName;
                if ((type == DefinitionHandler && target.Name is "ApplyAsync" or "ApplyInTransactionAsync")
                    || (type == GuardType && target.Name == nameof(ExternalRegistryGuard.EnsureFlaringEditAllowedAsync)))
                {
                    yield return new DefinitionCall(owner, origin, target.Name);
                }
            }
        }
    }

    /// <summary>Метод джерела крізь машину станів (<c>&lt;X&gt;d__N</c>) і замикання (<c>&lt;X&gt;b__N</c>).</summary>
    private static string OriginOf(MethodDefinition method)
    {
        // `<ApplyAsync>d__5`, `<ApplyAsync>b__0`, `<<ApplyAsync>b__0>d` (async-лямбда) → ApplyAsync.
        static string? Angle(string name)
            => System.Text.RegularExpressions.Regex.Match(name, "^<+([^<>]+)>") is { Success: true } m ? m.Groups[1].Value : null;

        return Angle(method.Name) ?? Angle(method.DeclaringType.Name) ?? method.Name;
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
