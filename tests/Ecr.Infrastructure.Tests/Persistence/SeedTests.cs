using Ecr.Infrastructure.Persistence;
using Ecr.Application.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Seed: ідемпотентність і повнота.</summary>
[Collection("SqlServer")]
public sealed class SeedTests(SqlServerFixture sql)
{
    /// <summary>Каталог прав із <c>02a-db-schema.md</c> §17.</summary>
    /// <remarks>
    /// Числа зашиті навмисно: якщо хтось додасть право в seed і не додасть
    /// сюди, тест впаде — і це правильно. Право, якого немає в цьому списку,
    /// ніхто не перевіряв.
    /// </remarks>
    private const int ExpectedPermissions = 38;

    private const int ExpectedDangerous = 8;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.17")]
    public async Task Повторний_запуск_не_створює_дублікатів()
    {
        var before = await CountsAsync();

        // Seed уже виконано фікстурою; запускаємо ВДРУГЕ на тій самій базі.
        await using (var db = CreateContext())
        {
            await new SeedRunner(db).RunAsync(CancellationToken.None);
        }

        var after = await CountsAsync();

        // MERGE … WHEN NOT MATCHED — не оптимізація, а умова перезапуску:
        // розгортання переграється, і другий прогін не має подвоїти каталог.
        Assert.Equal(before, after);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9")]
    [Trait("Requirement", "ФВ-2.2")]
    public async Task Створюються_три_мови_і_рівно_одна_за_замовчуванням()
    {
        Assert.Equal(3, await ScalarAsync("SELECT COUNT(*) FROM sys_ecr.Language"));

        // «Рівно одна» тримається фільтрованим індексом UX_Language_Default,
        // а не домовленістю: дві мови за замовчуванням зробили б підміну
        // за ФВ-14.9 недетермінованою.
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM sys_ecr.Language WHERE IsDefault = 1"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9")]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Створюються_усі_права_з_каталогу()
    {
        Assert.Equal(ExpectedPermissions, await ScalarAsync("SELECT COUNT(*) FROM sec.Permission"));
        Assert.Equal(ExpectedDangerous,
            await ScalarAsync("SELECT COUNT(*) FROM sec.Permission WHERE IsDangerous = 1"));

        // Права оголошує код, і саме він їх перевіряє: право, якого немає в
        // каталозі, не можна ні видати, ні перевірити.
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.Permission WHERE Code = N'Period.Reopen' AND IsDangerous = 1"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Небезпечні_права_не_потрапляють_у_вбудовані_ролі_автоматично()
    {
        // ⛔ Жодна СКЛАДЕНА вбудована роль не отримує небезпечного права з
        // seed — навіть SystemAdministrator (ФВ-6.12, D-40). Право на
        // симуляцію або відкриття періоду, видане розгортанням, не має автора
        // в аудиті — а саме автор й потрібен, коли потім з'ясовують, звідки
        // взялася можливість.
        Assert.Equal(0, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role       AS r ON r.Id   = rp.RoleId AND r.IsBuiltIn = 1
            JOIN sec.Permission AS p ON p.Code = rp.PermissionCode
            WHERE p.IsDangerous = 1
              AND r.Code <> N'{BootstrapAdmin.RoleCode}'
            """));

        // ⚠ Виняток рівно один і названий. Він не послаблення правила, а його
        // умова: у щойно розгорнутій системі небезпечних прав не має ніхто,
        // тому без цієї ролі їх ніхто й ніколи не видасть уперше (`A7-17`).
        // Носій — bootstrap-запис: один на систему, з обов'язковою зміною
        // пароля, вимикається появою доменного адміністратора (ФВ-6.18).
        Assert.Equal(2, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'{BootstrapAdmin.RoleCode}'
            """));

        // ⛔ І рівно ДВА — керування користувачами й ролями. Роль первинного
        // налаштування передає систему людям; вона не рахує, не публікує і не
        // дивиться чужими очима.
        Assert.Equal(0, await ScalarAsync($"""
            SELECT COUNT(*)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId
            WHERE r.Code = N'{BootstrapAdmin.RoleCode}'
              AND rp.PermissionCode NOT IN (N'Security.ManageUsers', N'Security.ManageRoles')
            """));

        // ⚠ І водночас ролі НЕ порожні: роль без жодного права виглядає
        // як робоча конфігурація і мовчки не працює — це той самий клас
        // дефекту, що й «робота, якої ніхто не робить».
        Assert.Equal(8, await ScalarAsync("""
            SELECT COUNT(DISTINCT rp.RoleId)
            FROM sec.RolePermission AS rp
            JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1
            """));

        // Симуляція — найпоказовіший випадок: вона дає чужі очі, а отже, чужі
        // дані, і видаватися має поіменно. Її не має НІХТО, включно з роллю
        // первинного налаштування.
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM sec.RolePermission WHERE PermissionCode = N'Security.Simulate'"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.2")]
    public async Task Кожна_розмірність_має_рівно_одну_базову_одиницю()
    {
        // Базові розмірності (1..7) мають базову одиницю; похідні (8..11) —
        // ні, бо складаються з чисельника і знаменника.
        Assert.Equal(7, await ScalarAsync("SELECT COUNT(*) FROM uom.Unit WHERE IsBase = 1"));
        Assert.Equal(7, await ScalarAsync("SELECT COUNT(*) FROM uom.Dimension WHERE BaseUnitId IS NOT NULL"));

        // Двох базових в одній розмірності бути не може — це тримає
        // фільтрований унікальний індекс UX_Unit_BasePerDimension, тобто
        // два різні «нулі» для перетворень фізично неможливі (ФВ-16.2).
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM (SELECT DimensionId FROM uom.Unit WHERE IsBase = 1 " +
            "GROUP BY DimensionId HAVING COUNT(*) > 1) AS d"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.2")]
    public async Task Похідні_одиниці_посилаються_на_чисельник_і_знаменник()
    {
        // Складаються ПОСИЛАННЯМИ, а не розбором рядка «g_per_s» (ФВ-16.2):
        // розбір коду означав би, що перейменування одиниці ламає конверсію.
        Assert.Equal(6, await ScalarAsync(
            "SELECT COUNT(*) FROM uom.Unit WHERE NumeratorUnitId IS NOT NULL AND DenominatorUnitId IS NOT NULL"));

        // Половина посилання — це не похідна одиниця, а зіпсований запис.
        // (У T-SQL булеве не є значенням, тому порівнюємо через CASE.)
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM uom.Unit " +
            "WHERE CASE WHEN NumeratorUnitId IS NULL THEN 1 ELSE 0 END " +
            "   <> CASE WHEN DenominatorUnitId IS NULL THEN 1 ELSE 0 END"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.2")]
    public async Task Множники_базових_одиниць_відповідають_фікстурі()
    {
        // ⚠ FactorToBase наявних одиниць МІНЯТИ ЗАБОРОНЕНО: на них спираються
        // фікстури і тести конверсій. Тест саме про це і стоїть.
        Assert.Equal(1000m, await DecimalAsync("SELECT FactorToBase FROM uom.Unit WHERE Code = N't'"));
        Assert.Equal(0.001m, await DecimalAsync("SELECT FactorToBase FROM uom.Unit WHERE Code = N'g'"));
        Assert.Equal(3600m, await DecimalAsync("SELECT FactorToBase FROM uom.Unit WHERE Code = N'h'"));
        Assert.Equal(31536000m, await DecimalAsync("SELECT FactorToBase FROM uom.Unit WHERE Code = N'year'"));

        // Градус Цельсія — єдина одиниця зі зсувом: перетворення температури
        // не є множенням, і саме тому OffsetToBase взагалі існує.
        Assert.Equal(273.15m, await DecimalAsync("SELECT OffsetToBase FROM uom.Unit WHERE Code = N'degC'"));
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);

    private async Task<string> CountsAsync()
    {
        var parts = new List<string>();
        foreach (var table in (string[])
                 ["sys_ecr.Language", "sec.Permission", "sec.Role", "sec.PasswordPolicy",
                  "uom.Dimension", "uom.Unit", "doc.PeriodPolicy", "sys_ecr.UiString"])
        {
            parts.Add($"{table}={await ScalarAsync($"SELECT COUNT(*) FROM {table}")}");
        }

        return string.Join(", ", parts);
    }

    private async Task<int> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<decimal> DecimalAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (decimal)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }
}
