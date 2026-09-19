using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>BE-06</c>: <c>IAuditReader.ReadLastChangesAsync</c> проти живого
/// <c>aud.CellChange</c> — хто і коли змінив комірку востаннє.
/// </summary>
/// <remarks>
/// ⛔ Чому проти справжньої СУБД, а не на підміні. Предмет цього читання —
/// ОСТАННІЙ рядок за <c>ChangedAt DESC</c> у межах вікна, з приєднаним іменем
/// автора з іншої схеми. Підміна повернула б те, що їй сказали повернути, і
/// довела б рівно нуль: і «останній», і «у межах вікна», і «ім'я, а не логін»
/// — це властивості ЗАПИТУ, а не коду навколо нього.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditReaderLastChangesTests(SqlServerFixture sql)
{
    /// <summary>Відображуване ім'я автора — те, що бачить людина в діалозі.</summary>
    private const string DisplayName = "A. Serikbayev";

    /// <summary>Логін того самого автора — те, чого бачити не має ніхто (R-A2, D-86).</summary>
    private const string UserName = "aserikbayev";

    private static readonly DateTime Older = new(2026, 2, 10, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 2, 10, 8, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowStart = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-06")]
    public async Task Повертається_остання_зміна_комірки_з_іменем_автора()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var userId = await CreateUserAsync(UserName, DisplayName);

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync(
            [
                Change(doc, Older, userId, "5", "UserEdit"),
                Change(doc, Newer, userId, "12.40", "UserEdit"),
            ],
            CancellationToken.None);

        var last = await new AuditReader(db).ReadLastChangesAsync(
            doc.DocumentId, [(doc.RowIds[0], doc.ColumnDefIds[1])], WindowStart, CancellationToken.None);

        var change = Assert.Contains((doc.RowIds[0], doc.ColumnDefIds[1]), last);

        // ⛔ ОСТАННЯ, а не перша-ліпша: `ORDER BY ChangedAt DESC` + `TOP (1)`.
        Assert.Equal(Newer, change.ChangedAt);
        Assert.Equal(userId, change.ChangedByUserId);
        Assert.Equal("UserEdit", change.Origin);

        // ⛔ Мутація, від якої падає рівно цей рядок: `u.DisplayName` →
        // `u.UserName` у `AuditReader.LastChangesSql`. Логін виглядає як
        // відповідь на питання «хто», нею не бувши.
        Assert.Equal(DisplayName, change.ChangedByDisplayName);
        Assert.NotEqual(UserName, change.ChangedByDisplayName);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-06")]
    public async Task Зміна_старша_за_вікно_не_повертається()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var userId = await CreateUserAsync(UserName + "2", DisplayName);

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync(
            [Change(doc, Older, userId, "5", "UserEdit")], CancellationToken.None);

        // Вікно починається ПІСЛЯ зміни.
        var last = await new AuditReader(db).ReadLastChangesAsync(
            doc.DocumentId, [(doc.RowIds[0], doc.ColumnDefIds[1])],
            Older.AddMinutes(1), CancellationToken.None);

        // ⛔ Порожньо, а не «остання з тих, що є». Вікно — не оптимізація, а
        // межа твердження: поза ним читач не обіцяє нічого, і віддати звідти
        // рядок означало б обійти саму причину, з якої вікно обов'язкове.
        Assert.Empty(last);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-06")]
    public async Task Кілька_комірок_читаються_одним_викликом_і_не_плутаються()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var human = await CreateUserAsync(UserName + "3", DisplayName);

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync(
            [
                Change(doc, Newer, human, "12.40", "UserEdit"),
                Change(doc, Newer, human, "77", "Recalculation", rowIndex: 1),
            ],
            CancellationToken.None);

        var last = await new AuditReader(db).ReadLastChangesAsync(
            doc.DocumentId,
            [(doc.RowIds[0], doc.ColumnDefIds[1]), (doc.RowIds[1], doc.ColumnDefIds[1])],
            WindowStart,
            CancellationToken.None);

        Assert.Equal(2, last.Count);

        // ⚠ Походження їде ЯК Є: рішення «це система, а не людина» ухвалює
        // обробник (`PatchCellsHandler`), а читач журналу не має права
        // підмінювати факт тлумаченням.
        Assert.Equal("UserEdit", last[(doc.RowIds[0], doc.ColumnDefIds[1])].Origin);
        Assert.Equal("Recalculation", last[(doc.RowIds[1], doc.ColumnDefIds[1])].Origin);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-06")]
    public async Task Автора_якого_вже_немає_не_вигадують()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync(
            [Change(doc, Newer, userId: 2_000_000_001, "12.40", "UserEdit")], CancellationToken.None);

        var last = await new AuditReader(db).ReadLastChangesAsync(
            doc.DocumentId, [(doc.RowIds[0], doc.ColumnDefIds[1])], WindowStart, CancellationToken.None);

        var change = Assert.Contains((doc.RowIds[0], doc.ColumnDefIds[1]), last);

        // ⛔ `LEFT JOIN`, не `INNER`: запису користувача немає, але ЗМІНА була —
        // і факт зміни не має зникати разом з обліковим записом. Ім'я — `null`,
        // а не порожній рядок: «невідомо хто» і «змінив ніхто» — різні
        // твердження.
        Assert.Null(change.ChangedByDisplayName);
        Assert.Equal(Newer, change.ChangedAt);
    }

    /// <summary>Один запис журналу про комірку тестового документа.</summary>
    private static CellChangeRecord Change(
        TestDocument doc, DateTime at, int userId, string newValue, string origin, int rowIndex = 0)
        => new(
            at,
            new CellAddress(doc.PeriodKey, doc.RowIds[rowIndex], doc.ColumnDefIds[1]),
            doc.DocumentId,
            $"R{(rowIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            OldValue: null,
            NewValue: newValue,
            ChangedByUserId: userId,
            Origin: origin,
            IsLateEdit: false,
            CorrelationId: null);

    /// <summary>
    /// Заводить користувача напряму в <c>sec.User</c> і повертає його <c>Id</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Провайдер <c>Windows</c> (<c>0</c>) з <c>WindowsSid</c> і без
    /// <c>PasswordHash</c> — рівно те, чого вимагає <c>CK_User_Provider</c>.
    /// Пароля цьому запису не треба: він тут не входить у систему, а лише дає
    /// журналу автора з іменем.
    /// </remarks>
    private async Task<int> CreateUserAsync(string userName, string displayName)
    {
        var unique = Guid.NewGuid().ToString("N");

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sec.[User]
                (UserName, DisplayName, Provider, WindowsSid, SecurityStamp, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@userName, @displayName, 0, @sid, @stamp, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@userName", $"{userName}-{unique}");
        command.Parameters.AddWithValue("@displayName", displayName);
        command.Parameters.AddWithValue("@sid", $"S-1-5-21-{unique}");
        command.Parameters.AddWithValue("@stamp", unique);

        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }
}
