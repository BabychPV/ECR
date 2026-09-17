using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Публікація версії методології — **найнебезпечніша операція в системі**:
/// вона тихо змінює числа у вже поданих формах (ФВ-9.6).
/// </summary>
public sealed class MethodologyPublishTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 51;
    private const int PreviousVersionId = 50;
    private const long CodEntry = 901;

    /// <summary>Друга речовина того самого виходу.</summary>
    private const int NoxEntry = 902;

    private const int TonneUnit = 8;

    private static readonly DateTime Now = new(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 6, 1);

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IConstantStore _constants = Substitute.For<IConstantStore>();
    private readonly ICalculationModule _module = Substitute.For<ICalculationModule>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly ICalculationResultStore _results = Substitute.For<ICalculationResultStore>();
    private readonly IWorkflowStore _workflow = Substitute.For<IWorkflowStore>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    private readonly Methodology _methodology;
    private readonly MethodologyVersion _version;

    public MethodologyPublishTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Reviewer);
        _user.CorrelationId.Returns("test");

        // Права видані обом учасникам: предмет цих тестів — правила
        // публікації, а не доступ. Саме право перевіряє AccessDecisionTests.
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Profile());

        _methodology = new Methodology(EcrCode.Create("WATER_DISCHARGE"), Text("Water"));
        _version = AddVersion(VersionId, "1.1.0.0");

        // ⛔ `Strict`, а не типовий `Legacy`, і це не «щоб зелене». Формули
        // цього набору побудовані на `CONVERT` — нашій власній функції, якої в
        // NCalc 1.3.8 немає. Версія в `Legacy` обіцяла б відтворити числа
        // чинного рушія на виразі, якого той не рахував ніколи; саме це й
        // відхиляє `ECR-CALC-0433` (`02b` §8). `Legacy` тут стояв за
        // замовчуванням конструктора, а не за рішенням.
        _version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);

        _store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_methodology);
        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(Formulas());
        _store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(TestCases());

        // ⚠ За замовчуванням — БЕЗ прив'язок: предмет більшості тестів цього
        // класу не структурна перевірка ECR-CALC-0438 (її власні тести нижче
        // налаштовують прив'язки самі), а решта правил публікації. Методологія
        // без жодної прив'язки цю перевірку пропускає (див. `CheckArgumentColumnsAsync`).
        _bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(new List<Ecr.Domain.Entities.Configuration.CalculationBinding>());

        // Модуль повертає числа, що збігаються з очікуваними: набір зелений.
        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => Output(call.Arg<CalculationInput>(), 0.912688m));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.5")]
    public async Task Публікація_автором_останньої_правки_відхиляється_ECR_CALC_0409()
    {
        _user.UserId.Returns(Author);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Handler().HandleAsync(VersionId, "Уточнено коефіцієнт", From, CancellationToken.None));

        // ⛔ Той, хто писав формулу, дивиться на неї як автор і саме тому не
        // бачить у ній того, що побачить інший. Правило тримається системно —
        // і в базі теж (CK_MV_FourEyes), а не інструкцією (D-40).
        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.False(_version.IsPublished);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.7")]
    public async Task Публікація_без_причини_зміни_відхиляється()
    {
        var error = await Assert.ThrowsAsync<DomainException>(
            () => Handler().HandleAsync(VersionId, "   ", From, CancellationToken.None));

        // Пробіли — те саме, що порожньо. Причина потрібна не формі, а тому,
        // хто через півроку звірятиме числа (ФВ-14.7).
        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.False(_version.IsPublished);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-14.7")]
    public async Task Публікація_без_дати_набуття_чинності_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", effectiveFrom: null, CancellationToken.None));

        // ⛔ Без дати версія не має місця в часі: незрозуміло, які періоди
        // рахувати нею, а які — попередньою, і VersionOn не має відповіді.
        // У схемі це CK_MV_Published.
        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Null(_version.EffectiveFrom);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Розширення_у_версії_Legacy_відхиляється_ECR_CALC_0433()
    {
        // ⛔ `Legacy` існує рівно для того, щоб відтворити ЧИСЛА чинного рушія
        // (NCalc 1.3.8). `CONVERT` — наша власна функція, якої там немає, отже
        // формула `gsec` не рахувалася чинною системою НІКОЛИ, і відтворювати
        // їй нічого. Мовчазний пропуск дав би версію, яка обіцяє звірку, а
        // звіряти нема з чим (`02b` §8).
        _version.SetModes(NumericMode.Legacy, CalendarMode.Actual, TraceLevel.ErrorsOnly);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        Assert.Equal("ECR-CALC-0433", error.ErrorCode);

        // ⚠ Повідомлення називає ФОРМУЛУ і ФУНКЦІЮ поіменно: «версія не пройшла
        // перевірок» відправило б методолога перебирати всі чотири формули.
        Assert.Contains("gsec", error.Message, StringComparison.Ordinal);
        Assert.Contains("CONVERT", error.Message, StringComparison.Ordinal);

        // ⛔ І версія лишається чернеткою: відмова публікації — це відмова, а
        // не попередження в журналі.
        Assert.False(_version.IsPublished);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Ядро_каталогу_у_версії_Legacy_проходить()
    {
        // ⛔ Друга половина того самого правила, і без неї перша нічого не
        // варта: сторож, який відхиляє все підряд, зелений із хибної причини.
        // Формули з самих лише `Core`-функцій — а саме такі всі імпортовані з
        // `AF_*` методології — публікуються в `Legacy` без зауважень.
        _version.SetModes(NumericMode.Legacy, CalendarMode.Actual, TraceLevel.ErrorsOnly);

        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(
        [
            Formula(201, "Volume", "@Jan + @Feb + @Mar"),
            Formula(202, "MassKg", "Round(!Volume * CST.EF, 4)"),
            Formula(203, "Peak", "Max(!MassKg, Pow(2, 3))"),
        ]);

        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        Assert.True(_version.IsPublished);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.6")]
    public async Task Публікація_формує_diff_РЕЗУЛЬТАТІВ_а_не_diff_коду()
    {
        // Попередня чинна версія з іншим календарним режимом і іншим числом.
        var previous = AddVersion(PreviousVersionId, "1.0.0.0");

        // ⚠ Числовий режим той самий, що й у версії, яку публікуємо: предмет
        // цього тесту — зміна КАЛЕНДАРЯ, і другий змінений режим сховав би її
        // за собою. Обидва `Strict` з тієї ж причини, що й у конструкторі:
        // формули набору побудовані на `CONVERT`.
        previous.SetModes(NumericMode.Strict, CalendarMode.Fixed360, TraceLevel.ErrorsOnly);
        _methodology.PublishVersion(
            previous, Reviewer, "Базова", new DateOnly(2026, 1, 1), testsPassed: true, Now);

        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => Output(
                   call.Arg<CalculationInput>(),
                   call.Arg<CalculationInput>().Methodology.MethodologyVersionId == VersionId
                       ? 0.912688m
                       : 0.880000m));

        var diff = await Handler().HandleAsync(VersionId, "Уточнено ХСК", From, CancellationToken.None);

        // ⚠ Diff — про ЧИСЛА, а не про текст формул. Змінений рядок виразу не
        // каже нічого; змінена на 3.7 % емісія каже все (ФВ-9.6).
        var delta = Assert.Single(diff.Changes);
        Assert.Equal("tons", delta.OutputCode);
        Assert.Equal(0.880000m, delta.Before);
        Assert.Equal(0.912688m, delta.After);
        Assert.NotNull(delta.RelativeChange);

        // ⚠ Обидва режими — ОБОВ'ЯЗКОВО в diff (ФВ-7.8, D-78). Їх зміни не
        // видно в жодному рядку формули, а числа змінюються всі.
        Assert.True(diff.Calendar.IsChanged);
        Assert.Equal(CalendarMode.Fixed360, diff.Calendar.Before);
        Assert.Equal(CalendarMode.Actual, diff.Calendar.After);
        Assert.False(diff.Numeric.IsChanged);
        Assert.True(diff.IsSignificant);

        // Diff іде в журнал публікацій, а не лише на екран.
        await _audit.Received(1).WritePublicationEventAsync(
            Arg.Is<PublicationEventRecord>(e => e.ResultDiffJson != null && e.EntityId == VersionId),
            Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Закриті_періоди_після_публікації_НЕ_перераховуються_автоматично()
    {
        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        Assert.True(_version.IsPublished);

        // ⛔ Жодної задачі перерахунку. Публікація методології заднім числом
        // не має мовчки змінювати подану звітність (ФВ-9.7): перерахунок —
        // окрема операція з власним погодженням.
        await _jobs.DidNotReceive().EnqueueAsync<IRecalculationJob>(
            Arg.Any<object?>(), Arg.Any<CancellationToken>());
        await _jobs.DidNotReceive().ScheduleAsync<IRecalculationJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Перерахунок_закритого_періоду_потребує_окремого_погодження()
    {
        _periods.GetPeriodStatesAsync(1, 202601, Arg.Any<CancellationToken>())
                .Returns(new List<PeriodStateRef> { new(202601, PeriodState.Closed) });
        _workflow.HasSubmittedSheetsAsync(1, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(false);

        // ⚠ Тест перевіряє правило ФВ-9.4 (закритий період без погодження), а
        // не право на запуск: профіль тут — окремий, із правом
        // `Calculation.Recalculate`, яке спільний `Profile()` класу не несе
        // (той служить `PublishMethodologyHandler`, де це право не потрібне).
        // Грант на сам проєкт (`Q-238`) — теж окремо: без нього перевірка в
        // `RunCalculationHandler` відмовляла б `AccessDeniedException` ДО
        // того, як дійде до правила ФВ-9.4, яке тут і перевіряється.
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p",
            UserId = Reviewer,
            SecurityStamp = "s",
            Permissions = new HashSet<string>(StringComparer.Ordinal) { "Calculation.Recalculate" },
            Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal) { ["Project:1"] = GrantLevel.Read },
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
        });

        var handler = new RunCalculationHandler(_periods, _workflow, _results, _jobs, _uow, _access, _user, _clock);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(1, 202601, approval: null, CancellationToken.None));

        Assert.Equal("ECR-CALC-4221", error.ErrorCode);
        await _jobs.DidNotReceive().EnqueueAsync<IRecalculationJob>(
            Arg.Any<object?>(), Arg.Any<CancellationToken>());

        // ⚠ Погодження ≠ прапорець у запиті: причина обов'язкова, і погодити
        // власний перерахунок не можна.
        var own = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(
                1, 202601, new ClosedPeriodApproval(Reviewer, "треба"), CancellationToken.None));
        Assert.Equal("ECR-CALC-0409", own.ErrorCode);

        var blank = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(
                1, 202601, new ClosedPeriodApproval(Author, "  "), CancellationToken.None));
        Assert.Equal("ECR-CALC-4221", blank.ErrorCode);

        // З погодженням від іншої людини і з причиною — проходить.
        _jobs.EnqueueAsync<IRecalculationJob>(Arg.Any<object?>(), Arg.Any<CancellationToken>())
             .Returns("job-1");

        var jobId = await handler.HandleAsync(
            1, 202601, new ClosedPeriodApproval(Author, "Помилка коефіцієнта, лист №17"),
            CancellationToken.None);

        Assert.Equal("job-1", jobId);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.10")]
    public async Task Топологічний_порядок_формул_обчислюється_при_публікації()
    {
        var formulas = Formulas();
        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);

        // До публікації порядок нульовий у всіх: його ніхто не вводить руками.
        Assert.All(formulas, f => Assert.Equal(0, f.EvaluationOrder));

        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        // ⚠ Порядок обчислюється САМЕ ПРИ ПУБЛІКАЦІЇ (ФВ-9.4). У рантаймі його
        // ловити пізно: цикл мав би стати тихо неправильним числом замість
        // відмови публікації.
        Assert.All(formulas, f => Assert.True(f.EvaluationOrder > 0));
        Assert.Equal(
            formulas.Select(f => f.EvaluationOrder).Order(),
            Enumerable.Range(1, formulas.Count));

        // Дозволити людині задати порядок руками означало б, що додана формула
        // тихо зміщує решту.
        Assert.Equal(formulas.Count, formulas.Select(f => f.EvaluationOrder).Distinct().Count());

        // ⚠ І порядок саме ТОПОЛОГІЧНИЙ, а не той, у якому формули лежали в
        // списку. У фікстурі вони навмисно задані у зворотному порядку:
        // gsec ← MassKg ← Volume. Перевіряти лише «усі > 0» означало б
        // приймати будь-яку нумерацію, зокрема вхідну.
        var order = formulas.ToDictionary(f => f.Code, f => f.EvaluationOrder, StringComparer.Ordinal);

        Assert.True(order["Volume"] < order["MassKg"], "Volume має рахуватися до MassKg");
        Assert.True(order["MassKg"] < order["gsec"], "MassKg має рахуватися до gsec");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Аргумент_якого_немає_серед_колонок_прив_язаної_таблиці_відхиляється_ECR_CALC_0438()
    {
        // ⛔ «Volume» вживає `@Jan`, `@Feb`, `@Mar` (Formulas()); таблиця,
        // до якої прив'язана методологія, має колонку лише під `Jan`/`Feb` —
        // `Mar` тут немає ЖОДНОЇ структурної підстави, і збірка
        // (`CalculationInputBuilder`) не побудує для неї аргумент узагалі:
        // без цієї перевірки формула мовчки порахувала б `Volume` без березня.
        const int TableDefId = 900;

        _bindings.ListAsync(_methodology.Id, Arg.Any<CancellationToken>())
                 .Returns([new Ecr.Domain.Entities.Configuration.CalculationBinding(
                     TableDefId, columnDefId: 1, _methodology.Id, "tons", "{}")]);

        _bindings.ListColumnCodesAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(TableDefId)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, IReadOnlyList<string>>
            {
                [TableDefId] = ["Jan", "Feb"],
            });

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        // ⚠ Повідомлення називає САМЕ ЦЕЙ аргумент і САМЕ ЦЮ таблицю: методолог
        // не має перебирати всі колонки, щоб зрозуміти, чого бракує.
        Assert.Equal("ECR-CALC-0438", error.ErrorCode);
        Assert.Contains("Mar", error.Message, StringComparison.Ordinal);
        Assert.Contains(TableDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            error.Message, StringComparison.Ordinal);
        Assert.False(_version.IsPublished);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Аргументи_що_відповідають_усім_колонкам_прив_язаної_таблиці_проходять()
    {
        // Той самий набір формул, але таблиця цього разу має усі три колонки —
        // перевірка не має валити публікацію без причини.
        const int TableDefId = 900;

        _bindings.ListAsync(_methodology.Id, Arg.Any<CancellationToken>())
                 .Returns([new Ecr.Domain.Entities.Configuration.CalculationBinding(
                     TableDefId, columnDefId: 1, _methodology.Id, "tons", "{}")]);

        _bindings.ListColumnCodesAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(TableDefId)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, IReadOnlyList<string>>
            {
                [TableDefId] = ["Jan", "Feb", "Mar"],
            });

        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        Assert.True(_version.IsPublished);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.7")]
    public async Task Розбіжність_ДРУГОЇ_речовини_відхиляє_публікацію()
    {
        // ⛔ Це та сама вада, через яку золотий набір був гіршим за відсутній.
        // Звірка брала `FirstOrDefault(v => v.OutputCode == code)` зі списку,
        // який іде ПО ОДНОМУ РЯДКУ НА (речовина × вихід), — отже міряла лише
        // ПЕРШУ речовину. Друга й далі могли бути якими завгодно, і версія
        // публікувалася зеленою: «на золотому наборі жодне число не змінилося»
        // було правдою рівно про одну речовину з N.
        //
        // Тут `tons` для речовини 901 збігається з очікуванням до останнього
        // знака, а для 902 відрізняється у 5 разів.
        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => TwoSubstances(call.Arg<CalculationInput>(), 0.912688m, 4.563440m));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);

        // ⚠ Відмова називає РЕЧОВИНУ, вихід і обидва числа. Сама лічба
        // («знайдено проблем — 1») відправила б методолога перебирати всі
        // речовини набору руками, щоб дізнатися, яка саме розійшлася.
        Assert.Contains("902", error.Message, StringComparison.Ordinal);
        Assert.Contains("tons", error.Message, StringComparison.Ordinal);
        Assert.Contains("4.563440", error.Message, StringComparison.Ordinal);
        Assert.Contains("0.912688", error.Message, StringComparison.Ordinal);

        // ⚠ І та сама розбіжність лежить у `details` структурно — клієнт
        // перелічує її, не розбираючи текст повідомлення.
        Assert.NotNull(error.Details);
        Assert.True(error.Details!.ContainsKey("goldenSet"));

        Assert.False(_version.IsPublished);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.7")]
    public async Task Збіг_УСІХ_речовин_публікацію_пропускає()
    {
        // ⛔ Друга половина того самого правила, і без неї перша нічого не
        // варта: сторож, який відхиляє все підряд, зелений із хибної причини.
        // Дві речовини, обидві в межах допуску — версія публікується.
        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => TwoSubstances(call.Arg<CalculationInput>(), 0.912688m, 0.912688m));

        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        Assert.True(_version.IsPublished);
    }

    /// <summary>Профіль із небезпечним правом публікації методології.</summary>
    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = Reviewer,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Calculation.Publish" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(),
        RoleIds = new HashSet<int>(),
    };

    private PublishMethodologyHandler Handler()
        => new(_module, _store, new RealFormulaEngine(), _bindings, _uow, _audit, _access, _user, _clock);

    private MethodologyVersion AddVersion(int id, string number)
    {
        var version = new MethodologyVersion(
            _methodology.Id, number, Domain.Enums.CalculationLevel.Configuration, Author, Now);

        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, id);
        _methodology.AddVersion(version);
        return version;
    }

    private static CalculationOutput Output(CalculationInput input, decimal tons) =>
        new(input.DocumentId, input.SourceRowKey,
            [new CalculationOutputValue(
                input.Methodology.MethodologyVersionId, (int)CodEntry, "tons", tons, TonneUnit)],
            []);

    /// <summary>
    /// Той самий вихід для ДВОХ речовин — форма, яку віддає реальний рушій:
    /// список результатів іде по одному рядку на (речовина × вихід).
    /// </summary>
    private static CalculationOutput TwoSubstances(CalculationInput input, decimal cod, decimal nox) =>
        new(input.DocumentId, input.SourceRowKey,
            [
                new CalculationOutputValue(
                    input.Methodology.MethodologyVersionId, (int)CodEntry, "tons", cod, TonneUnit),
                new CalculationOutputValue(
                    input.Methodology.MethodologyVersionId, NoxEntry, "tons", nox, TonneUnit),
            ],
            []);

    private static List<MethodologyTestCase> TestCases() =>
    [
        new("golden-7001001",
            new CalculationInput(
                new MethodologyDescriptor(
                    0, VersionId, "WATER_DISCHARGE", "1.1.0.0", Domain.Enums.CalculationLevel.Configuration,
                    NumericMode.Legacy, CalendarMode.Actual, TraceLevel.Off),
                DocumentId: 700,
                TableInstanceId: 500,
                PeriodKey: new PeriodKey(202601),
                SourceRowKey: "7001001",
                Arguments: []),
            new Dictionary<string, decimal> { ["tons"] = 0.912688m },
            Tolerance: 0.000001m),
    ];

    /// <summary>
    /// Формули в порядку, ЗВОРОТНОМУ до правильного: gsec залежить від MassKg,
    /// а той — від Volume. Публікація має розкласти їх сама.
    /// </summary>
    private static List<MethodologyFormula> Formulas() =>
    [
        Formula(101, "gsec", "CONVERT(!MassKg, 'kg', 'g') / [Period].Seconds"),
        Formula(102, "MassKg", "!Volume * CST.EF"),
        Formula(103, "Volume", "@Jan + @Feb + @Mar"),
    ];

    private static MethodologyFormula Formula(int id, string code, string expression)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);

        // Ідентифікатор дає база; у тесті — руками, бо саме за ним публікація
        // зіставляє вузли графа з формулами.
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(formula, id);
        return formula;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
