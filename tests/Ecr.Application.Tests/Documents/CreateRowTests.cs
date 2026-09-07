using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>Створення рядка динамічної таблиці.</summary>
public sealed class CreateRowTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>()
    };

    private void Arrange(TableRowMode mode, int? maxRows, params string[] existingKeys)
    {
        var sheet = new SheetDef(templateVersionId: 2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(sheetDefId: 1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, mode);
        SetId(table, 3);
        if (maxRows is { } m)
        {
            table.SetMaxDynamicRows(m);
        }
        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, 700, 3, 2, Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>()));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(existingKeys.ToDictionary(k => k, k => (long)k.GetHashCode(StringComparison.Ordinal)));
        _rows.CreateRowAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<RowKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(9001L);

        // ⚠ До цього рядка мок `_access` існував тільки щоб зібрався
        // конструктор: жодне з шести тіл до нього не зверталося, а `Profile()`
        // не має ЖОДНОГО гранта. Тобто набір закріплював саме те, що прав тут
        // не питають, — і будь-яка перевірка звалила б усі шість (`A7 §4.2`).
        Allow();
    }

    /// <summary>Служба доступу дозволяє створення.</summary>
    private void Allow()
        => _access.CanCreateRowsAsync(
                      Arg.Any<AccessProfile>(), TableInstance,
                      Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                  .Returns(call => Rows(call.ArgAt<IReadOnlyCollection<string>>(2), EditDecision.Allow()));

    /// <summary>Служба доступу відмовляє із зазначеною причиною.</summary>
    /// <param name="reason">Причина відмови.</param>
    private void Deny(EditDenyReason reason)
        => _access.CanCreateRowsAsync(
                      Arg.Any<AccessProfile>(), TableInstance,
                      Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                  .Returns(call => Rows(call.ArgAt<IReadOnlyCollection<string>>(2), EditDecision.Deny(reason)));

    private static Dictionary<string, NewRowAccess> Rows(
        IReadOnlyCollection<string> keys, EditDecision row)
        => keys.ToDictionary(k => k, _ => new NewRowAccess(row, new Dictionary<int, EditDecision>()), StringComparer.Ordinal);

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });
    private static void SetId(TableDef t, int id) => typeof(Entity<int>).GetProperty("Id")!.SetValue(t, id);

    private CreateRowHandler Handler()
        => new(_rows, Substitute.For<IDocumentStore>(), _metadata, _access, _uow, _clock);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task BaseVersion_null_трактується_як_створення()
    {
        Arrange(TableRowMode.Dynamic, maxRows: null);

        var key = await Handler().HandleAsync(700, TableInstance, requestedKey: null, Profile(), CancellationToken.None);

        // Ключ згенеровано як GUID у форматі "N" (ФВ-2.5) — саме так рядок
        // без бізнес-ключа отримує стабільну ідентичність.
        Assert.Equal(32, key.Value.Length);
        Assert.DoesNotContain('-', key.Value);

        await _rows.Received(1).CreateRowAsync(
            TableInstance, Arg.Any<PeriodKey>(), Arg.Any<RowKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Перевищення_MaxDynamicRows_відхиляється()
    {
        Arrange(TableRowMode.Dynamic, maxRows: 2, "a", "b");

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            700, TableInstance, requestedKey: null, Profile(), CancellationToken.None));

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);

        // Межа — не косметика: таблиця на тисячі рядків руйнує бюджет читання
        // зрізу, і з'явитися вона має не «випадково», а свідомим рішенням.
        await _rows.DidNotReceive().CreateRowAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<RowKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-3.1")]
    public async Task Створення_рядка_у_Fixed_таблиці_заборонене()
    {
        Arrange(TableRowMode.Fixed, maxRows: null);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            700, TableInstance, requestedKey: null, Profile(), CancellationToken.None));

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);

        // У Fixed склад рядків заданий шаблоном: «зайвий» рядок зламав би і
        // формули з діапазонами, і звірку з еталоном.
        Assert.Contains("Fixed", ex.Message, StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Створення_рядка_у_Mixed_таблиці_дозволене()
    {
        // ⛔ `Mixed` — це «фіксовані рядки ПЛЮС можливість додавати свої», і
        // саме так його розуміє домен: `TableDef.AllowsDynamicRows` повертає
        // true для `Dynamic` і для `Mixed`, а прив'язка виразів на цю
        // властивість спирається (`ReferenceResolver`).
        //
        // ⚠ Обробник же питав `RowMode != Dynamic` — тобто мав ВЛАСНЕ,
        // вужче визначення того самого поняття. Наслідок: режим `Mixed`
        // існував лише на папері — таблиця в ньому не приймала жодного
        // рядка і поводилася як `Fixed`, а користувач бачив відмову з
        // причиною «рядки задані шаблоном», неправдивою для цього режиму.
        Arrange(TableRowMode.Mixed, maxRows: null);

        var key = await Handler().HandleAsync(
            700, TableInstance, requestedKey: null, Profile(), CancellationToken.None);

        Assert.Equal(32, key.Value.Length);

        await _rows.Received(1).CreateRowAsync(
            TableInstance, Arg.Any<PeriodKey>(), Arg.Any<RowKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Стеля_рядків_діє_і_в_Mixed_таблиці()
    {
        // ⚠ Друга половина тієї самої розбіжності: домен не давав задати
        // `MaxDynamicRows` для `Mixed`. Тобто режим, який дозволяє додавати
        // рядки, був єдиним БЕЗ стелі — рівно там, де вона потрібна, бо до
        // динамічних рядків додаються ще й фіксовані.
        Arrange(TableRowMode.Mixed, maxRows: 2, "a", "b");

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            700, TableInstance, requestedKey: null, Profile(), CancellationToken.None));

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Дублікат_RowKey_дає_ECR_ROW_0409()
    {
        Arrange(TableRowMode.Dynamic, maxRows: null, "7001001");

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            700, TableInstance, RowKey.Create("7001001"), Profile(), CancellationToken.None));

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);

        // Унікальність (PeriodKey, TableInstanceId, RowKey) — не лише
        // обмеження бази: на RowKey посилаються формули й аудит.
        await _rows.DidNotReceive().CreateRowAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<RowKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.8")]
    public async Task Закритий_період_не_дає_додати_рядок()
    {
        // ⛔ `EditRules` про цю саме перевірку каже: «Закритий період блокує
        // ВСІХ, включно з `Manage`. Це головна перевірка моделі доступу: якщо
        // вона пропускає, зламана вся модель, і жоден інший тест цього не
        // покаже». Вона пропускала: обробник не звертався до
        // `IAccessDecisionService` жодного разу — залежність була вприснута й
        // не читана, і компілятор казав це прямо (`CS9113`).
        //
        // ⚠ Разом із періодом сюди повертаються симуляція, заархівований
        // проєкт, поданий аркуш і `isDeny`-гранти: усі вони живуть в одному
        // рішенні, і поки його ніхто не питав, не діяв жоден із них.
        Arrange(TableRowMode.Dynamic, maxRows: null);
        Deny(EditDenyReason.PeriodClosed);

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            700, TableInstance, requestedKey: null, Profile(), CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        Assert.Equal(nameof(EditDenyReason.PeriodClosed), ex.Details!["reason"]);

        await _rows.DidNotReceive().CreateRowAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<RowKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.8")]
    public async Task Права_питають_ДО_створення_а_не_після()
    {
        // ⚠ Порядок тут — частина вимоги, а не смак. Перевірка після вставки
        // означала б рядок у базі й відкат, який комусь колись не станеться;
        // до того ж `CreateRowAsync` бере `Id` із `SEQUENCE`, тобто витрачає
        // номер незалежно від результату.
        Arrange(TableRowMode.Dynamic, maxRows: null);
        Deny(EditDenyReason.DocumentSubmitted);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            700, TableInstance, requestedKey: null, Profile(), CancellationToken.None));

        await _access.Received(1).CanCreateRowsAsync(
            Arg.Any<AccessProfile>(), TableInstance,
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.8")]
    public async Task Екземпляр_таблиці_чужого_документа_відхиляється()
    {
        // ⛔ Без цієї звірки шлях у URL декоративний: клієнт вказав би чужий
        // `TableInstanceId` і писав би в чужий документ, маючи право лише на
        // свій. `Patch` цю перевірку робить і пояснює навіщо
        // (`CellsController.cs:55-58`); сюди вона не доїхала, хоча `documentId`
        // сюди приходив — і не читався взагалі.
        //
        // ⚠ Перевірка стоїть в ОБРОБНИКУ, а не в контролері, як у `Patch`:
        // обробник — єдина точка, повз яку не пройде другий виклик.
        Arrange(TableRowMode.Dynamic, maxRows: null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Handler().HandleAsync(
            documentId: 701, TableInstance, requestedKey: null, Profile(), CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", ex.ErrorCode);

        await _rows.DidNotReceive().CreateRowAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<RowKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.8")]
    public async Task Рішення_на_рядок_не_прийшло_це_відмова()
    {
        // ⛔ Той самий напрям замовчування, що й у пакетному записі: служба
        // зобов'язана відповісти на кожен запитаний ключ, а мовчанка означає
        // відмову. «Немає рішення, отже можна» — це і був дефект.
        Arrange(TableRowMode.Dynamic, maxRows: null);
        _access.CanCreateRowsAsync(
                   Arg.Any<AccessProfile>(), TableInstance,
                   Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, NewRowAccess>(StringComparer.Ordinal));

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            700, TableInstance, requestedKey: null, Profile(), CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
    }
}
