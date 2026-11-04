// tests/Ecr.Application.Tests/Permits/PermitListPreviewTests.cs
using Ecr.Application.Common;
using Ecr.Application.Permits.Lists;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// Перегляд перших значень у переліку списків (<c>PermitListDto.Preview</c>): форма, порожній для таблиці того самого дозволу,
/// ОДИН запит перегляду на всі реєстри. Порядок, відбір активних і «перші 4» виконує сховище - їх доводить
/// <c>PermitListsHttpTests</c> на справжній базі.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>PermitListCatalog.BuildAsync</c> замінити <c>[]</c> для <c>SamePermitTable</c> на <c>null</c> - червоний
/// «Таблиця_того_самого_дозволу…»; викликати <c>ListTopActiveEntriesAsync</c> по реєстру в циклі - червоний «Один_запит…».
/// </remarks>
public sealed class PermitListPreviewTests
{
    private static readonly int[] ExpectedIds = [10, 20, 30];
    private readonly IPermitListStore _lists = Substitute.For<IPermitListStore>();

    [Fact]
    public async Task Один_запит_перегляду_на_всі_реєстри_і_перегляд_у_порядку_сховища()
    {
        _lists.ListUsagesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Usage(1, PermitListScope.SharedRegistry, 10, "SHARED"),
            Usage(2, PermitListScope.SharedRegistry, 10, "SHARED"),
            Usage(3, PermitListScope.TypeOnly, 20, "PRM_A"),
            Usage(4, PermitListScope.TypeOnly, 30, "PRM_B"),
        ]);
        _lists.CountActiveEntriesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [10] = 2, [20] = 1 });
        _lists.ListTopActiveEntriesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, IReadOnlyList<RegistryEntry>>
            {
                [10] = [Entry(10, "A"), Entry(10, "B")],
                [20] = [Entry(20, "C")],
            });

        var result = await Handler().HandleAsync(CancellationToken.None);

        var shared = Assert.Single(result, l => l.Scope == "SharedRegistry");
        Assert.Equal(["A", "B"], shared.Preview!.Select(v => v.Code));
        Assert.Equal("a", shared.Preview![0].DisplayL10n["en"]);
        Assert.Equal(["C"], result.Single(l => l.RegistryCode == "PRM_A").Preview!.Select(v => v.Code));
        Assert.Empty(result.Single(l => l.RegistryCode == "PRM_B").Preview!);

        await _lists.Received(1).ListTopActiveEntriesAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Order().SequenceEqual(ExpectedIds)), 4, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Таблиця_того_самого_дозволу_має_порожній_перегляд_і_реєстру_не_торкається()
    {
        _lists.ListUsagesAsync(Arg.Any<CancellationToken>()).Returns([Usage(5, PermitListScope.SamePermitTable, null, null)]);
        _lists.CountActiveEntriesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, int>());
        _lists.ListTopActiveEntriesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, IReadOnlyList<RegistryEntry>>());

        var list = Assert.Single(await Handler().HandleAsync(CancellationToken.None));

        Assert.NotNull(list.Preview);
        Assert.Empty(list.Preview);
        await _lists.Received(1).ListTopActiveEntriesAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0), 4, Arg.Any<CancellationToken>());
    }

    private ListPermitListsHandler Handler()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.View").Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        return new ListPermitListsHandler(_lists, access, user);
    }

    private static RegistryEntry Entry(int registryDefId, string code)
        => new(registryDefId, EcrCode.Create(code), Text(code.ToLowerInvariant()));

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });

    private static PermitListUsage Usage(int typeId, PermitListScope scope, int? registryDefId, string? registryCode)
        => new(
            typeId, $"T{typeId}", Text($"Type {typeId}"), 100 + typeId, "Rows", Text("Rows"), $"F{typeId}", Text($"Field {typeId}"),
            scope, registryDefId, registryCode, registryCode is null ? null : Text(registryCode),
            scope == PermitListScope.SamePermitTable ? "Places" : null);
}
