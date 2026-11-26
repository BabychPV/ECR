using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Scripting;
using NSubstitute;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>Спільні будівельні блоки тестів скрипта методології: одиниці, таблиці, заглушки сховищ.</summary>
internal static class ScriptTestKit
{
    public const int Kg = 1;
    public const int Tonne = 2;
    public const int CubicMetre = 3;
    public const int Gram = 4;

    /// <summary>Довідник: <c>kg</c> і <c>t</c> — маса (1), <c>m3</c> — об'єм (2), <c>g_per_s</c> — інша розмірність (3).</summary>
    public static UnitCatalogSnapshot Units() => new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
        {
            ["kg"] = new(Kg, "kg", 1),
            ["t"] = new(Tonne, "t", 1),
            ["m3"] = new(CubicMetre, "m3", 2),
            ["g_per_s"] = new(Gram, "g_per_s", 3),
        },
        new Dictionary<string, int>(StringComparer.Ordinal));

    public static LiveColumn Column(int id, string code, CellDataType type = CellDataType.Decimal, byte? month = null)
        => new(id, code, type, month is not null, month, null);

    public static LiveTable Table(
        string code, IEnumerable<LiveColumn> columns, int tableId = 100, string template = "TPL", int templateId = 1,
        params string[] aliases)
        => new(templateId, template, 10, tableId, code, [.. columns], aliases);

    public static ScriptCompilation Compile(string text, bool isLibrary = false, NumericMode mode = NumericMode.Strict)
        => ScriptCompiler.Compile(text, new ScriptCompileOptions(mode, isLibrary));

    public static T WithId<T>(T entity, int id)
        where T : Entity<int>
    {
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
        return entity;
    }

    public static Methodology NewMethodology(string code, int id, MethodologyKind kind = MethodologyKind.DataDriven)
    {
        var methodology = WithId(new Methodology(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code })), id);
        methodology.SetKind(kind);
        return methodology;
    }

    public static MethodologyVersion NewVersion(
        Methodology methodology, int id, MethodologyEngine engine = MethodologyEngine.Script, string number = "1.0",
        CalculationLevel level = CalculationLevel.Configuration)
    {
        var version = WithId(
            new MethodologyVersion(
                methodology.Id, number, level, 1, new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), engine),
            id);
        methodology.AddVersion(version);
        return version;
    }

    /// <summary>Сховище методологій із живими таблицями й порожніми читаннями.</summary>
    public static IMethodologyStore Store(params LiveTable[] tables)
    {
        var store = Substitute.For<IMethodologyStore>();
        store.FindLiveTablesByCodeAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var code = call.ArgAt<string>(0);
                var template = call.ArgAt<string?>(1);
                IReadOnlyList<LiveTable> found =
                [
                    .. tables.Where(t => string.Equals(t.TableCode, code, StringComparison.Ordinal)
                                         && (template is null || string.Equals(t.TemplateCode, template, StringComparison.Ordinal))),
                ];
                return Task.FromResult(found);
            });
        store.FindSubstanceCandidatesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SubstanceCandidate>>([]));
        store.GetSubstanceCodesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>()));
        store.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ColumnResultBinding>>([]));
        store.GetMethodologyDependenciesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(new Dictionary<int, IReadOnlyList<int>>()));
        return store;
    }

    public static IUnitCatalog Catalog()
    {
        var catalog = Substitute.For<IUnitCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(Units());
        return catalog;
    }

    public static IRegistryStore EmptyRegistries()
    {
        var registries = Substitute.For<IRegistryStore>();
        registries.FindDefinitionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RegistryDef?>(null));
        return registries;
    }

    public static ScriptEnvironmentChecker Checker(
        IMethodologyStore store, IMethodologyDraftStore? drafts = null, IRegistryStore? registries = null)
        => new(store, drafts ?? Substitute.For<IMethodologyDraftStore>(), Catalog(), registries ?? EmptyRegistries());
}
