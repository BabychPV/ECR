using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>WR-06</c>: <c>MERGE</c> не переписує рядок, у якому нічого не змінилося.
/// </summary>
/// <remarks>
/// ⛔ Доказ тут — <c>@@ROWCOUNT</c> бойового запиту
/// (<see cref="NormalizedCellStore.LastUpsertRowsAffected"/>), а не «тест не
/// впав». Різниця принципова: безумовний <c>WHEN MATCHED</c> лишає ЗЕЛЕНИМ
/// будь-який тест, що перевіряє лише значення в базі, — значення ж однакове.
/// Видно тільки те, скільки рядків СУБД реально змінила.
///
/// ⛔ Друга половина класу — не про швидкість, а про те, щоб оптимізація не
/// стала тихим загубленим оновленням. Колація бази регістронечутлива
/// (<c>Latin1_General_100_CI_AS_SC</c>), а кінцеві пробіли ігноруються при
/// порівнянні <c>nvarchar</c> у будь-якій колації. Наївне «якщо однакове —
/// пропустити» означало б, що правки «київ» → «Київ» і <c>"a"</c> → <c>"a "</c>
/// мовчки не доїжджають до бази, тоді як аудит стверджує, що доїхали. Ці два
/// тести падають на наївній реалізації і проходять на нинішній.
/// </remarks>
[Collection("SqlServer")]
public sealed class UnchangedCellMergeTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторний_запис_тих_самих_значень_не_змінює_жодного_рядка()
    {
        var (doc, store) = await ArrangeAsync();
        var ct = CancellationToken.None;
        var value = new CellValueData { ValueNumeric = 42.5m, ValueString = "Київ" };

        await store.ApplyAsync(Set(doc, value), ct);

        // Контроль: перший запис — це ВСТАВКА, і вона мусить дати 1. Без цього
        // рядка «0 на другому записі» могло б означати, що MERGE не працює
        // взагалі.
        Assert.Equal(1, store.LastUpsertRowsAffected);

        await store.ApplyAsync(Set(doc, value), ct);

        Assert.Equal(0, store.LastUpsertRowsAffected);

        // І значення на місці: «нічого не змінили» не означає «нічого немає».
        var read = await store.ReadCellsAsync([Address(doc)], ct);
        Assert.Equal(42.5m, read[Address(doc)].ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Змінене_число_оновлюється()
    {
        var (doc, store) = await ArrangeAsync();
        var ct = CancellationToken.None;

        await store.ApplyAsync(Set(doc, new CellValueData { ValueNumeric = 42.5m }), ct);
        await store.ApplyAsync(Set(doc, new CellValueData { ValueNumeric = 42.5000000001m }), ct);

        // ⚠ Різниця в десятому знаку — усередині decimal(28,16). Округли
        // порівняння до дев'ятого, і правка мовчки зникла б.
        Assert.Equal(1, store.LastUpsertRowsAffected);
        var read = await store.ReadCellsAsync([Address(doc)], ct);
        Assert.Equal(42.5000000001m, read[Address(doc)].ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_лише_регістру_рядка_доїжджає_до_бази()
    {
        // ⛔ Найдорожчий із можливих тут дефектів: порівняння в колації бази
        // вважає «київ» і «Київ» однаковими, тож наївний WR-06 пропустив би
        // UPDATE. Користувач побачив би 200, аудит — перехід, а в базі лишився
        // б старий рядок.
        var (doc, store) = await ArrangeAsync();
        var ct = CancellationToken.None;

        await store.ApplyAsync(Set(doc, new CellValueData { ValueString = "київ" }), ct);
        await store.ApplyAsync(Set(doc, new CellValueData { ValueString = "Київ" }), ct);

        Assert.Equal(1, store.LastUpsertRowsAffected);

        // ⚠ Читання через ReadCellsAsync тут НЕ доказ саме по собі: воно
        // повертає те, що в базі, і саме тому твердження про рядок іде поруч
        // із твердженням про @@ROWCOUNT, а не замість нього.
        var read = await store.ReadCellsAsync([Address(doc)], ct);
        Assert.Equal("Київ", read[Address(doc)].ValueString);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_лише_кінцевого_пробілу_доїжджає_до_бази()
    {
        // ⛔ Той самий клас, інший механізм: кінцеві пробіли ігнорує вже саме
        // порівняння nvarchar, і двійкова колація від цього НЕ рятує
        // (перевірено запитом: N'a' = N'a ' під Latin1_General_BIN2 — істина).
        // Ловить різницю тільки DATALENGTH.
        var (doc, store) = await ArrangeAsync();
        var ct = CancellationToken.None;

        await store.ApplyAsync(Set(doc, new CellValueData { ValueString = "10" }), ct);
        await store.ApplyAsync(Set(doc, new CellValueData { ValueString = "10 " }), ct);

        Assert.Equal(1, store.LastUpsertRowsAffected);

        var read = await store.ReadCellsAsync([Address(doc)], ct);
        Assert.Equal("10 ", read[Address(doc)].ValueString);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перехід_між_значенням_і_явною_порожнечею_видно_в_обидва_боки()
    {
        var (doc, store) = await ArrangeAsync();
        var ct = CancellationToken.None;

        await store.ApplyAsync(Set(doc, new CellValueData { ValueNumeric = 1m }), ct);

        // Значення → явна порожнеча: зміна, і її не можна пропустити.
        await store.ApplyAsync(Set(doc, CellValueData.Empty), ct);
        Assert.Equal(1, store.LastUpsertRowsAffected);

        // Порожнеча → та сама порожнеча: NULL = NULL у EXCEPT це ЗБІГ, і саме
        // так і треба — переписувати порожню комірку порожньою немає чим.
        await store.ApplyAsync(Set(doc, CellValueData.Empty), ct);
        Assert.Equal(0, store.LastUpsertRowsAffected);

        // Порожнеча → значення: знову зміна.
        await store.ApplyAsync(Set(doc, new CellValueData { ValueNumeric = 1m }), ct);
        Assert.Equal(1, store.LastUpsertRowsAffected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Змінена_мілісекунда_дати_оновлюється()
    {
        var (doc, store) = await ArrangeAsync();
        var ct = CancellationToken.None;

        // ⛔ Дата з ХВОСТОМ ТИКІВ дрібнішим за мілісекунду, а не рівна
        // мілісекунда. Саме такою її дає DateTime.UtcNow, тобто саме така
        // приходить із продукту. Стовпець datetime2(3) зберігає округлене до
        // мілісекунд, тож якби параметр їхав зі стандартним масштабом 7,
        // порівняння «збережене проти надісланого» не збігалося б НІКОЛИ, і
        // наступне твердження (0) було б хибним: WR-06 виглядав би справним і
        // не робив нічого.
        var at = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

        await store.ApplyAsync(Set(doc, new CellValueData { ValueDate = at }), ct);
        await store.ApplyAsync(Set(doc, new CellValueData { ValueDate = at }), ct);
        Assert.Equal(0, store.LastUpsertRowsAffected);

        // Одна мілісекунда — межа роздільності datetime2(3): менше вже не
        // зміна, більше — вже зміна.
        await store.ApplyAsync(Set(doc, new CellValueData { ValueDate = at.AddMilliseconds(1) }), ct);
        Assert.Equal(1, store.LastUpsertRowsAffected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Із_цілого_зрізу_оновлюються_лише_змінені_комірки()
    {
        // Наскрізний випадок, заради якого рядок плану й існує: сітка шле весь
        // зріз, а не лише правку. Тридцять комірок, змінена одна — СУБД мусить
        // торкнутися однієї.
        var (doc, store) = await ArrangeAsync(rowCount: 10);
        var ct = CancellationToken.None;

        var first = Slice(doc, offset: 0);
        await store.ApplyAsync(first, ct);
        Assert.Equal(first.Upserts.Count, store.LastUpsertRowsAffected);

        await store.ApplyAsync(Slice(doc, offset: 0), ct);
        Assert.Equal(0, store.LastUpsertRowsAffected);

        await store.ApplyAsync(Slice(doc, offset: 0, changeAt: 3), ct);
        Assert.Equal(1, store.LastUpsertRowsAffected);
    }

    /// <summary>Батч на весь зріз таблиці; <paramref name="changeAt"/> — єдина правка.</summary>
    private static CellChangeSet Slice(TestDocument doc, int offset, int? changeAt = null)
    {
        var records = new List<CellRecord>();
        var index = 0;

        foreach (var rowId in doc.RowIds)
        {
            foreach (var columnId in doc.ColumnDefIds)
            {
                var value = offset + index + (index == changeAt ? 1000 : 0);
                records.Add(new CellRecord(
                    new CellAddress(doc.PeriodKey, rowId, columnId),
                    doc.TableDefId,
                    new CellValueData { ValueNumeric = value }));
                index++;
            }
        }

        return new CellChangeSet(
            doc.TableInstanceId, records, [], doc.RowIds, ChangedByUserId: 1, IsLateEdit: false);
    }

    private static CellAddress Address(TestDocument doc)
        => new(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);

    private static CellChangeSet Set(TestDocument doc, CellValueData value)
        => new(
            doc.TableInstanceId,
            [new CellRecord(Address(doc), doc.TableDefId, value)],
            [],
            [doc.RowIds[0]],
            ChangedByUserId: 1,
            IsLateEdit: false);

    private async Task<(TestDocument Document, NormalizedCellStore Store)> ArrangeAsync(int rowCount = 4)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: rowCount, ct: CancellationToken.None);
        return (doc, new NormalizedCellStore(builder.CreateContext()));
    }
}
