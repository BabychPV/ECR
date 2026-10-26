// tests/Ecr.Application.Tests/Registries/RegistryEntriesScopedViewTests.cs
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
/// P3 (рішення людини 26.10): пікер <c>GET registries/{code}/entries</c> приймає <c>Registry.View</c> ролі, призначеної на
/// проєкт документа (<see cref="ScopedProjectAccess.RegistryView"/>), коли задано <c>documentId</c>; без <c>documentId</c> -
/// як було (глобальне право чи грант Read). Узгоджено з PATCH шапки/комірки (<c>LookupTargetMatcher</c>).
/// </summary>
/// <remarks>
/// Мутація: прибрати гілку scoped View в <c>GetRegistryEntriesHandler</c> - червоніє
/// <c>Scoped_View_на_проєкт_документа_пікер_відкривається</c>.
/// </remarks>
public sealed class RegistryEntriesScopedViewTests
{
    private const int UserId = 9;
    private const int RegistryId = 701;
    private const string Code = "SCOPED_PICK";
    private const long DocumentId = 801;
    private const int ProjectId = 10;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryEntryCache _cache = Substitute.For<IRegistryEntryCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();

    public RegistryEntriesScopedViewTests()
    {
        _user.UserId.Returns(UserId);
        var definition = new RegistryDef(
            EcrCode.Create(Code), new LocalizedText(new Dictionary<string, string> { ["en"] = Code }), isTemporal: false);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(definition, RegistryId);
        _registries.FindDefinitionAsync(Code, Arg.Any<CancellationToken>()).Returns(definition);
        _registries.FindDefinitionByIdAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(definition);
        _cache.GetOrAddAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RegistryEntry>());

