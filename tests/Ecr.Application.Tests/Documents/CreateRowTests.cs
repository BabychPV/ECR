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
        Denies = new HashSet<string>()
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
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });
    private static void SetId(TableDef t, int id) => typeof(Entity<int>).GetProperty("Id")!.SetValue(t, id);

    private CreateRowHandler Handler() => new(_rows, _cells, _metadata, _access, _uow, _clock);

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
}
