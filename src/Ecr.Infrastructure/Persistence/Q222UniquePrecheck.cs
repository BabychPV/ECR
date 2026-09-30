// src/Ecr.Infrastructure/Persistence/Q222UniquePrecheck.cs
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Передперевірка перед <c>Q222MissingForeignKeysAndConstraints</c> (аудит D2):
/// три унікальні індекси, які ця міграція ставить на наявні таблиці, не мають
/// падати на базі з дублікатами «SQL-помилкою 1505 без переліку» і не мають
/// нічого видаляти самі.
/// </summary>
/// <remarks>
/// ⚠ Міграція Q222 (2026-09-10) додає <c>UQ_RoleAssignment_Sid</c>,
/// <c>UQ_RoleAssignment_User</c> і <c>UQ_MethodologyConstant</c>. До неї база
/// цих ключів не тримала, тож майданчик, розгорнутий раніше, міг накопичити
/// дублі. <c>CREATE UNIQUE INDEX</c> тоді падає 1505 — з ключем, але без
/// переліку і посеред міграції.
///
/// ⛔ Дедуплікації немає: який із двох однакових рядків правильний
/// (наприклад, яка з двох констант методології має лишитись), вирішує людина
/// (runbook §8.3), а не міграція. Перевірка лише ЗУПИНЯЄ оновлення ДО зміни
/// схеми і називає порушників.
///
/// ⛔ Застосовані міграції не переписуються — перевірку вставляє
/// <see cref="EcrMigrationsSqlGenerator"/> першою командою SQL цієї міграції
/// (той самий механізм, що <see cref="D148ScalePrecheck"/>). Нової міграції й
/// зміни знімка моделі це не потребує. На базі, де Q222 уже застосовано,
/// ідемпотентний скрипт міграцію не виконує — і перевірку разом із нею
/// (індекси там уже стоять, дублів бути не може).
///
/// ⚠ <c>FOR XML PATH</c>, а не <c>STRING_AGG</c>: підлога сервера — 2016 SP1 (D-101).
/// </remarks>
public static class Q222UniquePrecheck
{
    /// <summary>Номер помилки <c>THROW</c>: «Q222: дублікати під унікальний індекс».</summary>
    public const int ErrorNumber = 50222;

    /// <summary>Скільки груп-порушників показувати на індекс.</summary>
    private const int SampleSize = 10;

    /// <summary>
    /// Перевірка потрібна саме міграції Q222: лише вона створює
    /// <c>UQ_MethodologyConstant</c>.
    /// </summary>
    /// <param name="operations">Операції <c>Up</c> однієї міграції.</param>
    /// <returns><c>true</c> для Q222.</returns>
    public static bool Applies(IEnumerable<MigrationOperation> operations)
        => operations.Any(op => op is CreateIndexOperation { Name: "UQ_MethodologyConstant", Table: "MethodologyConstant" });

    /// <summary>
    /// Індекси, які ставить Q222: назва, таблиця, умова фільтра, ключ
    /// групування і текст групи для переліку.
    /// </summary>
    /// <remarks>
    /// ⚠ Нормалізація <c>UQ_MethodologyConstant</c> ті самі вирази, що в обчислюваних
    /// стовпцях міграції (<c>ISNULL(Category, N'')</c>,
    /// <c>ISNULL(ValidFrom, CONVERT(date, '19000101', 112))</c>): на момент перевірки
    /// самих стовпців ще немає.
    /// </remarks>
    private static readonly (string Index, string Table, string Where, string GroupBy, string Describe)[] Indexes =
    [
        (
            "UQ_RoleAssignment_Sid", "sec.RoleAssignment", "PrincipalSid IS NOT NULL",
            "PrincipalSid, RoleId",
            "N'SID ' + CONVERT(nvarchar(200), PrincipalSid) + N', роль ' + CONVERT(nvarchar(20), RoleId)"
        ),
        (
            "UQ_RoleAssignment_User", "sec.RoleAssignment", "UserId IS NOT NULL",
            "UserId, RoleId",
            "N'користувач ' + CONVERT(nvarchar(20), UserId) + N', роль ' + CONVERT(nvarchar(20), RoleId)"
        ),
        (
            "UQ_MethodologyConstant", "calc.MethodologyConstant", "1 = 1",
            "MethodologyVersionId, Code, ISNULL(Category, N''), ISNULL(ValidFrom, CONVERT(date, '19000101', 112))",
            "N'версія методології ' + CONVERT(nvarchar(20), MethodologyVersionId) + N', код ' + Code"
            + " + N', категорія ' + ISNULL(Category, N'') + N', діє з ' + CONVERT(nvarchar(10), ISNULL(ValidFrom, CONVERT(date, '19000101', 112)), 23)"
        ),
    ];

    /// <summary>T-SQL передперевірки: <c>THROW 50222</c> з переліком, якщо є дублі.</summary>
    /// <remarks>⚠ Оголошено ПІСЛЯ <c>Indexes</c>: статичні ініціалізатори йдуть за порядком тексту.</remarks>
    public static string Sql { get; } = BuildSql();

    private static string BuildSql()
    {
        var sections = string.Concat(Indexes.Select(i => Section(i.Index, i.Table, i.Where, i.GroupBy, i.Describe)));

        return $"""
            DECLARE @q222Report nvarchar(max) = N'';
            DECLARE @q222Count int, @q222Rows nvarchar(max);
            {sections}
            IF @q222Report <> N''
            BEGIN
                DECLARE @q222Message nvarchar(2048) = LEFT(
                      N'Передперевірка Q222: оновлення зупинено ДО зміни схеми. '
                    + N'Міграція Q222MissingForeignKeysAndConstraints додає унікальні індекси, '
                    + N'а в цих таблицях є рядки з однаковим ключем; SQL Server дав би 1505 без переліку. '
                    + N'Дублі (кількість груп; перші групи):' + @q222Report + NCHAR(10)
                    + N'Схему й дані не змінено, автоматичного видалення немає. Що робити: docs/admin/operations-runbook.md, п. 8.3.',
                    2048);
                THROW {ErrorNumber}, @q222Message, 1;
            END
            """;
    }

    private static string Section(string index, string table, string where, string groupBy, string describe)
    {
        var groups = $"""
            FROM {table}
                    WHERE {where}
                    GROUP BY {groupBy}
                    HAVING COUNT(*) > 1
            """;

        return $"""

            SELECT @q222Count = COUNT(*)
            FROM (SELECT 1 AS One {groups}) AS g;

            IF @q222Count > 0
            BEGIN
                SET @q222Rows = STUFF((
                    SELECT TOP ({SampleSize}) N'; (' + {describe} + N') x' + CONVERT(nvarchar(20), COUNT(*))
                    {groups}
                    FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');

                SET @q222Report = @q222Report + NCHAR(10) + N'  {index} ({table}): ' + CONVERT(nvarchar(20), @q222Count)
                    + N' груп; ' + ISNULL(@q222Rows, N'');
            END
            """;
    }
}
