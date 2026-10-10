// tests/Ecr.Application.Tests/Documents/CreateDocumentHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
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
    private readonly ITemplateVersionStore _templates = Substitute.For<ITemplateVersionStore>();
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

        // ⛔ `V-11`: версію документа визначає ПРОЄКТ — обробник питає її тут.
        _documents.FindProjectTemplateVersionIdAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>())
            .Returns(TemplateVersionId);

        _documents.NextBusinessKeyAsync(
                AccessBuilder.ProjectId, TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns("P10-V2-0001");

        // ⛔ L6-02 / N1-04: документ пишеться в транзакції під блоком рядка проєкту. Тут — прохідна
        // транзакція й блок, що показує версію проєкту; саму гонку з переносом тримають
        // `CreateDocumentStructureLockTests` (Infrastructure.Tests) на справжній базі.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _documents.LockProjectTemplateVersionAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>())
            .Returns(TemplateVersionId);

        // ⚠ Шаблон версії В ОБІГУ: із `BE-26` створення документа питає про
        // нього, бо архівований шаблон для нових документів не пропонується
        // (`Template.EnsureOfferedForNewDocuments`). Доказ самого правила — на
        // живому HTTP (`Ecr.Api.Tests/TemplateCardTests`); тут шаблон потрібен
        // рівно для того, щоб перевірятися було чому.
        _templates.FindTemplateOfVersionAsync(TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(new Template(
                EcrCode.Create("TPL"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Template" }),
                createdByUserId: 9,
                Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-3.1")]
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Архівований_шаблон_не_пропонується_для_нового_документа()
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ №2 (частина друга). Прибрати виклик
        // `template.EnsureOfferedForNewDocuments()` у `CreateDocumentHandler`
        // — і червоним стає рівно цей тест: архівований шаблон лишається
        // повністю придатним для кожного, хто знає `templateVersionId`, а
        // «не пропонується» звужується до вигляду списку.
        //
        // ⚠ Правило стоїть на СТВОРЕННІ й ніде більше: наявні документи цього
        // шаблону працюють далі (рішення людини на `Q15-05`).
        var archived = new Template(
            EcrCode.Create("TPLARC"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Archived" }),
            createdByUserId: 9,
            Now);
        archived.Archive(Now);

        _templates.FindTemplateOfVersionAsync(TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(archived);

        var error = await Assert.ThrowsAsync<DomainException>(() => Create(name: null));

        // ⚠ Саме КОД: суфікс `-0409` і є тим, що перетворює відмову на 409 у
        // `ExceptionHandlingMiddleware`. Перевіряти текст означало б прибити
        // до тесту речення, яке клієнтові все одно їде з каталогу.
        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);

        // Документа не з'явилося: без цього твердження тест лишався б зеленим
        // і на системі, яка спершу створює, а потім згадує перевірити шаблон.
        Assert.DoesNotContain(
            _documents.ReceivedCalls(),
            c => string.Equals(c.GetMethodInfo().Name, nameof(IDocumentStore.AddAsync), StringComparison.Ordinal));
    }

    /// <summary>
    /// L6-02 / N1-04: проєкт перенесено на іншу версію між читанням версії і записом — документ не
    /// створюється, відповідь — <c>409 ECR-DOC-4091</c> «структуру змінено».
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: прибрати <c>DocumentStructure.EnsureProjectVersionUnchanged</c> у
    /// <c>CreateDocumentHandler</c> — документ додається зі складом старої версії, тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Finding", "L6-02")]
    public async Task Проєкт_перенесено_на_іншу_версію_до_запису_документ_не_створюється()
    {
        _documents.LockProjectTemplateVersionAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>())
            .Returns(TemplateVersionId + 1);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Create(name: null));

        Assert.Equal("ECR-DOC-4091", error.ErrorCode);
        Assert.Equal(DocumentStructure.StructureChangedKey, error.Details!["messageKey"]);
        Assert.DoesNotContain(
            _documents.ReceivedCalls(),
            c => string.Equals(c.GetMethodInfo().Name, nameof(IDocumentStore.AddAsync), StringComparison.Ordinal));
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    /// <summary>
    /// L6-02 / N1-04: блок рядка проєкту береться ДО підбору ключа й додавання документа —
    /// усередині транзакції, а не перед нею.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Finding", "L6-02")]
    public async Task Блок_проєкту_береться_всередині_транзакції_до_додавання_документа()
    {
        var trace = new List<string>();
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                trace.Add("tx:open");
                await call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1));
                trace.Add("tx:commit");
            });
        _documents.LockProjectTemplateVersionAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                trace.Add("lock");
                return Task.FromResult<int?>(TemplateVersionId);
            });
        _documents.When(d => d.AddAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>()))
            .Do(_ => trace.Add("add"));

        await Create(name: null);

        Assert.Equal(["tx:open", "lock", "add", "tx:commit"], trace);
    }

    private async Task<long> Create(IReadOnlyDictionary<string, string>? name)
        => await new CreateDocumentHandler(_metadata, _documents, _templates, _uow, _access, _user, _clock)
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
