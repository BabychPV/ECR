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
    /// <summary>
    /// ✎ P-1124-3 (D-NEW, людина 24.11 «можеш прибрати, якщо не потрібні»): вʼюху зрізу <c>rpt.v_WaterReport_v1</c> і
    /// таблиці зрізів прибрано міграцією <c>P1124RemoveReportSnapshots</c> — свідома зміна публічного контракту D-53 (запис
    /// у <c>docs/tz/10-decisions.md</c>). Доказ, що нею ніхто не користувався: RDL у репозиторії немає, 166 RDL замовника
    /// у <c>docs/</c> не згадують <c>rpt.*</c>, на live 0 зрізів і 0 рядків, і жодна інша вʼюха чи процедура на неї не
    /// посилається (<c>sys.sql_expression_dependencies</c>, 24.11).
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути блок <c>CREATE OR ALTER VIEW rpt.v_WaterReport_v1</c> у <c>05-rpt-views.sql</c> — скрипт
    /// падає (таблиць зрізів немає); прибрати з міграції <c>DropTable</c> — тест червоніє на таблицях.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Вʼюху_і_таблиці_зрізів_прибрано()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.objects
             WHERE schema_id = SCHEMA_ID(N'rpt')
               AND name IN (N'v_WaterReport_v1', N'ReportDef', N'ReportVersion', N'ReportSnapshot', N'ReportRow');
            """;
        var left = (int)(await command.ExecuteScalarAsync())!;

        Assert.Equal(0, left);
    }

    /// <summary>
    /// Контракт довгої вʼюхи <c>rpt.v_DocumentCells</c>: імена колонок за порядком. Колонки лише ДОДАЮТЬСЯ і лише в кінець
    /// (правило стабільних вʼюх rpt): B138 додала <c>_WorkflowMode</c> (tinyint, режим погодження версії шаблону рядка)
    /// після <c>IsArchived</c> (R-1), а за нею <c>_RecalculatedAfterSubmission</c> (tinyint, людина 16.11) — останньою.
    /// </summary>
    private static readonly string[] DocumentCells =
    [
        "ProjectId", "ProjectCode", "TemplateCode", "TemplateVersion", "DocumentId", "DocumentKey", "PeriodKey",
        "SheetCode", "TableCode", "RowId", "RowKey", "RowOrdinal", "ColumnCode", "DataType",
        "ValueString", "ValueNumeric", "ValueDate", "ValueBool", "ValueRegistryEntryId", "ValueUnitId", "ValueUnitCode",
        "ColumnUnitCode", "IsCalculated", "Status", "SheetCopyOrdinal", "SheetDisplayName", "IsArchived", "_WorkflowMode",
        "_RecalculatedAfterSubmission",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.12")]
    public async Task Контракт_довгої_вʼюхи_колонки_лише_додаються_в_кінець()
    {
        var actual = await ColumnsAsync("rpt", "v_DocumentCells");

        Assert.Equal(DocumentCells, actual.Select(c => c.Name));

        // B138: дві останні колонки — режим погодження версії (cfg.TemplateVersion.WorkflowMode) і позначка «перераховано
        // після подання»; обидві tinyint NOT NULL.
        Assert.Equal(("_WorkflowMode", "tinyint", 1, false), actual[^2]);
        Assert.Equal(("_RecalculatedAfterSubmission", "tinyint", 1, false), actual[^1]);
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
