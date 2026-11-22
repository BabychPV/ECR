// tests/Ecr.Infrastructure.Tests/Persistence/UnitConcurrencyTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Units;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Довідник одиниць під паралельними записами на РЕАЛЬНОМУ SQL Server, двома з'єднаннями
/// (аудит C6).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Що було. Версія з <c>If-Match</c> звірялася в пам'яті з рядком, прочитаним під RCSI без
/// блокування, а перевірка «на одиницю ніщо не посилається» читала знімок: дві правки з
/// однаковою версією обидві проходили (друга мовчки затирала першу), а посилання, що
/// комітилося між перевіркою й записом, для перевірки не існувало — множник мінявся під уже
/// збереженим посиланням, а видалення падало на FK голим <c>500</c>.
/// </para>
/// <para>
/// ⚠ Гонки відтворено детерміновано: або чужа транзакція тримає незакомічене посилання, поки
/// обробник перевіряє, або обробник спиняється рівно у вікні «перевірено — ще не записано»
/// (<see cref="PausingUnitOfWork"/>, <see cref="PausingUnitStore"/>). Пауза обмежена
/// <see cref="Window"/>: під виправленим кодом чужий запис чекає на блокування, і пауза
/// закінчується за часом, а не за подією.
/// </para>
/// <para>
/// Мутаційні докази: <c>UPDLOCK, HOLDLOCK</c> прибрано з <c>UnitStore.LockUnitAsync</c> →
/// червоний <see cref="Дві_одночасні_правки_з_однією_версією_друга_дає_409_а_не_затирає_першу"/>;
/// прибрано <c>SET TRANSACTION ISOLATION LEVEL SERIALIZABLE</c> у
/// <c>UnitStore.FindUnitUsageForUpdateAsync</c> → червоні три решта.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class UnitConcurrencyTests(SqlServerFixture sql)
{
    /// <summary>Скільки обробник тримає вікно гонки відкритим, чекаючи на чужий запис.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2.5);

    /// <summary>Стеля на будь-яке очікування тесту: зависання — провал, а не вічний прогін.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дві_одночасні_правки_з_однією_версією_друга_дає_409_а_не_затирає_першу()
    {
        var (unitId, _) = await ArrangeAsync();
        var version = await VersionAsync(unitId);

        await using var firstDb = sql.CreateContext();
        await using var secondDb = sql.CreateContext();

        var reachedSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Перший перевірив версію і стоїть перед записом, доки другий не завершиться
        // (або не мине вікно — коли другий чекає на блокування першого).
        var firstUow = new PausingUnitOfWork(new UnitOfWork(firstDb), reachedSave, secondDone.Task);
        var first = Outcome(() => Update(firstDb, firstUow).HandleAsync(
            unitId, Text("u"), Text("Alpha"), 2m, 0m, version, CancellationToken.None));

        await ReachedOrFailedAsync(reachedSave, first, "перший запис дійшов до паузи перед збереженням");

        var second = Outcome(() => Update(secondDb, new UnitOfWork(secondDb)).HandleAsync(
            unitId, Text("u"), Text("Beta"), 2m, 0m, version, CancellationToken.None));
        _ = second.ContinueWith(_ => secondDone.TrySetResult(), TaskScheduler.Default);

        var outcomes = await Task.WhenAll(first, second).WaitAsync(Ceiling);

        Assert.Null(outcomes[0]);
        var conflict = Assert.IsType<ConcurrencyConflictException>(outcomes[1]);
        Assert.Equal("ECR-UOM-0409", conflict.ErrorCode);
        Assert.Equal("err.ECR-UOM-0409.unitChanged", conflict.Details!["messageKey"]);

        // ⛔ Головне: перший запис не загублено.
        await using var read = sql.CreateContext();
        var unit = await read.Units.AsNoTracking().SingleAsync(u => u.Id == unitId);
        Assert.Equal("Alpha", unit.NameL10n.Values["en"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Посилання_що_комітиться_під_час_перевірки_зупиняє_зміну_множника()
    {
        var (unitId, otherId) = await ArrangeAsync();
        var version = await VersionAsync(unitId);

        await using var holderDb = sql.CreateContext();
        await using var holder = await holderDb.Database.BeginTransactionAsync();
        holderDb.UnitConversions.Add(new UnitConversion(otherId, unitId, 3m, 0m, 0, null));
        await holderDb.SaveChangesAsync();

        await using var db = sql.CreateContext();
        var update = Outcome(() => Update(db, new UnitOfWork(db)).HandleAsync(
            unitId, Text("u"), Text("Unit"), 5m, 0m, version, CancellationToken.None));

        await Task.WhenAny(update, Task.Delay(Window));
        await holder.CommitAsync();

        var conflict = Assert.IsType<ConcurrencyConflictException>(await update.WaitAsync(Ceiling));
        Assert.Equal("err.ECR-UOM-0409.unitFactorInUse", conflict.Details!["messageKey"]);

        // Множник лишився тим, під яким посилання комітилося.
        await using var read = sql.CreateContext();
        Assert.Equal(2m, (await read.Units.AsNoTracking().SingleAsync(u => u.Id == unitId)).FactorToBase);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Посилання_що_комітиться_під_час_перевірки_зупиняє_видалення_409_а_не_500()
    {
        var (unitId, otherId) = await ArrangeAsync();

        await using var holderDb = sql.CreateContext();
        await using var holder = await holderDb.Database.BeginTransactionAsync();
        holderDb.UnitConversions.Add(new UnitConversion(unitId, otherId, 0.5m, 0m, 0, null));
        await holderDb.SaveChangesAsync();

        await using var db = sql.CreateContext();
        var delete = Outcome(() => Delete(db, new UnitStore(db)).HandleAsync(unitId, CancellationToken.None));

        await Task.WhenAny(delete, Task.Delay(Window));
        await holder.CommitAsync();

        var conflict = Assert.IsType<ConcurrencyConflictException>(await delete.WaitAsync(Ceiling));
        Assert.Equal("err.ECR-UOM-0409.unitInUse", conflict.Details!["messageKey"]);
        Assert.Equal("1", conflict.Details["total"]);

        await using var read = sql.CreateContext();
        Assert.True(await read.Units.AnyAsync(u => u.Id == unitId));
        Assert.Equal(1, await read.UnitConversions.CountAsync(c => c.FromUnitId == unitId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Посилання_після_перевірки_чекає_видалення_і_не_лишається_висячим()
    {
        var (unitId, otherId) = await ArrangeAsync();

        await using var db = sql.CreateContext();
        var checkedUsage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var referenceDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Видалення перевірило «посилань немає» і стоїть перед записом.
        var store = new PausingUnitStore(new UnitStore(db), checkedUsage, referenceDone.Task);
        var delete = Outcome(() => Delete(db, store).HandleAsync(unitId, CancellationToken.None));

        await ReachedOrFailedAsync(checkedUsage, delete, "видалення перевірило посилання");

        await using var referrerDb = sql.CreateContext();
        referrerDb.UnitConversions.Add(new UnitConversion(unitId, otherId, 0.5m, 0m, 0, null));
        var reference = Outcome(() => referrerDb.SaveChangesAsync());
        _ = reference.ContinueWith(_ => referenceDone.TrySetResult(), TaskScheduler.Default);

        var outcomes = await Task.WhenAll(delete, reference).WaitAsync(Ceiling);

        // Видалення, що вже перевірило, завершується саме (не 500 на FK), а посилання, яке
        // чекало на нього, відхиляє вже база: одиниці, на яку воно вказує, більше немає.
        Assert.Null(outcomes[0]);
        Assert.IsType<DbUpdateException>(outcomes[1]);

        await using var read = sql.CreateContext();
        Assert.False(await read.Units.AnyAsync(u => u.Id == unitId));
        Assert.Equal(0, await read.UnitConversions.CountAsync(c => c.FromUnitId == unitId));
    }

    /// <summary>
    /// ⛔ B186 — причина 1-хв таймауту в CI. Версія одиниці — Base64, а <c>NormalizeETag</c> зрізав <c>W/</c> і з
    /// ГОЛОГО <c>If-Match</c>: для вмісту, чия версія починається з <c>W/</c> (1 з 4096; у гонці вище вміст випадковий
    /// через <c>_tag</c>), перший учасник отримував <c>409 unitChanged</c> ще до паузи, а старий тест чекав паузу всі
    /// 60 с. Тут такий вміст підбирається навмисно: до виправлення — <c>409</c> на кожну зміну такої одиниці.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Голий_If_Match_з_версією_що_починається_з_W_slash_не_дає_409()
    {
        var (unitId, _) = await ArrangeAsync();

        await using (var db = sql.CreateContext())
        {
            var unit = await db.Units.SingleAsync(u => u.Id == unitId);
            for (var i = 0; !UnitVersion.Of(unit).StartsWith("W/", StringComparison.Ordinal); i++)
            {
                Assert.True(i < 1_000_000, "не знайдено вмісту з версією «W/…»");
                unit.Update(new LocalizedText(Text("u")), new LocalizedText(Text($"Weak {i}")), 2m, 0m);
            }

            await db.SaveChangesAsync();
        }

        var version = await VersionAsync(unitId);
        Assert.StartsWith("W/", version, StringComparison.Ordinal);

        await using var updateDb = sql.CreateContext();
        var outcome = await Outcome(() => Update(updateDb, new UnitOfWork(updateDb)).HandleAsync(
            unitId, Text("u"), Text("Strong"), 2m, 0m, version, CancellationToken.None));

        Assert.Null(outcome);
        await using var read = sql.CreateContext();
        Assert.Equal("Strong", (await read.Units.AsNoTracking().SingleAsync(u => u.Id == unitId)).NameL10n.Values["en"]);
    }

    /// <summary>
    /// Чекає, доки учасник гонки дійде до паузи, АЛЕ не довше, ніж він живий.
    /// </summary>
    /// <remarks>
    /// ⛔ B186 (діагностика; причину знайдено — див. тест про голий <c>If-Match</c> з <c>W/</c>). Раніше тут було <c>reached.Task.WaitAsync(Ceiling)</c>: якщо учасник
    /// падав ДО паузи (взаємоблокування SQL, обрив з'єднання, будь-який виняток поза <c>EcrException</c>/<c>DbUpdateException</c>,
    /// що <see cref="Outcome"/> не ковтає), <c>reached</c> не спрацьовував ніколи, а справжня причина лишалася в
    /// незчитаній задачі — тест висів рівно <see cref="Ceiling"/> (1 хв у CI) і падав голим <c>TimeoutException</c>.
    /// Тепер завершення учасника раніше за паузу — негайний збій із його справжнім винятком (<c>await actor</c>
    /// перекидає його) або з його результатом.
    /// </remarks>
    private static async Task ReachedOrFailedAsync(TaskCompletionSource reached, Task<Exception?> actor, string what)
    {
        var first = await Task.WhenAny(reached.Task, actor).WaitAsync(Ceiling);
        if (ReferenceEquals(first, reached.Task))
        {
            return;
        }

        var outcome = await actor;
        throw new Xunit.Sdk.XunitException(
            $"Учасник гонки завершився, не дійшовши до паузи ({what}); результат: {outcome?.ToString() ?? "успіх без паузи"}");
    }

    /// <summary>Дві небазові одиниці однієї розмірності; перша — з множником 2.</summary>
    private async Task<(int UnitId, int OtherId)> ArrangeAsync()
    {
        await using var db = sql.CreateContext();

        var dimension = await db.Dimensions.OrderBy(d => d.Id).FirstAsync();
        var unit = new Unit(
            EcrCode.Create($"c6a{_tag}"), new LocalizedText(Text("u")), new LocalizedText(Text("Unit")),
            dimension.Id, isBase: false, factorToBase: 2m, offsetToBase: 0m);
        var other = new Unit(
            EcrCode.Create($"c6b{_tag}"), new LocalizedText(Text("o")), new LocalizedText(Text("Other")),
            dimension.Id, isBase: false, factorToBase: 4m, offsetToBase: 0m);
        db.Units.AddRange(unit, other);
        await db.SaveChangesAsync();

        return (unit.Id, other.Id);
    }

    private async Task<string> VersionAsync(int unitId)
    {
        await using var db = sql.CreateContext();
        return UnitVersion.Of(await db.Units.AsNoTracking().SingleAsync(u => u.Id == unitId));
    }

    private static UpdateUnitHandler Update(EcrDbContext db, IUnitOfWork uow)
        => new(new UnitStore(db), uow, Access(), User(), Substitute.For<IAuditWriter>(),
               new TestClock(new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc)));

    private static DeleteUnitHandler Delete(EcrDbContext db, IUnitStore store)
        => new(store, new UnitOfWork(db), Access(), User());

    private static IAccessDecisionService Access()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Uom.EditCatalog").Build());
        return access;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        return user;
    }

    private static Dictionary<string, string> Text(string value) => new() { ["en"] = value };

    private static async Task<Exception?> Outcome(Func<Task> action)
    {
        await Task.Yield();
        try
        {
            await action();
            return null;
        }
        catch (Exception ex) when (ex is EcrException or DbUpdateException)
        {
            return ex;
        }
    }

    /// <summary>Одиниця роботи, що перед першим записом спиняється у вікні гонки.</summary>
    private sealed class PausingUnitOfWork(IUnitOfWork inner, TaskCompletionSource reached, Task release)
        : IUnitOfWork
    {
        private bool _paused;

        public async Task<int> SaveChangesAsync(CancellationToken ct)
        {
            if (!_paused)
            {
                _paused = true;
                reached.TrySetResult();
                await Task.WhenAny(release, Task.Delay(Window, ct));
            }

            return await inner.SaveChangesAsync(ct);
        }

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct) => inner.BeginTransactionAsync(ct);

        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
            => inner.ExecuteInTransactionAsync(operation, ct);
    }

    /// <summary>Сховище, що після перевірки посилань спиняється у вікні гонки.</summary>
    private sealed class PausingUnitStore(IUnitStore inner, TaskCompletionSource reached, Task release)
        : IUnitStore
    {
        public Task<Unit?> FindUnitByCodeAsync(string code, CancellationToken ct) => inner.FindUnitByCodeAsync(code, ct);

        public Task<bool> DimensionExistsAsync(byte dimensionId, CancellationToken ct)
            => inner.DimensionExistsAsync(dimensionId, ct);

        public void AddUnit(Unit unit) => inner.AddUnit(unit);

        public Task<Unit?> FindUnitByIdAsync(int unitId, CancellationToken ct) => inner.FindUnitByIdAsync(unitId, ct);

        public Task<Unit?> LockUnitAsync(int unitId, CancellationToken ct) => inner.LockUnitAsync(unitId, ct);

        public Task<IReadOnlyDictionary<int, int>> CountUnitStructuralUsageAsync(CancellationToken ct)
            => inner.CountUnitStructuralUsageAsync(ct);

        public async Task<UsageResponse> FindUnitUsageAsync(int unitId, int take, CancellationToken ct)
            => await PauseAfter(inner.FindUnitUsageAsync(unitId, take, ct), ct);

        public async Task<UsageResponse> FindUnitUsageForUpdateAsync(int unitId, int take, CancellationToken ct)
            => await PauseAfter(inner.FindUnitUsageForUpdateAsync(unitId, take, ct), ct);

        public void RemoveUnit(Unit unit) => inner.RemoveUnit(unit);

        private async Task<UsageResponse> PauseAfter(Task<UsageResponse> usage, CancellationToken ct)
        {
            var result = await usage;
            reached.TrySetResult();
            await Task.WhenAny(release, Task.Delay(Window, ct));
            return result;
        }
    }
}
