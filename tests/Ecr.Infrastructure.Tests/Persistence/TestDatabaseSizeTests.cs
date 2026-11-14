using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Тестова база не має права народжуватися продуктивного розміру.
/// </summary>
/// <remarks>
/// ⛔ Сторож з'явився заднім числом, і рахунок відомий точно: **152 ГБ**.
/// <c>01-filegroups.sql</c> зменшував файли лише на Express
/// (<c>EngineEdition = 4</c>), а замовник ухвалив ставити локально
/// **Developer Edition** (<c>H-19</c>) — у неї <c>EngineEdition = 3</c>, та
/// сама, що в Enterprise. Перевірка мовчала, і кожна тестова база з кількома
/// сотнями рядків отримувала 4096 + 4096 + 4096 + 2048 МБ. Сімнадцять баз
/// заповнили диск, і робота стала з помилкою «MODIFY FILE encountered
/// operating system error 112».
///
/// ⚠ Найоманливіше було ім'я інстансу: він називається <c>SQLEXPRESS</c>,
/// тобто сервер сам казав «Express» — а виданням не був. Саме тому сторож
/// питає РОЗМІР ФАЙЛІВ, а не видання і не наявність позначки: перевіряти те,
/// що ми щойно налаштували, означало б повторити ту саму помилку — питати
/// ознаку замість наслідку.
///
/// ⚠ Симптом був не «диск повний», а хвиля падінь SQL-тестів у всіх збірках
/// одразу. Шукати причину починають у власній правці.
/// </remarks>
[Collection("SqlServer")]
public sealed class TestDatabaseSizeTests(SqlServerFixture sql)
{
    /// <summary>
    /// Стеля одного файла тестової бази, МБ.
    /// </summary>
    /// <remarks>
    /// Скрипт створює файли по 64 МБ і дає їм рости по 64 МБ. Стеля вдвічі
    /// вища за початковий розмір: один приріст під час прогону — нормальна
    /// робота, а не привід до червоного. Продуктивний файл (2048 МБ) від цієї
    /// межі відрізняється на порядок, тож сплутати їх неможливо.
    ///
    /// ⛔ Журнал упритул до стелі — НЕ привід її піднімати. 2026-09-29 журнал
    /// спільної бази стояв на 104–120 МБ, і причиною була одна транзакція на
    /// 100 001 рядок (засів і прибирання в <c>CollectionStoreRawPointUpsertTests</c>):
    /// ~53 МБ на INSERT і ~85 МБ на DELETE разом із резервом під відкат, а
    /// транзакцію посередині не звільняє ні CHECKPOINT, ні модель відновлення
    /// (RECOVERY SIMPLE перевірено — той самий пік). Порціями з CHECKPOINT —
    /// 56–72 МБ. Велика заливка в тесті — порціями, як там і в
    /// <c>CollectionStoreCoverageWindowTests</c>.
    /// </remarks>
    private const int MaxFileMb = SqlServerFixture.MaxFileMb;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Жоден_файл_тестової_бази_не_має_продуктивного_розміру()
    {
        // ⛔ B117: розмір НАРОДЖЕННЯ (після схеми й сіду, до першого тесту), а не живої спільної бази. Раніше вердикт
        // залежав від порядку класів: повний локальний прогін Persistence+Jobs давав файл даних «= 136 МБ», бо хтось до
        // сторожа вже виростив файл, а той самий код окремо був зелений. Ріст за прогін — сторож наприкінці колекції
        // (`SqlServerFixture.DisposeAsync`, D-375), без залежності від порядку.
        Assert.NotEmpty(sql.FileSizesAtBirthMb);
        var oversized = sql.FileSizesAtBirthMb
            .Where(f => f.Value > MaxFileMb)
            .OrderByDescending(f => f.Value)
            .Select(f => $"{f.Key} = {f.Value} МБ")
            .ToList();

        // ⛔ Повідомлення несе ІМЕНА і РОЗМІРИ: «якийсь файл завеликий» не
        // сказало б, чи це файлова група з `01-filegroups.sql`, чи журнал, що
        // розрісся від довгої транзакції, — а лікуються вони по-різному.
        Assert.Empty(oversized);
    }

    /// <summary>
    /// Z8-03: ріст ЗА ПРОГІН (журнал, що розрісся від будь-якого класу) стереже <c>SqlServerFixture.DisposeAsync</c>
    /// наприкінці колекції; сторожі вище (B117) — лише розмір народження, від порядку класів не залежать.
    /// Тут — що спільна функція справді бачить завеликі файли (стеля 0 МБ — завжди є) і мовчить на великій стелі;
    /// що <c>DisposeAsync</c> її кличе й кидає виняток після зупинки контейнера — <c>SqlServerFixtureSizeGuardTests</c>.
    /// Мутація: повернути <c>null</c> завжди → червоний перший Assert.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Спільна_перевірка_розміру_бачить_завеликі_файли()
    {
        var withZeroCeiling = await SqlServerFixture.FindOversizedFilesAsync(sql.ConnectionString, maxFileMb: 0);
        Assert.False(string.IsNullOrEmpty(withZeroCeiling), "стеля 0 МБ мусить знаходити файли тестової бази");
        Assert.Contains(" МБ", withZeroCeiling, StringComparison.Ordinal);

        Assert.Null(await SqlServerFixture.FindOversizedFilesAsync(sql.ConnectionString, maxFileMb: int.MaxValue));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Уся_тестова_база_вкладається_в_гігабайт()
    {
        // ⚠ Окремо від перевірки файлів, і не заради повноти: одна межа на
        // всю базу ловить те, чого не ловить межа на файл, — чотирнадцять
        // файлів, кожен трохи нижчий за стелю. B117: теж розмір народження.
        var totalMb = sql.FileSizesAtBirthMb.Values.Sum();

        Assert.InRange(totalMb, 1, 1024);
    }
}
