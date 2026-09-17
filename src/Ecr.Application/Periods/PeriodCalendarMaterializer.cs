// src/Ecr.Application/Periods/PeriodCalendarMaterializer.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Services;

namespace Ecr.Application.Periods;

/// <summary>
/// Добудовує календар періодів проєкту — без перевірки прав і без збереження.
/// </summary>
/// <remarks>
/// ⛔ Винесено з <see cref="BuildPeriodCalendarHandler"/>, бо календар потрібен
/// ДВОМ сценаріям із РІЗНИМИ правами: читанню календаря
/// (<c>Document.View</c> + грант <c>Read</c>) і активації проєкту
/// (<c>Project.Manage</c> + грант <c>Manage</c>). Покликати обробник із
/// обробника означало б вимагати від того, хто активує проєкт, ще й
/// <c>Document.View</c> — і людина з повним правом на керування проєктами
/// діставала б <c>403</c> на активації, не розуміючи, до чого тут перегляд
/// документів.
///
/// ⛔ Тому саме ЦЕЙ клас не перевіряє прав узагалі: він нічого не вирішує про
/// доступ, а лише виконує вже дозволену роботу. Право перевіряє кожен
/// викликач — своє.
///
/// ⚠ І НЕ ЗБЕРІГАЄ. Активація кладе періоди, переходи станів і сам перехід
/// проєкту ОДНІЄЮ транзакцією; власний <c>SaveChangesAsync</c> тут розрізав би
/// її навпіл, і збій на другій половині лишив би проєкт із календарем, але
/// чернеткою. Зберігає той, хто володіє транзакцією.
/// </remarks>
public sealed class PeriodCalendarMaterializer(IPeriodStore periods)
{
    /// <summary>Створює періоди, яких ще немає, і перераховує межі наявних.</summary>
    /// <param name="project">Проєкт із завантаженими періодами.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Лише НОВІ періоди. Порожньо — календар уже повний (або політика не дає
    /// жодного періоду).
    /// </returns>
    /// <remarks>
    /// ⚠ Повернені періоди НЕ потрапляють у <c>project.Periods</c>: вони йдуть
    /// у сховище, а колекція агрегата лишається тією, якою її завантажили.
    /// Викликач, якому потрібен повний набір, зобов'язаний об'єднати сам —
    /// інакше обчислення над «усіма періодами» мовчки не побачить щойно
    /// створених. Домовленість збережена від початкового коду, а не введена
    /// тут; змінити її означало б чіпати інваріанти агрегата заради зручності
    /// одного сценарію.
    /// </remarks>
    public async Task<IReadOnlyList<Period>> MaterializeAsync(Project project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        var policy = await periods.GetPolicyAsync(project.PeriodPolicyId, ct).ConfigureAwait(false);

        // ⚠ Межі рахуються опівночі В ПОЯСІ МАЙДАНЧИКА і лише потім переводяться
        // в UTC (D-68). Невідомий ідентифікатор поясу — виняток, а не мовчазний
        // UTC: зсув на кілька годин ніхто б не помітив, поки період не закрився
        // б «не тоді».
        var zone = TimeZoneInfo.FindSystemTimeZoneById(project.TimeZoneId);

        // Ідемпотентність забезпечує сам календар: він СТВОРЮЄ лише ті періоди,
        // яких ще немає, і перераховує межі наявних. Повторний виклик після
        // зміни меж проєкту добудує хвіст, а не подвоїть наявне.
        //
        // ⛔ T6/#36: `CustomPeriodCount` передається ЯВНО, а не через параметр
        // за замовчуванням. До цього виклик завжди йшов з `customCount = 0`, і
        // `PeriodKind.Custom` був недосяжний через API.
        var created = PeriodCalendar.Build(
            project, policy, zone, project.Periods, project.CustomPeriodCount ?? 0);

        if (created.Count > 0)
        {
            periods.AddRange(created);
        }

        return created;
    }
}
