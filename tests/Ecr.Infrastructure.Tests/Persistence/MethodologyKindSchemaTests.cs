// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyKindSchemaTests.cs
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Схема <c>calc</c> після поправок 2-біс, 6 і 10 директиви ПК-1 №05 — на
/// **реальному** SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Перевірка саме на базі, а не в домені: константи корпусу кладе імпортер
/// масовою вставкою повз сутності, і правило, яке тримає лише застосунок,
/// обходиться одним <c>INSERT</c>. Рівно так у чинній системі й з'явилися три
/// відомі дефекти (<c>'-'</c> двічі та <c>''</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyKindSchemaTests(SqlServerFixture sql)
{
    /// <summary>Код методології, яку тест заводить для власних вставок.</summary>
    private const string Code = "ITEST_KIND";

    /// <summary>Локалізована назва як JSON; вміст неістотний, форма — так.</summary>
    private const string Name = """{"en":"kind probe"}""";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.5")]
    public async Task Текстова_константа_з_одиницею_відхиляється_базою()
    {
        var versionId = await SeedVersionAsync().ConfigureAwait(true);

        // ⛔ Вимір — властивість числа. Текстова константа з одиницею пройшла б
        // перевірку розмірностей при публікації (вона порівнює одиниці, а не
        // значення) і зрівняла б 'Summer' із тоннами без жодного зауваження.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT INTO calc.MethodologyConstant
                (MethodologyVersionId, Code, Kind, [Value], TextValue, UnitId)
            SELECT {versionId}, N'k1_Season_', 1, NULL, N'Summer', u.Id
            FROM uom.Unit u WHERE u.Code = N't'
            """)).ConfigureAwait(true);

        Assert.Contains("CK_MC_Kind", error.Message, StringComparison.Ordinal);

        // ⚠ Межа правила: та сама константа без одиниці лягає без заперечень.
        await ExecuteAsync($"""
            INSERT INTO calc.MethodologyConstant
                (MethodologyVersionId, Code, Kind, [Value], TextValue, UnitId)
            VALUES ({versionId}, N'k1_Season_', 1, NULL, N'Summer', NULL)
            """).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.12")]
    public async Task Нерозібране_число_база_приймає_а_порожню_числову_константу_ні()
    {
        var versionId = await SeedVersionAsync().ConfigureAwait(true);

        // ⛔ Дірка НАВМИСНА і це головне твердження тесту: `'-'` мусить доїхати
        // до публікації, щоб людина ухвалила рішення. Заборонити його тут
        // означало б або тихий нуль у 16 формулах, або обрив імпорту 6507
        // констант на трьох дефектних рядках.
        await ExecuteAsync($"""
            INSERT INTO calc.MethodologyConstant
                (MethodologyVersionId, Code, Kind, [Value], TextValue, UnitId)
            SELECT {versionId}, N'n_ECW_C11_13_', 0, NULL, N'-', u.Id
            FROM uom.Unit u WHERE u.Code = N't'
            """).ConfigureAwait(true);

        // ⚠ А от рядок, у якому немає НІ числа, ні сирого тексту, назвати
        // нічим: він не є ані значенням, ані проблемою, яку можна показати.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT INTO calc.MethodologyConstant
                (MethodologyVersionId, Code, Kind, [Value], TextValue, UnitId)
            SELECT {versionId}, N'k22_HSE30X_Int_FG_', 0, NULL, NULL, u.Id
            FROM uom.Unit u WHERE u.Code = N't'
            """)).ConfigureAwait(true);

        Assert.Contains("CK_MC_Kind", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Ребро_графа_методологій_не_може_бути_петлею()
    {
        var methodologyId = await ScalarAsync<int>($"""
            SELECT TOP 1 Id FROM calc.Methodology WHERE Code = N'{Code}'
            """).ConfigureAwait(true);

        if (methodologyId == 0)
        {
            _ = await SeedVersionAsync().ConfigureAwait(true);
            methodologyId = await ScalarAsync<int>($"""
                SELECT TOP 1 Id FROM calc.Methodology WHERE Code = N'{Code}'
                """).ConfigureAwait(true);
        }

        // ⛔ Петля в графі методологій зупиняє топологічний порядок УСЬОГО
        // перерахунку, а не однієї методології, і виглядає при цьому як
        // «цикл» без жодної підказки, у чому річ.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT INTO calc.MethodologyDependency (FromMethodologyId, ToMethodologyId)
            VALUES ({methodologyId}, {methodologyId})
            """)).ConfigureAwait(true);

        Assert.Contains("CK_MD_NoSelfLoop", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.6")]
    public async Task Текстова_формула_з_одиницею_результату_відхиляється_базою()
    {
        var versionId = await SeedVersionAsync().ConfigureAwait(true);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT INTO calc.MethodologyFormula
                (MethodologyVersionId, Code, Expression, ResultType, OutputUnitId, EvaluationOrder)
            SELECT {versionId}, N'Verdict', N'''Сверхнорматив''', 1, u.Id, 0
            FROM uom.Unit u WHERE u.Code = N't'
            """)).ConfigureAwait(true);

        Assert.Contains("CK_MF_TextHasNoUnit", error.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Заводить методологію і чернетку версії — мінімум, потрібний для FK.
    /// </summary>
    /// <remarks>
    /// ⚠ Кожен тест заводить СВОЮ версію: спільна означала б, що порядок
    /// виконання визначає, чи є в ній уже константа з тим самим кодом
    /// (<c>UQ_MethodologyConstant</c>).
    /// </remarks>
    private async Task<int> SeedVersionAsync()
    {
        await ExecuteAsync($"""
            IF NOT EXISTS (SELECT 1 FROM calc.Methodology WHERE Code = N'{Code}')
                INSERT INTO calc.Methodology (Code, NameL10n, Kind, IsActive)
                VALUES (N'{Code}', N'{Name}', 2, 1)
            """).ConfigureAwait(true);

        return await ScalarAsync<int>($"""
            INSERT INTO calc.MethodologyVersion
                (MethodologyId, Version, Status, [Level], NumericMode, CalendarMode, TraceLevel,
                 CreatedAt, CreatedByUserId)
            OUTPUT INSERTED.Id
            SELECT m.Id, CONCAT(N'1.0.', CAST(ABS(CHECKSUM(NEWID())) % 100000 AS nvarchar(10)), N'.0'),
                   0, 1, 0, 0, 1, SYSUTCDATETIME(), 1
            FROM calc.Methodology m WHERE m.Code = N'{Code}'
            """).ConfigureAwait(true);
    }

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<T?> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }
}
