// tests/Ecr.Application.Tests/Documents/CreateDocumentHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Людське ім'я документа (директива "людське ім'я документа"): опційний
/// <c>Name</c> у <c>CreateDocumentRequest</c> лягає в
/// <c>Document.NameL10n</c>, а <c>BusinessKey</c> лишається технічним ключем
/// незалежно від нього.
/// </summary>
public sealed class CreateDocumentHandlerTests
{
    private const int TemplateVersionId = 2;
    private const int SheetId = 5;
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public CreateDocumentHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 9 }
                .Permission(CreateDocumentHandler.Permission)
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
                .Build());

        var sheet = new SheetDef(
            TemplateVersionId, EcrCode.Create("SHEET"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        SetId(sheet, SheetId);

        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersionId, 0, [sheet],
                new Dictionary<int, ColumnDef>(),
                new Dictionary<(int, string), RowDef>()));

        _documents.ValidateCompositionAsync(
                TemplateVersionId, Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<CompositionViolation>());

        _documents.NextBusinessKeyAsync(
                AccessBuilder.ProjectId, TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns("P10-V2-0001");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Задане_імя_записується_в_NameL10n()
    {
        await Create(new Dictionary<string, string> { ["en"] = "Water intake report" });

        var document = AddedDocument();

        Assert.NotNull(document.NameL10n);
        Assert.Equal("Water intake report", document.NameL10n!.Get("en"));

        // ⛔ BusinessKey — технічний ключ, і задане ім'я не має жодного
        // способу підмінити його механізм.
        Assert.Equal("P10-V2-0001", document.BusinessKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Без_імені_NameL10n_лишається_null_як_до_цього_поля()
    {
        await Create(name: null);

        var document = AddedDocument();

        Assert.Null(document.NameL10n);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Порожній_обєкт_імені_трактується_як_відсутність_імені()
    {
        // ⚠ `{}` — це не «ім'я порожньою мовою», а той самий випадок, що й
        // відсутній `name` узагалі: другого способу сказати «немає імені» тут
        // бути не повинно.
        await Create(new Dictionary<string, string>());

        var document = AddedDocument();

        Assert.Null(document.NameL10n);
    }

    private async Task<long> Create(IReadOnlyDictionary<string, string>? name)
        => await new CreateDocumentHandler(_metadata, _documents, _uow, _access, _user, _clock)
            .HandleAsync(AccessBuilder.ProjectId, TemplateVersionId, [SheetId], name, CancellationToken.None);

    private Document AddedDocument()
    {
        var call = _documents.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IDocumentStore.AddAsync));

        return (Document)call.GetArguments()[0]!;
    }

    /// <summary>Виставляє приватний <c>Id</c> сутності через рефлексію — те саме,
    /// що вже роблять сусідні тести цього шару (`RequiredByMethodologyColumnTests`).</summary>
    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
