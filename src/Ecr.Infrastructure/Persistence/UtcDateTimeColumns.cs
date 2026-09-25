using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Колонки <c>datetime2</c>, що зберігають МОМЕНТ ЧАСУ в UTC, читаються з
/// <see cref="DateTimeKind.Utc"/> (`V-13`).
/// </summary>
/// <remarks>
/// ⛔ Корінь `V-13`. <c>datetime2</c> не несе зони, і EF матеріалізує його з
/// <see cref="DateTimeKind.Unspecified"/>. <c>System.Text.Json</c> пише такий
/// <see cref="DateTime"/> без «Z» (<c>"2026-09-24T12:48:47.085"</c>), а браузер
/// читає рядок без зони як МІСЦЕВИЙ час — тобто показує момент зі зсувом на
/// пояс оператора (Київ −3 год, <c>Asia/Aqtau</c> −5). Частина сховищ латала
/// це поштучно (<c>UserStore</c> — <c>LastSignInAt</c>, <c>AuditReader</c>,
/// <c>CollectionRunReader</c>), і на одному стенді жили дві шкали часу:
/// перелік користувачів і журнал змін — правильно, документи, задачі, зрізи —
/// ні. Тут правило одне на всю модель: кожна <see cref="DateTime"/>-властивість
/// сутності — момент у UTC, ЯКЩО її не названо в
/// <see cref="CalendarDates"/>.
///
/// ⚠ Межа — лише ЧИТАННЯ. Запис лишається тотожним: у базу йде рівно те
/// значення, що й до виправлення (домен бере час з <c>IClock.UtcNow</c>,
/// тобто вже UTC). Перерахунок <c>Local → UTC</c> на запис змінив би
/// збережені дані й параметри запитів, а про це `V-13` не просить.
///
/// ⛔ НЕ моменти часу — і тому свідомо без конвертера — колонки
/// <c>ValueDate</c>: значення типу <c>Date</c> у комірці, полі шапки, індексі
/// документа, записі довідника й рядку зрізу. Це календарна ДАТА без часу й
/// без зони («15 вересня»), а не мить. Позначка UTC зсунула б її на добу
/// назад у будь-якому поясі на захід від Гринвіча, а <c>ToString("O")</c>, з
/// якого складаються відбиток подання й текст конфлікту
/// (<c>SubmissionPayload</c>, <c>PatchCellsHandler</c>), отримав би «Z» і
/// перестав би збігатися з уже збереженим. Межі періодів (<c>PeriodStart</c>,
/// <c>PeriodEnd</c>) — <see cref="DateOnly"/> і сюди не потрапляють узагалі, а
/// <c>Period.Computed*At</c> — навпаки, моменти: <c>Period.ToUtc</c>
/// переводить їх із поясу майданчика в UTC ще до запису.
///
/// ⚠ Нова <see cref="DateTime"/>-колонка за замовчуванням стає моментом UTC.
/// Якщо вона — календарна дата або «місцевий час майданчика», її треба
/// додати в <see cref="CalendarDates"/>; сторож
/// <c>UtcDateTimeColumnsTests</c> тримає, що кожна властивість моделі класифікована
/// рівно одним способом.
/// </remarks>
public static class UtcDateTimeColumns
{
    /// <summary>
    /// Календарні дати без часу: читаються як є, з <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    public static readonly IReadOnlySet<(Type Entity, string Property)> CalendarDates =
        new HashSet<(Type, string)>
        {
            (typeof(CellValue), nameof(CellValue.ValueDate)),
            (typeof(DocumentHeaderValue), nameof(DocumentHeaderValue.ValueDate)),
            (typeof(DocumentIndexValue), nameof(DocumentIndexValue.ValueDate)),
            (typeof(RegistryValue), nameof(RegistryValue.ValueDate)),
            (typeof(ReportRow), nameof(ReportRow.ValueDate)),
        };

    /// <summary>Єдиний екземпляр конвертера: він без стану.</summary>
    public static readonly ValueConverter<DateTime, DateTime> Converter = new UtcDateTimeConverter();

    /// <summary>
    /// Ставить <see cref="Converter"/> на кожну <see cref="DateTime"/>-властивість
    /// моделі, крім <see cref="CalendarDates"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Прохід по ГОТОВІЙ моделі, а не конвенція в <c>ConfigureConventions</c>:
    /// виняток для календарних дат тоді довелося б знімати в кожній конфігурації
    /// окремо, і забута одна <c>ValueDate</c> тихо переїхала б в UTC. Тут
    /// класифікація в одному переліку, який видно цілком.
    ///
    /// ⚠ Анотацій на властивості не ставимо навмисно: вони потрапили б у знімок
    /// моделі й дали б міграцію без жодної зміни схеми. Конвертер
    /// <c>DateTime → DateTime</c> тип колонки не змінює, тож
    /// <c>has-pending-model-changes</c> лишається порожнім.
    /// </remarks>
    public static void Apply(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // Declared, а не всі: успадкована властивість класифікується за типом,
            // що її оголошує, — інакше `ValueDate` базового типу в нащадка TPH
            // не знайшлася б у переліку.
            foreach (var property in entity.GetDeclaredProperties())
            {
                if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) != typeof(DateTime))
                {
                    continue;
                }

                if (CalendarDates.Contains((entity.ClrType, property.Name)))
                {
                    continue;
                }

                // Явний конвертер з конфігурації сутності — її рішення; не затираємо.
                if (property.GetValueConverter() is not null)
                {
                    continue;
                }

                property.SetValueConverter(Converter);
            }
        }
    }

    /// <summary>Запис — тотожний; читання — з <see cref="DateTimeKind.Utc"/>.</summary>
    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
