// tests/Ecr.Api.Tests/AdminListQueryCountTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>RD-06</c>: число звернень до БД на адміністративний перелік
/// <b>не залежить</b> від кількості елементів у ньому.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Замір, а не таймінг.</b> Лічильник — <c>Ecr.TestKit.DbCommandCounter</c>
/// (інструмент рядка <c>MS-01</c>). EF-перехоплювач тут доречний і достатній:
/// обидва шляхи повністю EF-ові, сирих команд на них немає взагалі — той
/// «вужчий обсяг», який <c>DbCommandCounter</c> сам називає перевагою для
/// <c>N+1</c>.
/// </para>
/// <para>
/// ⛔ <b>Чому це тут, а не в тесті з підміненим сховищем.</b> Підмінене
/// сховище доводить лише, що ОБРОБНИК кличе порт один раз
/// (<c>Ecr.Application.Tests…ListMethodologiesBatchTests</c>,
/// <c>SwitchRegistrySourceTests</c>). Воно нічого не знає про те, скільки
/// команд випустить EF усередині: <c>Include</c> роздільним запитом, ліниве
/// завантаження версій, батч із кількох <c>SELECT</c> — усе це дало б
/// «один виклик порту» і N запитів до бази. Ці два рівні доказу не
/// замінюють один одного.
/// </para>
/// <para>
/// ⚠ Порівнюються ДВА розміри (<c>N = 1</c> і <c>N = 5</c>), а не одне число.
/// Саме рівність і є твердженням рядка; «запитів мало» було б зеленим і для
/// коду, який нічого не читає, — тому поруч звіряється ще й вміст відповіді.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class AdminListQueryCountTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Перелік методологій: <c>N = 1</c> і <c>N = 5</c> дають те саме число.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація, що валить тест (перевірена перезбіркою): повернути в
    /// <c>ListMethodologiesHandler</c> цикл
    /// <c>foreach (id) → FindAsync(id)</c>. Числа стають 1 і 5 замість 1 і 1.
    ///
    /// ⚠ Ідентифікатори передаються ЯВНО, а не порожнім переліком: база
    /// спільна на всю збірку, і «всі активні» залежало б від того, що встигли
    /// завести сусідні тести — тобто замір залежав би від порядку прогону.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Методології_число_запитів_не_росте_з_переліком()
    {
        var one = await MeasureMethodologiesAsync(1).ConfigureAwait(true);
        var five = await MeasureMethodologiesAsync(5).ConfigureAwait(true);

        // Головне твердження рядка RD-06.
        Assert.True(
            one.Queries == five.Queries,
            $"N=1 дав {one.Queries}, N=5 дав {five.Queries}.\n"
            + $"N=1:\n{one.Detail}\nN=5:\n{five.Detail}");

        Assert.True(one.Queries == 1, $"очікувався один запит, а було {one.Queries}:\n{one.Detail}");

        // ⛔ Без цього рівність вище була б зеленою і для обробника, який
        // нічого не повернув: нуль роботи теж не залежить від N.
        Assert.Equal(1, one.Returned);
        Assert.Equal(5, five.Returned);

        // ⚠ Версії справді приїхали, тобто `Include` відпрацював у ТОМУ Ж
        // запиті. Без цієї перевірки ліниве довантаження версій пізніше (поза
        // вікном заміру) виглядало б як «один запит».
        Assert.Equal(1, one.VersionsOfFirst);
        Assert.Equal(1, five.VersionsOfFirst);
    }

    /// <summary>
    /// Перемикання master: набір розв'язується <b>одним</b> запитом.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: повернути <c>foreach (code) → FindDefinitionAsync(code)</c> —
    /// число стає 2 і 6 замість 2 і 2.
    ///
    /// ⚠ Два, а не один: розв'язання набору плюс питання «чи є відкритий
    /// період». Друге ставиться ОДИН раз на весь набір, і це теж тримає саме
    /// число, а не коментар. <c>IUnitOfWork</c> і <c>IAuditWriter</c>
    /// підмінені, тож запис у вікно заміру не входить — рядок <c>RD-06</c>
    /// про читання.
    ///
    /// ⚠ База збірки спільна: відкритий період, заведений сусіднім тестом,
    /// відхилив би операцію <c>ECR-REG-0422</c> — але ПІСЛЯ обох запитів, тож
    /// число те саме. Саме тому ця відмова ловиться й ігнорується: інакше
    /// замір залежав би від порядку прогону.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Довідники_набір_розвязується_одним_запитом()
    {
        var one = await MeasureSwitchAsync(1).ConfigureAwait(true);
        var five = await MeasureSwitchAsync(5).ConfigureAwait(true);

        Assert.True(
            one.Queries == five.Queries,
            $"N=1 дав {one.Queries}, N=5 дав {five.Queries}.\n"
            + $"N=1:\n{one.Detail}\nN=5:\n{five.Detail}");

        // Рівно два: розв'язання набору і питання про відкриті періоди. Друге
        // ставиться ОДИН раз на весь набір — це теж властивість, яку тримає
        // саме число, а не коментар.
        Assert.True(one.Queries == 2, $"очікувалося два запити, а було {one.Queries}:\n{one.Detail}");
    }

    private async Task<(int Queries, int Returned, int VersionsOfFirst, string Detail)>
        MeasureMethodologiesAsync(int methodologyCount)
    {
        var ids = await SeedMethodologiesAsync(methodologyCount).ConfigureAwait(true);

        var counter = new DbCommandCounter();
        await using var db = Context(counter);

        var handler = new ListMethodologiesHandler(
            new MethodologyDraftStore(db), Access(ListMethodologiesHandler.Permission), User());

        // Прогрів: перший запит на з'єднанні несе службові команди EF, і без
        // скидання вони потрапили б у число разом із корисними.
        await handler.HandleAsync(ids, CancellationToken.None).ConfigureAwait(true);

        counter.Tally.Reset();
        var result = await handler.HandleAsync(ids, CancellationToken.None).ConfigureAwait(true);
        var seen = counter.Tally.Snapshot();

        // ⚠ Рахується ЗАГАЛЬНЕ число команд, а не одна категорія. Усі порти,
        // крім сховища методологій, цей обробник не чіпає взагалі, тож у вікно
        // заміру не потрапляє нічого стороннього — а категорія, навпаки,
        // залежить від форми SQL, яку EF вибирає сам (`Include` із `TOP` дає
        // підзапит, і ім'я таблиці перестає стояти одразу після `FROM`).
        return (seen.Total, result.Count, result[0].Versions.Count, seen.Format());
    }

    private async Task<(int Queries, string Detail)> MeasureSwitchAsync(int registryCount)
    {
        var codes = await SeedRegistriesAsync(registryCount).ConfigureAwait(true);

        var counter = new DbCommandCounter();
        await using var db = Context(counter);

        var uow = Substitute.For<IUnitOfWork>();
        uow.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(
                call.ArgAt<CancellationToken>(1)));

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        var handler = new SwitchRegistrySourceHandler(
            new RegistryStore(db),
            uow,
            Substitute.For<IAuditWriter>(),
            Access(SwitchRegistrySourceHandler.Permission),
            User(),
            clock);

        counter.Tally.Reset();

        try
        {
            await handler
                .HandleAsync(codes, RegistrySourceKind.External, "RD-06: замір", CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (BusinessRuleException)
        {
            // Відкритий період у спільній базі збірки. Набір на цей момент уже
            // розв'язано — саме це число й міряється; див. зауваження до тесту.
        }

        var seen = counter.Tally.Snapshot();

        return (seen.Total, seen.Format());
    }

    /// <summary>Заводить методології з однією версією кожна; повертає їхні <c>Id</c>.</summary>
    private async Task<IReadOnlyList<int>> SeedMethodologiesAsync(int count)
    {
        await using var db = Context(counter: null);

        // ⚠ Коди унікальні на кожен виклик: база лишається після прогону
        // навмисно, а `UQ_Methodology` не дасть завести ті самі вдруге.
        var prefix = $"RD06M{Guid.NewGuid():N}"[..20];
        var seeded = new List<Methodology>(count);

        for (var i = 0; i < count; i++)
        {
            var methodology = new Methodology(
                EcrCode.Create($"{prefix}_{i}"),
                new LocalizedText(new Dictionary<string, string> { ["uk"] = $"{prefix}_{i}" }));

            db.Methodologies.Add(methodology);
            seeded.Add(methodology);
        }

        await db.SaveChangesAsync().ConfigureAwait(true);

        // Версія додається ДРУГИМ збереженням: до першого `MethodologyId` ще
        // нуль, а саме його перевіряє `UQ_MethodologyVersion`.
        //
        // ⛔ І вона ОПУБЛІКОВАНА, а не чернетка. `MethodologyDto` віддає лише
        // опубліковані версії з датою чинності, тож на чернетці перевірка
        // «версії приїхали тим самим запитом» була б хибно червоною —
        // порожній перелік версій означав би не «Include не спрацював», а
        // «обробник відфільтрував правильно».
        //
        // ⚠ Публікує ІНШИЙ користувач: `CK_MV_FourEyes` тримає чотири ока на
        // рівні бази, і рівність авторів відхилив би сам SQL Server.
        foreach (var methodology in seeded)
        {
            var version = new MethodologyVersion(
                methodology.Id, "1.0.0.0", CalculationLevel.Configuration, 9, Now);

            methodology.AddVersion(version);
            methodology.PublishVersion(
                version,
                publishedByUserId: 10,
                changeReason: "RD-06: насіння заміру",
                effectiveFrom: new DateOnly(2026, 1, 1),
                testsPassed: true,
                utcNow: Now);
        }

        await db.SaveChangesAsync().ConfigureAwait(true);

        return seeded.Select(m => m.Id).ToList();
    }

    /// <summary>Заводить довідники в <c>Local</c>; повертає їхні коди.</summary>
    private async Task<IReadOnlyList<string>> SeedRegistriesAsync(int count)
    {
        await using var db = Context(counter: null);

        var prefix = $"RD06R{Guid.NewGuid():N}"[..20];
        var codes = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var code = $"{prefix}_{i}";
            db.RegistryDefs.Add(new RegistryDef(
                EcrCode.Create(code),
                new LocalizedText(new Dictionary<string, string> { ["uk"] = code }),
                isTemporal: true));

            codes.Add(code);
        }

        await db.SaveChangesAsync().ConfigureAwait(true);

        return codes;
    }

    private EcrDbContext Context(DbCommandCounter? counter)
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"));

        if (counter is not null)
        {
            options.AddInterceptors(counter);
        }

        return new EcrDbContext(options.Options);
    }

    private static IAccessDecisionService Access(string permission)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(permission).Build());

        return access;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.Language.Returns("uk");
        user.CorrelationId.Returns("rd06");

        return user;
    }
}
