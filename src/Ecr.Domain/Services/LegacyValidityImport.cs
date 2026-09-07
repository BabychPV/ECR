// src/Ecr.Domain/Services/LegacyValidityImport.cs

using System.Globalization;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Services;

/// <summary>Що саме джерело написало на межі чинності.</summary>
/// <remarks>
/// ⚠ Вид зберігається окремо від результату навмисно: за самим лише
/// <c>2025-01-01</c> уже не видно, чи він походить із чесного
/// <c>23:59:59</c>, чи з набраного з помилкою <c>12:59:59</c>, — а
/// відрізняти їх зобов'язаний саме звіт міграції.
/// </remarks>
public enum LegacyBoundaryKind
{
    /// <summary>Межі в джерелі немає (<c>NULL</c>).</summary>
    Absent = 0,

    /// <summary>Сентинел <c>9999-*</c> — «без обмеження».</summary>
    Sentinel = 1,

    /// <summary>Опівніч: значення вже є виключною межею.</summary>
    Midnight = 2,

    /// <summary>Канонічний кінець доби <c>23:59:59</c>.</summary>
    EndOfDay = 3,

    /// <summary>
    /// Кінець доби, набраний з помилкою (<c>12:59:59</c>) — межа зсувається.
    /// </summary>
    MistypedEndOfDay = 4,
}

/// <summary>Одна перенесена межа чинності.</summary>
/// <param name="Kind">Що написало джерело.</param>
/// <param name="Boundary">
/// Наша межа: виключна дата або <c>null</c> — «без обмеження».
/// </param>
/// <param name="Shift">
/// Наскільки межа зсунулася відносно канонічного кінця доби
/// (<c>23:59:59</c>); <see cref="TimeSpan.Zero"/> — не зсунулася.
/// </param>
public readonly record struct LegacyBoundary(
    LegacyBoundaryKind Kind, DateOnly? Boundary, TimeSpan Shift)
{
    /// <summary>Чи потребує ця межа рядка у звіті міграції.</summary>
    /// <remarks>
    /// ⛔ Не «чи зсунулася на скільки-небудь», а саме окремий вид. Мовчазний
    /// зсув межі на одинадцять годин — рівно те, що директива ПК-1 №05 §7
    /// забороняє: <c>12:59:59</c> і <c>23:59:59</c> дають однакову дату, і без
    /// рядка у звіті різниці між ними не побачить ніхто ніколи.
    /// </remarks>
    public bool NeedsReport => Kind == LegacyBoundaryKind.MistypedEndOfDay;
}

/// <summary>
/// Рядок звіту міграції про зсунуту межу чинності.
/// </summary>
/// <param name="Subject">Що переносили: таблиця, ключ, поле.</param>
/// <param name="Source">Значення, як воно записане в джерелі.</param>
/// <param name="Boundary">Наша виключна межа.</param>
/// <param name="Shift">Зсув відносно кінця доби.</param>
public sealed record LegacyBoundaryNote(
    string Subject, DateTime Source, DateOnly? Boundary, TimeSpan Shift);

/// <summary>
/// Перенос дат чинності з чинної системи: **три різні «нескінченності»**
/// джерела зводяться до одного напівінтервала (директива ПК-1 №05 §7,
/// пастка 5).
/// </summary>
/// <remarks>
/// ⛔ Правило переносу оголошене ТУТ і рівно один раз. Імпортери пишуться
/// окремо (<c>tools/Ecr.Bootstrap.Excel</c>, перенос <c>CInfo</c>, перенос
/// довідників), і якби кожен ніс власну копію, вони розійшлися б мовчки: усі
/// три види межі дають на вигляд правдоподібну дату, а різниця між ними —
/// один день або одинадцять годин. Той самий прийом, що з
/// <c>MethodologyConstant.ClassifyText</c>.
///
/// Таблиця переносу:
/// <list type="table">
/// <item>
///   <term><c>NULL</c></term>
///   <description><c>ValidTo = null</c> — межі немає.</description>
/// </item>
/// <item>
///   <term><c>9999-02-20</c>, <c>9999-12-31</c></term>
///   <description><c>ValidTo = null</c>. ⛔ Сентинел у наші дані не
///   потрапляє **взагалі**: дата, яка означає «ніколи», лишившись датою,
///   рано чи пізно потрапляє в різницю дат і дає 7975 років.</description>
/// </item>
/// <item>
///   <term><c>2024-12-31 23:59:59</c></term>
///   <description><c>ValidTo = 2025-01-01</c> — напівінтервал
///   <c>[from, to)</c>.</description>
/// </item>
/// <item>
///   <term><c>2024-12-31 12:59:59</c> (24 рядки корпусу)</term>
///   <description>те саме <c>2025-01-01</c> — **і рядок у звіті**: межу
///   зсунуто на 11 годин уперед.</description>
/// </item>
/// </list>
///
/// ⚠ Чому зсув рахується від <c>23:59:59</c>, а не від опівночі: 24 рядки
/// корпусу — це та сама «остання мить доби», набрана з помилкою, і питання
/// до методолога звучить «чому тут 12, а в сусідньому рядку 23», а не «чому
/// не опівніч». <c>23:59:59 − 12:59:59 = 11:00:00</c> — рівно те число, яке
/// називає директива.
/// </remarks>
public static class LegacyValidityImport
{
    /// <summary>Рік-сентинел джерела: <c>9999-02-20</c>, <c>9999-12-31</c>.</summary>
    public const int SentinelYear = 9999;

