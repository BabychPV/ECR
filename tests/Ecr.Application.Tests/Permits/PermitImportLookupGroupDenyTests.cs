// tests/Ecr.Application.Tests/Permits/PermitImportLookupGroupDenyTests.cs
using Ecr.Application.Common;
using Ecr.Application.Permits.Import;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// D-456: перевірка значень імпорту в таблицю дозволу для колонки, ціль якої — ГРУПА <c>PRMG_*</c>. Перевірка рівня реєстру
/// (#757: ціль читається) пропускає групу, а рівень записів — <c>LookupTargetEntries.ListAsync</c> із профілем — розгортає її в
/// учасників і пропускає учасника під Deny: його код відповідає <c>unknown</c>, як неіснуючий (без оракула існування).
/// </summary>
/// <remarks>
/// Мутації (прогнано, червоніють): <c>CandidatesAsync</c> → завжди <c>rows.ListEntriesAsync</c> — учасників не видно зовсім;
/// <c>ListAsync</c> без <c>profile</c> — код Deny-учасника знаходиться.
/// </remarks>
public sealed class PermitImportLookupGroupDenyTests
{
    private const int TableId = 50;
    private const int Group = 900;
    private const int TypeA = 10;
    private const int TypeDenied = 12;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryRowsQuery _rows = Substitute.For<IRegistryRowsQuery>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly FakePermitGroupResolver _groups = new FakePermitGroupResolver().WithGroup(Group, [TypeA, TypeDenied]);

    public PermitImportLookupGroupDenyTests()
    {
        var table = Definition(TableId, "PRM_T_WATER");
        var permit = Field(TableId, 501, "PERMIT", Group);
        permit.ComposeInto(ParentDeletePolicy.Restrict);
        table.AddField(permit);
        table.AddField(Field(TableId, 502, "WATER", Group));

        Register(table);
        Register(Definition(Group, "PRMG_WATER"));
        Register(Definition(TypeA, "PRM_WATER_A"));
        Register(Definition(TypeDenied, "PRM_WATER_C"));

        _registries.ListEntriesAsync(Group, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([]));
        _registries.ListEntriesAsync(TypeA, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([Entry(TypeA, 2001, "N200")]));
        _registries.ListEntriesAsync(TypeDenied, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([Entry(TypeDenied, 2201, "N300")]));
        _rows.ListEntriesAsync(Arg.Any<int>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RegistryEntry>>([]));

        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string> { "Registry:" + TypeDenied }, RoleIds = new HashSet<int>(),
        });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Учасник_групи_під_Deny_відповідає_unknown_доступний_знаходиться()
    {
        var handler = new PermitImportLookupHandler(
            _registries, _rows, Substitute.For<IRegistryHierarchyStore>(), _access, _user, groups: _groups);

        var result = await handler.HandleAsync(
            "PRM_T_WATER",
            new PermitImportLookupRequest([new PermitImportLookupColumn("WATER", ["N200", "N300", "N999"])], Derive: false),
            CancellationToken.None);

        var values = Assert.Single(result.Columns).Values.ToDictionary(v => v.Text);
        Assert.Equal(2001, values["N200"].EntryId);
        Assert.Null(values["N300"].EntryId);
        Assert.Equal(PermitImportReasons.Unknown, values["N300"].Reason);
        // Відповідь на код Deny-учасника нерозрізнювана з неіснуючим кодом.
        Assert.Equal(values["N999"] with { Text = "N300" }, values["N300"]);
    }

    private void Register(RegistryDef definition)
    {
        _registries.FindDefinitionAsync(definition.Code, Arg.Any<CancellationToken>()).Returns(definition);
        _registries.FindDefinitionByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
    }

    private static RegistryDef Definition(int id, string code)
    {
        var definition = new RegistryDef(EcrCode.Create(code), Names(code), isTemporal: true);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, id);
        return definition;
    }

    private static RegistryFieldDef Field(int registryId, int id, string code, int target)
    {
        var field = new RegistryFieldDef(registryId, EcrCode.Create(code), Names(code), CellDataType.Lookup, id);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(field, id);
        field.PointTo(target);
        return field;
    }

    private static RegistryEntry Entry(int registryId, long id, string code)
    {
        var entry = new RegistryEntry(registryId, EcrCode.Create(code), Names(code));
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Names(string text) => new(new Dictionary<string, string> { ["en"] = text });
}
