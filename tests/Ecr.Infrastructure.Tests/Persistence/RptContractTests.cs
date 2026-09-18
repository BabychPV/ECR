using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// `rpt.*` — постійний публічний контракт: імена, типи й порядок колонок
/// закріплені тут поіменно.
/// </summary>
/// <remarks>
/// ⛔ ПРЕДМЕТ. `ФВ-10.12` (`docs/tz/02-requirements.md`) і `D-53`
/// (`docs/tz/10-decisions.md`) оголошують `rpt.*` **постійним публічним
/// контрактом**: стабільні імена і типи, версійність вʼюх, зворотна
/// сумісність. Споживач — SSRS, тобто зовнішня система, яку наш CI не
/// збирає й не запускає. Отже перейменована або зникла колонка ламає звіти
/// замовника, а в нас не червоніє НІЧОГО.
///
/// ⚠ ЧОГО ТУТ НЕМАЄ — і це виправлення мого ж першого висновку. Я спершу
/// вирішив, що `05-rpt-views.sql` не виконує ЖОДЕН тест; це неправда.
/// `ReportSnapshotBuilderTests.DeployViewAsync()` накочував його сам,
/// свідомо й із поясненням («`SqlServerFixture` цей скрипт не накочує сама»).
/// Тобто вʼюха в тестовій базі була. Чого не було — це перевірки ІМЕН І
/// ТИПІВ: жоден тест не звіряв контракт, тож перейменування колонки ламало
/// звіти замовника й не червонило нічого.
///
/// ⛔ Обхід у тому тесті тепер зайвий: скрипт переїхав у `SqlServerFixture`,
/// у ту саму позицію, що й у `tools/setup-dev-db.ps1`. Розбіжність між тим,
/// що створює розгортання, і тим, що створює фікстура, — це зазор, у якому
/// дефект живе до самого проду; у `setup-dev-db.ps1` проти такого зазору вже
/// стоїть окремий сторож, а у фікстури його не було.
///
/// ⚠ Перевірка по РОЗГОРНУТІЙ базі (`sys.columns`), а не по тексту `.sql`:
/// контракт для споживача — це те, що віддає сервер, а не те, що написано в
/// скрипті. Тип колонки вʼюхи виводиться з типів джерел, тож зміна
/// `ReportRow.ValueString` з `nvarchar(400)` на `nvarchar(200)` мовчки
/// звузить контракт, не змінивши в `05-rpt-views.sql` жодного символу.
///
/// ⚠ Очікуване — СПИСКОМ У КОДІ, не знімком у файлі. Вʼюха одна, і зміна
/// контракту мусить бути свідомою правкою ТУТ, поряд із причиною, а не
/// перегенерованим файлом, який легко оновити не читаючи. Якщо вʼюх стане
/// багато — це привід перейти на знімок у `contracts/`, як
/// `openapi.snapshot.json`, і тоді ж переписати цей абзац.
/// </remarks>
[Collection("SqlServer")]
public sealed class RptContractTests(SqlServerFixture sql)
{
    /// <summary>Контракт <c>rpt.v_WaterReport_v1</c>: порядок, ім'я, тип, довжина, nullability.</summary>
    private static readonly (int Ordinal, string Name, string Type, int MaxLength, bool IsNullable)[] WaterReportV1 =
    [
        (1, "ProjectId", "int", 4, false),
        (2, "PeriodKey", "int", 4, true),
        (3, "RowNo", "int", 4, false),
        (4, "ColumnCode", "nvarchar", 128, false),
        (5, "ValueString", "nvarchar", 2000, true),
        (6, "ValueNumeric", "decimal", 13, true),
        (7, "ValueDate", "datetime2", 7, true),
        (8, "BuiltAt", "datetime2", 7, false),
        (9, "Status", "tinyint", 1, false),
    ];

    /*
     * ⚠ Числа вище зняті з РОЗГОРНУТОЇ бази, не виведені з голови: перша
     * спроба вгадати їх дала розбіжність уже на другій колонці. `max_length`
     * для `nvarchar` — у БАЙТАХ (2000 = 1000 символів), для `decimal` це
     * внутрішній розмір (13), а не точність.
     *
     * ⚠ Дві речі, які тест ФІКСУЄ як є, і жодна з них не виправляється тут,
     * бо виправлення = зміна публічного контракту:
     *   — `PeriodKey` виходить NULLABLE, хоча в джерелі ключ: тип колонки
     *     вʼюхи виводить сервер, і крізь з'єднання він не доводить
     *     обов'язковість;
     *   — `ValueDate` має тип `datetime2`, а не `date`, попри ім'я — тобто
     *     несе ще й час. Споживач (SSRS) уже бачить саме це.
     * Обидві — кандидати на `v2`, якщо замовник погодиться; мовчки міняти їх
     * не можна, і саме це й стереже цей тест.
     */

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Контракт_вʼюхи_звіту_не_змінився()
    {
        var actual = await ColumnsAsync("rpt", "v_WaterReport_v1");

        // ⛔ Без цього перейменована чи видалена вʼюха дала б ПОРОЖНІЙ список
        // і зелений тест — тобто найгірший різновид сторожа.
        Assert.True(
            actual.Count > 0,
            "Вʼюхи rpt.v_WaterReport_v1 у базі немає взагалі. Якщо її свідомо "
            + "прибрали — це зміна публічного контракту (ФВ-10.12, D-53), і "
            + "вона мусить бути описана тут разом із версією, що прийшла на заміну.");

        Assert.Equal(WaterReportV1.Length, actual.Count);

        foreach (var (ordinal, name, type, maxLength, isNullable) in WaterReportV1)
        {
            var column = actual[ordinal - 1];

            Assert.True(
                column.Name == name
                && column.Type == type
                && column.MaxLength == maxLength
                && column.IsNullable == isNullable,
                $"Колонка {ordinal} вʼюхи rpt.v_WaterReport_v1 розійшлася з контрактом."
                + Environment.NewLine
                + $"  очікували: {name} {type}({maxLength}) null={isNullable}"
                + Environment.NewLine
                + $"  у базі:    {column.Name} {column.Type}({column.MaxLength}) null={column.IsNullable}"
                + Environment.NewLine
                + "ФВ-10.12 і D-53: rpt.* — постійний публічний контракт, споживач "
                + "SSRS. Зміна імені чи типу ламає звіти замовника мовчки. Потрібна "
                + "зміна — заведи НОВУ версію вʼюхи (v2) поряд зі старою і онови "
                + "перелік у цьому тесті; правка на місці зворотної сумісності не дає.");
        }
    }

    private async Task<IReadOnlyList<(string Name, string Type, int MaxLength, bool IsNullable)>> ColumnsAsync(
        string schema,
        string view)
    {
        await using var db = sql.CreateContext();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();

        // ⚠ `ORDER BY c.column_id` — порядок колонок теж частина контракту:
        // SSRS-звіт, побудований на позиціях, зламається від перестановки так
        // само, як від перейменування.
        await using var command = new SqlCommand(
            """
            SELECT c.name, t.name AS type_name, c.max_length, c.is_nullable
            FROM sys.columns c
            JOIN sys.views  v ON v.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = v.schema_id
            JOIN sys.types  t ON t.user_type_id = c.user_type_id
            WHERE s.name = @schema AND v.name = @view
            ORDER BY c.column_id;
            """,
            connection);

        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@view", view);

        var rows = new List<(string, string, int, bool)>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt16(2), reader.GetBoolean(3)));
        }

        return rows;
    }
}