    /// <summary>Канонічний «кінець доби» джерела — <c>23:59:59</c>.</summary>
    public static TimeSpan EndOfDayMarker { get; } = new(23, 59, 59);

    /// <summary>
    /// Переносить **верхню** межу чинності.
    /// </summary>
    /// <param name="source">Значення джерела; <c>null</c> — межі немає.</param>
    /// <returns>Наша межа, вид і зсув.</returns>
    public static LegacyBoundary MapUpperBound(DateTime? source)
    {
        if (source is not { } raw)
        {
            return new LegacyBoundary(LegacyBoundaryKind.Absent, null, TimeSpan.Zero);
        }

        if (raw.Year == SentinelYear)
        {
            return new LegacyBoundary(LegacyBoundaryKind.Sentinel, null, TimeSpan.Zero);
        }

        var day = DateOnly.FromDateTime(raw);
        var time = raw.TimeOfDay;

        // Опівніч уже є виключною межею: джерело написало «з цього дня вже не
        // чинне». Додавати добу тут означало б подовжити чинність на день.
        if (time == TimeSpan.Zero)
        {
            return new LegacyBoundary(LegacyBoundaryKind.Midnight, day, TimeSpan.Zero);
        }

        var boundary = day.AddDays(1);

        if (time == EndOfDayMarker)
        {
            return new LegacyBoundary(LegacyBoundaryKind.EndOfDay, boundary, TimeSpan.Zero);
        }

        // ⛔ Будь-який інший час доби — це кінець доби, набраний з помилкою.
        // Він переноситься ТАК САМО (день чинний цілком), але зсув
        // називається числом: мовчазне «дотягування» межі до опівночі і є те,
        // що директива забороняє.
        return new LegacyBoundary(
            LegacyBoundaryKind.MistypedEndOfDay, boundary, EndOfDayMarker - time);
    }

    /// <summary>
    /// Переносить **нижню** межу чинності.
    /// </summary>
    /// <param name="source">Значення джерела; <c>null</c> — межі немає.</param>
    /// <returns>Наша межа, вид і зсув.</returns>
    /// <remarks>
    /// ⚠ Нижня межа включна, тому час доби просто відкидається: «чинне з
    /// 1 січня 08:00» і «чинне з 1 січня» — той самий перший чинний ДЕНЬ, а
    /// дрібнішого за день кроку в нашій моделі немає.
    /// Сентинел <c>9999</c> знизу в корпусі не трапляється; якщо трапиться,
    /// він означає те саме, що й згори, — «межі не задавали», — і так само
    /// стає <c>null</c>, а не датою, з якої запис не чинний ніколи.
    /// </remarks>
    public static LegacyBoundary MapLowerBound(DateTime? source)
    {
        if (source is not { } raw)
        {
            return new LegacyBoundary(LegacyBoundaryKind.Absent, null, TimeSpan.Zero);
        }

        if (raw.Year == SentinelYear)
        {
            return new LegacyBoundary(LegacyBoundaryKind.Sentinel, null, TimeSpan.Zero);
        }

        return new LegacyBoundary(
            LegacyBoundaryKind.Midnight, DateOnly.FromDateTime(raw), TimeSpan.Zero);
    }

    /// <summary>
    /// Переносить обидві межі одразу.
    /// </summary>
    /// <param name="subject">Що переносимо: таблиця, ключ, поле.</param>
    /// <param name="from">Нижня межа джерела.</param>
    /// <param name="to">Верхня межа джерела.</param>
    /// <param name="notes">
    /// Куди складати рядки звіту; <c>null</c> — звіт не ведеться.
    /// </param>
    /// <returns>Вікно чинності в нашій моделі.</returns>
    /// <remarks>
    /// ⛔ <paramref name="notes"/> необов'язковий, але викликач, який його не
    /// передав, мовчки втрачає 24 відомі рядки корпусу. Саме тому перевантаження
    /// без цього параметра НЕМАЄ: пропустити звіт має бути видно в коді.
    /// </remarks>
    public static ValidityWindow Map(
        string subject, DateTime? from, DateTime? to, ICollection<LegacyBoundaryNote>? notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var lower = MapLowerBound(from);
        var upper = MapUpperBound(to);

        if (upper.NeedsReport && notes is not null)
        {
            notes.Add(new LegacyBoundaryNote(subject, to!.Value, upper.Boundary, upper.Shift));
        }

        return new ValidityWindow(lower.Boundary, upper.Boundary);
    }

    /// <summary>Один рядок звіту міграції — людською мовою і з числом.</summary>
    /// <param name="note">Що переносили і як зсунулася межа.</param>
    /// <returns>Рядок для звіту.</returns>
    /// <remarks>
    /// ⚠ <see cref="CultureInfo.InvariantCulture"/> обов'язковий: звіт
    /// звіряють із дампом джерела, а на машині з українською локаллю дата
    /// надрукувалася б як <c>31.12.2024</c> і перестала б збігатися рядково.
    /// </remarks>
    public static string Describe(LegacyBoundaryNote note)
    {
        ArgumentNullException.ThrowIfNull(note);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}: у джерелі «{1:yyyy-MM-dd HH:mm:ss}», у нас межа {2} (не включно); "
            + "межу зсунуто на {3:hh\\:mm\\:ss} уперед проти кінця доби 23:59:59.",
            note.Subject,
            note.Source,
            note.Boundary?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—",
            note.Shift);
    }
}
