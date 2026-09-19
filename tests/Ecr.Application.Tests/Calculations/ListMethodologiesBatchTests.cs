// tests/Ecr.Application.Tests/Calculations/ListMethodologiesBatchTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// <c>RD-06</c>: перелік методологій читається <b>одним</b> зверненням до
/// сховища, скільки б методологій у ньому не було.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Доти обробник робив <c>ListActiveIdsAsync</c>, а далі <c>FindAsync</c> у
/// ЦИКЛІ — <c>1 + N</c> звернень до бази на кожне відкриття екрана
/// конфігуратора, де <c>N</c> — усі активні методології корпусу (їх там
/// сотні). Відкриття коштувало рівно стільки, скільки методологій завели.
/// </para>
/// <para>
/// ⚠ Тут рахуються звернення до <b>порту</b>, а не команди SQL. Того, що порт
/// кличуть один раз, недостатньо: реалізація могла б віддати один виклик і
/// випустити N запитів усередині (<c>Include</c> роздільним запитом, ліниве
/// завантаження). Саме це число міряє
/// <c>Ecr.Api.Tests.AdminListQueryCountTests</c> на живій базі лічильником
/// <c>DbCommandCounter</c>. Два рівні доказу потрібні обидва: цей ловить
/// повернення циклу в обробник, той — розмноження запитів у сховищі.
/// </para>
/// </remarks>
public sealed class ListMethodologiesBatchTests
{
    private static readonly DateTime Now = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string[] RequestedOrder = ["C", "A", "B"];
    private static readonly string[] OnlyKnown = ["A"];

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ListMethodologiesBatchTests()
    {
        _user.UserId.Returns(9);
        _user.Language.Returns("uk");

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(ListMethodologiesHandler.Permission)
                .Build());
    }

    /// <summary>
    /// Один елемент і п'ять — <b>однакове</b> число звернень.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація, що валить тест: повернути в обробник
    /// <c>foreach (id) → FindAsync(id)</c>. Тоді <c>N = 1</c> дає 2 звернення
    /// (ids + одне читання), <c>N = 5</c> — 6, і рівність зникає.
    ///
    /// ⚠ Перевіряється саме РІВНІСТЬ двох розмірів, а не «звернення одне».
    /// «Одне звернення» було б зеленим і для обробника, який нічого не читає;
    /// тому нижче ще й звіряється вміст відповіді.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Число_звернень_не_залежить_від_кількості_методологій()
    {
        var one = await MeasureAsync(1);
        var five = await MeasureAsync(5);

        Assert.Equal(one.StoreCalls, five.StoreCalls);
        Assert.Equal(1, one.StoreCalls);

        // ⛔ Замір недійсний, якщо обробник насправді нічого не віддав: нуль
        // роботи теж дає стале число звернень.
        Assert.Equal(1, one.Returned);
        Assert.Equal(5, five.Returned);
    }

    /// <summary>
    /// Поштучного <c>FindAsync</c> не лишилося: пакет ЗАМІНИВ цикл, а не додався.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цієї перевірки попередній тест лишився б зеленим і в разі, якби
    /// хтось дописав пакетний виклик ПЕРЕД циклом: пакет один, рівність
    /// тримається, а N+1 нікуди не подівся. Різниця видна тільки звідси.
    ///
    /// ⚠ <c>ListActiveIdsAsync</c> теж не має викликатися: пакетний метод сам
    /// знає, що порожній перелік означає «всі активні», і окремий запит по
    /// ідентифікатори був би другим зверненням із того самого приводу.
    ///
    /// ⛔ Перевірка стоїть на ТОМУ САМОМУ підмінюванні, яке бачив обробник
    /// (<c>measured.Store</c>), а не на полі класу. Спершу тут стояло поле —
    /// і мутаційний прогін показав, що тест лишається ЗЕЛЕНИМ із поверненим
    /// циклом: обробник читав поштучно з локального підмінювання, а перевірка
    /// питала інше, якого ніхто не торкався. Тавтологія, знайдена рівно тим,
    /// заради чого мутацію й ганяють.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Поштучного_читання_не_лишилося()
    {
        var measured = await MeasureAsync(5);

        await measured.Store.DidNotReceive().FindAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await measured.Store.DidNotReceive().ListActiveIdsAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Явні ідентифікатори віддаються <b>в порядку запиту</b>, а не бази.
    /// </summary>
    /// <remarks>
    /// ⛔ Ризик саме пакетного читання, якого в циклі не було: база віддає
    /// рядки у своєму порядку (тут — за кодом), і без відновлення порядку
    /// клієнт, що передав ids усвідомлено, отримав би переставлені рядки.
    /// Цикл цієї помилки зробити не міг — він ішов по запиту.
    ///
    /// ⛔ Мутація: прибрати відновлення порядку (віддати <c>found</c> як є) —
    /// очікується <c>C, A, B</c>, приходить <c>A, B, C</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Порядок_відповіді_повторює_порядок_запиту()
    {
        // Сховище віддає за кодом — A, B, C; запит просить C, A, B.
        var stored = new[] { Methodology(1, "A"), Methodology(2, "B"), Methodology(3, "C") };

        _drafts.ListWithVersionsAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Methodology>)stored);

        var result = await Handler().HandleAsync([3, 1, 2], CancellationToken.None);

        Assert.Equal(RequestedOrder, result.Select(m => m.Code));
    }

    /// <summary>
    /// Невідомий ідентифікатор мовчки пропускається — як і раніше.
    /// </summary>
    /// <remarks>
    /// ⚠ Поведінка збережена свідомо: до правки <c>FindAsync</c> повертав
    /// <c>null</c>, і обробник робив <c>continue</c>. Перелік адміністрування —
    /// не адресний запит, і <c>404</c> на один зниклий рядок закрив би весь
    /// екран.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Невідомий_ідентифікатор_не_ламає_переліку()
    {
        _drafts.ListWithVersionsAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Methodology>)[Methodology(1, "A")]);

        var result = await Handler().HandleAsync([1, 777], CancellationToken.None);

        Assert.Equal(OnlyKnown, result.Select(m => m.Code));
    }

    private async Task<(int StoreCalls, int Returned, IMethodologyDraftStore Store)> MeasureAsync(
        int methodologyCount)
    {
        var drafts = Substitute.For<IMethodologyDraftStore>();
        var stored = Enumerable
            .Range(1, methodologyCount)
            .Select(i => Methodology(i, $"M{i:00}"))
            .ToList();

        drafts.ListWithVersionsAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Methodology>)stored);

        // ⚠ `FindAsync` теж підмінений і повертає ті самі дані: якби цикл
        // лишився, тест упав би на ЧИСЛІ звернень, а не на порожній відповіді.
        // Хибне падіння «нема даних» сховало б справжню причину.
        foreach (var methodology in stored)
        {
            drafts.FindAsync(methodology.Id, Arg.Any<CancellationToken>()).Returns(methodology);
        }

        drafts.ListActiveIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<int>)stored.Select(m => m.Id).ToList());

        // ⛔ Межа між налаштуванням і заміром проводиться ЯВНО. NSubstitute
        // сам знімає останній виклик, накритий `.Returns(…)`, але покладатися
        // на це означало б, що число звернень залежить від форми налаштування
        // вище, а не від коду обробника.
        drafts.ClearReceivedCalls();

        var handler = new ListMethodologiesHandler(drafts, _access, _user);
        var result = await handler.HandleAsync([], CancellationToken.None);

        return (drafts.ReceivedCalls().Count(c => c.GetMethodInfo().Name.EndsWith(
                    "Async", StringComparison.Ordinal)),
                result.Count,
                drafts);
    }

    private ListMethodologiesHandler Handler() => new(_drafts, _access, _user);

    /// <summary>Методологія з призначеним <c>Id</c> — його дає база.</summary>
    private static Methodology Methodology(int id, string code)
    {
        var methodology = new Methodology(
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = code }));

        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(methodology, id);
        methodology.AddVersion(
            new MethodologyVersion(id, "1.0.0.0", CalculationLevel.Configuration, 9, Now));

        return methodology;
    }
}