        _access.DocumentProjectIdAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(ProjectId);
        _documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSummary(
                DocumentId, ProjectId, "FILE-1", DateTime.UtcNow, SheetCount: 1,
                new Dictionary<string, string>(StringComparer.Ordinal)));
        var project = new Project(
            EcrCode.Create("PRJ"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Project" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 1, PeriodKind.Monthly, 1, "Asia/Atyrau");
        typeof(Entity<int>).GetProperty("Id")!.SetValue(project, ProjectId);
        _periods.FindProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(project);
    }

    private void Profile(
        bool globalView = false, bool scopedView = false, int scopedProject = ProjectId, bool deny = false,
        bool readGrant = false, bool scopedDeny = false, bool scopedReadGrant = false, bool scopedPresent = false)
    {
        var grants = new Dictionary<string, GrantLevel> { [$"Project:{ProjectId}"] = GrantLevel.Read };
        if (readGrant)
        {
            grants[$"Registry:{RegistryId}"] = GrantLevel.Read;
        }

        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "scoped-pick", UserId = UserId, SecurityStamp = "s",
            Permissions = new HashSet<string>(globalView ? ["Registry.View"] : []),
            Grants = grants,
            Denies = new HashSet<string>(deny ? [$"Registry:{RegistryId}"] : []),
            RoleIds = new HashSet<int>(),
            Scoped = scopedView || scopedDeny || scopedReadGrant || scopedPresent
                ? new Dictionary<int, ScopedProjectAccess>
                {
                    [scopedProject] = new(
                        new Dictionary<string, GrantLevel>(), new HashSet<string>(), new HashSet<int>(), new HashSet<string>())
                    {
                        RegistryView = scopedView,
                        RegistryDenies = scopedDeny ? new HashSet<int> { RegistryId } : new HashSet<int>(),
                        RegistryGrants = scopedReadGrant
                            ? new Dictionary<int, GrantLevel> { [RegistryId] = GrantLevel.Read }
                            : new Dictionary<int, GrantLevel>(),
                    },
                }
                : new Dictionary<int, ScopedProjectAccess>(),
        });
    }

    private GetRegistryEntriesHandler Handler()
        => new(_registries, new RegistryResolver(), _cache, _access, _user, _documents, _periods);

    private Task<IReadOnlyList<Ecr.Application.Registries.Dto.RegistryEntryDto>> Pick(long? documentId)
        => Handler().HandleAsync(Code, new DateOnly(2026, 9, 30), null, documentId, default);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "REG-VIEW-PICKER")]
    public async Task Scoped_View_на_проєкт_документа_пікер_відкривається()
    {
        Profile(scopedView: true);

        await Pick(DocumentId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "REG-VIEW-PICKER")]
    public async Task Scoped_View_без_documentId_пікер_закритий_як_було()
    {
        Profile(scopedView: true);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Pick(null));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "REG-VIEW-PICKER")]
    public async Task Scoped_View_лише_на_інший_проєкт_пікер_закритий_404_як_неіснуючий()
    {
        // P52: профіль з областю без View у проєкті документа відповідає 404, як неіснуючий реєстр (не 403).
        Profile(scopedView: true, scopedProject: ProjectId + 1);

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => Pick(DocumentId));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "REG-VIEW-PICKER")]
    public async Task Без_View_пікер_закритий_і_з_documentId()
    {
        Profile();

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Pick(DocumentId));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "REG-VIEW-PICKER")]
    public async Task Scoped_View_і_Deny_на_довідник_пікер_404_як_неіснуючий()
    {
        Profile(scopedView: true, deny: true);

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => Pick(DocumentId));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "REG-VIEW-PICKER")]
    public async Task Глобальний_View_пікер_як_раніше_без_запиту_проєкту_документа()
    {
        Profile(globalView: true);

        await Pick(DocumentId);

        await _access.DidNotReceiveWithAnyArgs().DocumentProjectIdAsync(default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "REG-VIEW-PICKER")]
    public async Task Грант_Read_на_довідник_пікер_як_раніше()
    {
        Profile(readGrant: true);

        await Pick(DocumentId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "P50")]
    public async Task Scoped_View_і_власний_Deny_ролі_пікер_404_як_неіснуючий()
    {
        Profile(scopedView: true, scopedDeny: true);

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => Pick(DocumentId));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "P50")]
    public async Task Глобальний_View_і_scoped_Deny_у_проєкті_документа_пікер_404()
    {
        Profile(globalView: true, scopedDeny: true);

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => Pick(DocumentId));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "P50")]
    public async Task Scoped_Deny_іншого_проєкту_глобальному_View_не_заважає()
    {
        Profile(globalView: true, scopedDeny: true, scopedProject: ProjectId + 1);

        await Pick(DocumentId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "P50")]
    public async Task Scoped_Read_грант_на_довідник_без_View_пікер_відкривається()
    {
        Profile(scopedReadGrant: true);

        await Pick(DocumentId);
    }

    // ── P52: вирівнювання шляху відмови пікера (кількість викликів, не таймінг) і однакова відповідь без View ──────────

    private int ProjectLookups() => _access.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAccessDecisionService.DocumentProjectIdAsync));

    private int DefinitionLookups() => _registries.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IRegistryStore.FindDefinitionAsync));

    /// <summary>
    /// Deny (scoped) і неіснуючий реєстр - той самий шлях: однакова кількість запитів проєкту документа й довідника,
    /// інакше різниця часу розкриває існування реєстру. Мутація: повернути запит проєкту ПІСЛЯ FindDefinitionAsync.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "P52-ALIGN")]
    public async Task Deny_реєстр_і_неіснуючий_реєстр_мають_однакову_кількість_запитів(bool globalViewWithScopedDeny, bool scopedViewWithScopedDeny)
    {
        Profile(globalView: globalViewWithScopedDeny, scopedView: scopedViewWithScopedDeny, scopedDeny: true);

        await Assert.ThrowsAsync<NotFoundException>(() => Pick(DocumentId));
        var deniedProject = ProjectLookups();
        var deniedDefinition = DefinitionLookups();

        _access.ClearReceivedCalls();
        _registries.ClearReceivedCalls();
        await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync("NO_SUCH_REG", new DateOnly(2026, 9, 30), null, DocumentId, default));

        Assert.Equal(deniedProject, ProjectLookups());
        Assert.Equal(deniedDefinition, DefinitionLookups());
    }

    [Theory]
    [InlineData("scopedElsewhere")]
    [InlineData("scopedNoView")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "P52-ORACLE")]
    public async Task Без_View_зі_scoped_областю_існуючий_і_неіснуючий_реєстр_дають_однакову_404(string kind)
    {
        Profile(scopedPresent: true, scopedProject: kind == "scopedElsewhere" ? ProjectId + 1 : ProjectId);

        var existing = await Assert.ThrowsAsync<NotFoundException>(() => Pick(DocumentId));
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync("NO_SUCH_REG", new DateOnly(2026, 9, 30), null, DocumentId, default));

        Assert.Equal("ECR-REG-0404", existing.ErrorCode);
        Assert.Equal(missing.ErrorCode, existing.ErrorCode);
        Assert.Equal(missing.Details!["messageKey"], existing.Details!["messageKey"]);
    }}
