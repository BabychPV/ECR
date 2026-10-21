// tests/Ecr.Application.Tests/Registries/RegistryEntriesDocumentWindowTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Піцкер шапки документа за реєстром шапок дозволів (<c>PRM_*</c>): <c>GET …/entries?documentId=</c> віддає ЛИШЕ записи,
/// які <c>PATCH …/header</c> прийняв би (<c>ECR-HDR-4223</c>): активні, не видалені, чинні бодай один день вікна проєкту.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>RegistryResolver.SelectCore</c> прибрати <c>.Where(e =&gt; e.IsActive &amp;&amp; !e.IsDeleted)</c>
/// - червоні тести про Draft і видалений; у <c>SelectInWindow</c> замінити <c>OverlapsSegment</c> на
/// <c>Contains(windowStart)</c> - червоний тест про запис, що починається всередині вікна.
/// </remarks>
public sealed class RegistryEntriesDocumentWindowTests
{
    private const int RegistryDefId = 100;
    private const int ProjectId = 7;
    private const long DocumentId = 55;
    private const string Code = "PRM_AIR";

    // Вікно проєкту: звітний рік 2026, обидві межі включно.
    private static readonly DateOnly WindowStart = new(2026, 1, 1);
    private static readonly DateOnly WindowEnd = new(2026, 12, 31);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryEntryCache _cache = Substitute.For<IRegistryEntryCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();

    public RegistryEntriesDocumentWindowTests()
    {
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        UseProfile(new AccessBuilder { UserId = 9 }
            .Permission(GetRegistryEntriesHandler.Permission)
            .Grant(ResourceKind.Project, ProjectId, GrantLevel.Read)
            .Build());

        _cache.GetOrAddAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                callInfo.ArgAt<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(1)(default));

        var definition = new RegistryDef(EcrCode.Create(Code), Text("Permit header"), isTemporal: true);
        SetId(definition, RegistryDefId);
        _registries.FindDefinitionAsync(Code, Arg.Any<CancellationToken>()).Returns(definition);

        _documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSummary(DocumentId, ProjectId, "BK", DateTime.UnixEpoch, 0, new Dictionary<string, string>()));

        var project = new Project(
            EcrCode.Create("P7"), Text("Project"), WindowStart, WindowEnd, templateVersionId: 1,
            PeriodKind.Monthly, periodPolicyId: 1, timeZoneId: "UTC");
        _periods.FindProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(project);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Пропонуються_лише_активні_записи_чинні_бодай_один_день_вікна()
    {
        Seed(
            Entry(1, "ACTIVE", null, null),
            Entry(2, "PARTIAL_START", new DateOnly(2026, 6, 1), null),
            Entry(3, "ENDS_IN_WINDOW", new DateOnly(2025, 1, 1), new DateOnly(2026, 2, 1)),
            Entry(4, "DRAFT", null, null, deactivate: true),
            Entry(5, "DELETED", null, null, delete: true),
            Entry(6, "EXPIRED_BEFORE", new DateOnly(2024, 1, 1), new DateOnly(2026, 1, 1)),
            Entry(7, "STARTS_AFTER", new DateOnly(2027, 1, 1), null));

        var shown = await ShownAsync();

        // 3 - ValidTo виключний, 2026-02-01 перетинає вікно; 6 - ValidTo = початок вікна, жодного спільного дня.
        Assert.Equal(["ACTIVE", "PARTIAL_START", "ENDS_IN_WINDOW"], shown);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Draft_і_видалений_дозвіл_ніколи_не_пропонуються()
    {
        Seed(Entry(1, "DRAFT", null, null, deactivate: true), Entry(2, "DELETED", null, null, delete: true));

        Assert.Empty(await ShownAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Запис_що_закінчується_рівно_в_першу_мить_вікна_і_що_починається_після_його_кінця_не_пропонуються()
    {
        Seed(
            Entry(1, "ENDS_AT_START", new DateOnly(2020, 1, 1), WindowStart),
            Entry(2, "STARTS_DAY_AFTER", WindowEnd.AddDays(1), null),
            Entry(3, "STARTS_LAST_DAY", WindowEnd, null));

        Assert.Equal(["STARTS_LAST_DAY"], await ShownAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_documentId_працює_колишнє_правило_asOf_і_вимагає_дату_для_темпорального()
    {
        Seed(Entry(1, "ACTIVE", null, null));

        var withoutDate = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Code, default, parentEntryId: null, CancellationToken.None));
        Assert.Equal(Domain.Errors.ErrorCodes.RequestInvalid, withoutDate.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Невидимий_чи_відсутній_документ_дає_404_а_не_порожній_перелік()
    {
        Seed(Entry(1, "ACTIVE", null, null));
        UseProfile(new AccessBuilder { UserId = 9 }.Permission(GetRegistryEntriesHandler.Permission).Build());

        var hidden = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Code, default, null, DocumentId, CancellationToken.None));
        Assert.Equal(Domain.Errors.ErrorCodes.DocumentNotFound, hidden.ErrorCode);

        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Code, default, null, DocumentId + 1, CancellationToken.None));
        Assert.Equal(Domain.Errors.ErrorCodes.DocumentNotFound, missing.ErrorCode);
    }

    private async Task<string[]> ShownAsync()
        => (await Handler().HandleAsync(Code, default, null, DocumentId, CancellationToken.None))
            .Select(e => e.Code).ToArray();

    private GetRegistryEntriesHandler Handler()
        => new(_registries, new RegistryResolver(), _cache, _access, _user, _documents, _periods);

    private void UseProfile(AccessProfile profile)
        => _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(profile);

    private void Seed(params RegistryEntry[] entries)
        => _registries.ListEntriesAsync(RegistryDefId, Arg.Any<CancellationToken>())
            .Returns(entries.ToList());

    private static RegistryEntry Entry(
        long id, string code, DateOnly? from, DateOnly? to, bool deactivate = false, bool delete = false)
    {
        var entry = new RegistryEntry(RegistryDefId, EcrCode.Create(code), Text(code));
        SetId(entry, id);
        entry.SetOrdinal((int)id);
        if (from is not null || to is not null)
        {
            entry.SetValidity(from, to);
        }

        if (deactivate)
        {
            entry.Deactivate();
        }

        if (delete)
        {
            entry.SoftDelete();
        }

        return entry;
    }

    private static void SetId<T>(Entity<T> entity, T id) where T : IEquatable<T>
        => typeof(Entity<T>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Text(string en)
        => new(new Dictionary<string, string> { ["en"] = en });
}
