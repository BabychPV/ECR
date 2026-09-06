using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Презентаційна правка «на льоту» — те, заради чого існує
/// <c>PresentationRevision</c> (ФВ-7.2).
/// </summary>
public sealed class PatchPresentationTests
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IRepository<TemplateVersion, int> _versions = Substitute.For<IRepository<TemplateVersion, int>>();
    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TemplateVersion _published;

    public PatchPresentationTests()
    {
        _published = new TemplateVersion(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
        _published.Publish(publishedByUserId: 8, utcNow: Now);

        _clock.UtcNow.Returns(Now);
        _versions.GetAsync(1, Arg.Any<CancellationToken>()).Returns(_published);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        _store.IncrementPresentationRevisionAsync(1, Arg.Any<CancellationToken>()).Returns(1);

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());
    }

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private PatchPresentationHandler Handler()
        => new(_versions, _store, new ChangeClassifier(), _audit, _uow, _clock, _access, _user);

    private const string HeaderPatch =
        """[{"entityType":"ColumnDef","entityId":5,"field":"HeaderL10n","value":"{\"en\":\"Volume, m3\"}"}]""";

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Зміна_підпису_опублікованої_версії_проходить()
    {
        var revision = await Handler().PatchAsync(1, HeaderPatch, userId: 9, CancellationToken.None);

        Assert.Equal(1, revision);
        Assert.Equal(1, _published.PresentationRevision);

        // Версія лишається опублікованою: презентаційна правка не «розморожує» її.
        Assert.Equal(TemplateVersionStatus.Published, _published.Status);
        Assert.True(_published.IsStructurallyFrozen);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Зміна_типу_даних_відхиляється_з_ECR_TMPL_0409()
    {
        const string patch =
            """[{"entityType":"ColumnDef","entityId":5,"field":"DataType","value":"Decimal"}]""";

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().PatchAsync(1, patch, userId: 9, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);

        // Ревізія не інкрементована і нічого не збережено — інакше клієнти
        // отримали б новий ключ кешу на структуру, якої не існує.
        await _store.DidNotReceive().IncrementPresentationRevisionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(0, _published.PresentationRevision);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-7.4")]
    public async Task Перейменування_коду_колонки_на_версії_з_документами_дає_ECR_SCHM_0409()
    {
        // ⛔ ФВ-7.4 каже дослівно: `Breaking`-зміна у версії, до якої вже
        // прив'язані документи, — це ВІДМОВА ОПЕРАЦІЇ, а не попередження. Доки
        // код `ECR-SCHM-0409` не кидав ніхто, ця зміна поверталася загальним
        // `ECR-TMPL-0409` разом із порадою «внесіть це клонуванням версії»
        // (ФВ-7.1) — і саме порада тут коштує дорого: клон із новим кодом
        // колонки НЕ рятує введені дані. Комірка посилається на код, тож після
        // переходу документів значення просто перестають знаходитися, і
        // дізнаються про це не з відмови, а з порожньої форми через місяць.
        const string patch =
            """[{"entityType":"ColumnDef","entityId":5,"field":"Code","value":"VOLUME_M3"}]""";

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().PatchAsync(1, patch, userId: 9, CancellationToken.None));

        Assert.Equal("ECR-SCHM-0409", ex.ErrorCode);

        // ⚠ Тип винятку тут — частина перевірки, а не випадковість: статус
        // відповіді береться з ТИПУ, і `BusinessRuleException` дав би 422 при
        // коді `…0409`. Саме така суперечність усередині одного коду щойно
        // виправлена в `ECR-PRD-0422` (`P-25`), і відтворювати її новим кодом
        // не можна.
        Assert.Equal(
            ChangeClass.Breaking,
            new ChangeClassifier().Classify("ColumnDef", "Code", hasDocuments: true));

        // Порушник названий поіменно, і кількість зачеплених документів теж:
        // «щось структурне» не дає підстав ухвалити рішення.
        Assert.Contains("ColumnDef.Code", ex.Message, StringComparison.Ordinal);
        Assert.Equal(true, ex.Details?["hasDocuments"]);

        // Нічого не застосовано і ревізія не інкрементована.
        await _store.DidNotReceive().ApplyPresentationAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<PresentationChange>>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().IncrementPresentationRevisionAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-7.4")]
    public async Task Те_саме_перейменування_без_документів_лишається_ФВ_7_1()
    {
        // ⛔ Друга половина ФВ-7.4, без якої перша нічого не означає: та сама
        // зміна на версії БЕЗ документів не є `Breaking` — рятувати нічого, і
        // класифікатор повертає `Safe`. Відмова лишається, але це вже ФВ-7.1
        // («опублікована версія структурно незмінна»), і порада «внесіть
        // клонуванням» тут правильна.
        //
        // ⚠ Без цього тесту перший був би зеленим і від «кидати ECR-SCHM-0409
        // на будь-яку структурну зміну» — тобто від відмови, яка забороняє те,
        // що дозволено.
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        const string patch =
            """[{"entityType":"ColumnDef","entityId":5,"field":"Code","value":"VOLUME_M3"}]""";

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().PatchAsync(1, patch, userId: 9, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Змішаний_патч_із_однією_структурною_зміною_відхиляється_повністю()
    {
        const string patch = """
            [{"entityType":"ColumnDef","entityId":5,"field":"HeaderL10n","value":"{\"en\":\"A\"}"},
             {"entityType":"ColumnDef","entityId":6,"field":"Ordinal","value":"2"},
             {"entityType":"ColumnDef","entityId":7,"field":"Precision","value":"18"}]
            """;

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().PatchAsync(1, patch, userId: 9, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);

        // Дві легітимні зміни з трьох НЕ застосовані: патч є одним цілим.
        // Часткове застосування залишило б користувача в стані, який він
        // не замовляв і не бачить.
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteStructureChangeAsync(
            Arg.Any<StructureChangeRecord>(), Arg.Any<CancellationToken>());

        // Порушник названий поіменно — інакше користувач шукав би його наосліп.
        Assert.Contains("Precision", ex.Message, StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-7.2")]
    public async Task Успішний_патч_інкрементує_ревізію_і_записує_аудит()
    {
        await Handler().PatchAsync(1, HeaderPatch, userId: 9, CancellationToken.None);

        await _store.Received(1).IncrementPresentationRevisionAsync(1, Arg.Any<CancellationToken>());

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.TemplateVersionId == 1 &&
                r.ChangeClass == ChangeClass.Presentation &&
                r.ChangedByUserId == 9),
            Arg.Any<CancellationToken>());

        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-7.2")]
    public async Task Патч_справді_міняє_поле_а_не_лише_піднімає_ревізію()
    {
        // ⛔ Найдорожчий різновид зеленого тесту — той, що перевіряє все
        // навколо дії, крім самої дії. Тести цього файлу дивилися на ревізію
        // і на аудит; обробник же розбирав патч, класифікував зміни, відхиляв
        // структурні, піднімав ревізію, писав аудит — і **не змінював жодного
        // поля**. Підпис колонки лишався старим, ключ кешу ставав новим, і всі
        // клієнти перечитували структуру, щоб побачити те саме.
        //
        // ⚠ Аудит при цьому запевняв, що зміна відбулася: у `NewJson` лежало
        // значення, якого в базі не було ніколи.
        await Handler().PatchAsync(1, HeaderPatch, userId: 9, CancellationToken.None);

        await _store.Received(1).ApplyPresentationAsync(
            1,
            Arg.Is<IReadOnlyList<PresentationChange>>(changes =>
                changes.Count == 1
                && changes[0].EntityType == "ColumnDef"
                && changes[0].EntityId == 5
                && changes[0].Field == "HeaderL10n"),
            Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Відхилений_патч_не_застосовує_нічого()
    {
        const string patch =
            """[{"entityType":"ColumnDef","entityId":5,"field":"DataType","value":"Decimal"}]""";

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().PatchAsync(1, patch, userId: 9, CancellationToken.None));

        // Класифікація йде ПЕРЕД будь-яким записом: інакше половина патча
        // застосувалася б, а друга половина відхилилася.
        await _store.DidNotReceive().ApplyPresentationAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<PresentationChange>>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Зміна_Ordinal_не_впливає_на_результати_формул_із_діапазонами()
    {
        const string patch =
            """[{"entityType":"ColumnDef","entityId":5,"field":"Ordinal","value":"9"}]""";

        var revision = await Handler().PatchAsync(1, patch, userId: 9, CancellationToken.None);

        // Ordinal класифікується як презентаційний саме тому, що формули на
        // нього не спираються: діапазони розкриваються в явний список RowKey
        // при Publish, і в рантаймі діапазонів не існує (B03 §4).
        Assert.Equal(ChangeClass.Presentation, new ChangeClassifier().Classify("ColumnDef", "Ordinal", hasDocuments: true));
        Assert.Equal(1, revision);

        // Ключ кешу змінився — клієнт перечитає структуру і побачить новий
        // порядок; самі числа при цьому лишаються тими самими.
        Assert.Equal($"v{_published.Id}:r1", _published.CacheKey);
    }
}
