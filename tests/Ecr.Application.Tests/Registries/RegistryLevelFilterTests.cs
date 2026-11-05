// tests/Ecr.Application.Tests/Registries/RegistryLevelFilterTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// HSE401: фільтр Lookup-колонки <c>level=&lt;CODE&gt;</c> - формат, валідація при збереженні й звуження переліку записів
/// ієрархічного довідника (<c>GET registries/{code}/entries?level=</c>).
/// </summary>
public sealed class RegistryLevelFilterTests
{
    private const int RegistryDefId = 100;
    private const string Code = "NAE_TREE";

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryHierarchyStore _hierarchy = Substitute.For<IRegistryHierarchyStore>();
    private readonly IRegistryEntryCache _cache = Substitute.For<IRegistryEntryCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public RegistryLevelFilterTests()
    {
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 9 }.Permission(GetRegistryEntriesHandler.Permission).Build());
        _cache.GetOrAddAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(1)(default));

        var definition = new RegistryDef(EcrCode.Create(Code), Text("Tree"), false);
        definition.UseHierarchy(true);
        SetId(definition, RegistryDefId);
        _registries.FindDefinitionAsync(Code, Arg.Any<CancellationToken>()).Returns(definition);

        var area = new RegistryLevelDef(RegistryDefId, 1, EcrCode.Create("AREA"), Text("Area"), false);
        var site = new RegistryLevelDef(RegistryDefId, 2, EcrCode.Create("SITE"), Text("Site"), false);
        SetId(area, 1);
        SetId(site, 2);
        _hierarchy.ListLevelsAsync(RegistryDefId, Arg.Any<CancellationToken>())
            .Returns(new List<RegistryLevelDef> { area, site });

        _registries.ListEntriesAsync(RegistryDefId, Arg.Any<CancellationToken>()).Returns(new List<RegistryEntry>
        {
            Entry(1, "A1", levelId: 1),
            Entry(2, "S1", levelId: 2),
            Entry(3, "S2", levelId: 2),
            Entry(4, "NOLEVEL", levelId: null),
        });
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("  ", true, null)]
    [InlineData("level=SITE", true, "SITE")]
    [InlineData(" level=PLANT_1 ", true, "PLANT_1")]
    [InlineData("kind=gas", false, null)]
    [InlineData("level=", false, null)]
    [InlineData("level=A B", false, null)]
    [InlineData("level=A;level=B", false, null)]
    [InlineData("SITE", false, null)]
    public void Формат_фільтра_довідника_це_лише_level_CODE(string? filter, bool valid, string? level)
    {
        Assert.Equal(valid, LookupFilterSpec.TryParse(filter, out var parsed));
        Assert.Equal(level, parsed);
    }

    [Fact]
    public void Збереження_колонки_з_невідомим_ключем_фільтра_відмовляється_422_з_ключем()
    {
        var column = NewLookupColumn();

        var error = Assert.Throws<DomainException>(() => column.SetLookup(5, "kind=gas"));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.lookupFilterUnsupported", error.Details!["messageKey"]);
        Assert.Null(column.LookupFilter);

        column.SetLookup(5, " level=SITE ");
        Assert.Equal("level=SITE", column.LookupFilter);

        column.SetLookup(5, "  ");
        Assert.Null(column.LookupFilter);
    }

    [Fact]
    public async Task Перелік_без_level_віддає_усі_записи_з_кодом_рівня()
    {
        var result = await HandleAsync(level: null);

        Assert.Equal(["A1", "NOLEVEL", "S1", "S2"], result.Select(e => e.Code));
        Assert.Equal(["AREA", null, "SITE", "SITE"], result.Select(e => e.LevelCode));
    }

    [Fact]
    public async Task Перелік_з_level_віддає_лише_записи_цього_рівня_без_урахування_регістру()
    {
        Assert.Equal(["S1", "S2"], (await HandleAsync("SITE")).Select(e => e.Code));
        Assert.Equal(["S1", "S2"], (await HandleAsync("site")).Select(e => e.Code));
        Assert.Equal(["A1"], (await HandleAsync("AREA")).Select(e => e.Code));
    }

    [Fact]
    public async Task Невідомий_рівень_дає_порожній_перелік_а_не_увесь_довідник()
    {
        Assert.Empty(await HandleAsync("NOPE"));
    }

    private Task<IReadOnlyList<Ecr.Application.Registries.Dto.RegistryEntryDto>> HandleAsync(string? level)
        => new GetRegistryEntriesHandler(
                _registries, new RegistryResolver(), _cache, _access, _user, hierarchy: _hierarchy)
            .HandleAsync(Code, default, parentEntryId: null, documentId: null, level, default);

    private static ColumnDef NewLookupColumn()
        => new(tableDefId: 3, EcrCode.Create("Site"), Text("Site"), 1, CellDataType.Lookup);

    private static RegistryEntry Entry(long id, string code, int? levelId)
    {
        var entry = new RegistryEntry(RegistryDefId, EcrCode.Create(code), Text(code));
        SetId(entry, id);
        entry.SetLevel(levelId);
        return entry;
    }

    private static void SetId<T>(Entity<T> entity, T id) where T : IEquatable<T>
        => typeof(Entity<T>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Text(string en)
        => new(new Dictionary<string, string> { ["en"] = en });
}
