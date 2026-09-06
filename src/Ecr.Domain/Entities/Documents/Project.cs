using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Проєкт — звітний рік або інший діапазон.</summary>
public sealed class Project : Entity<int>
{
    private readonly List<Period> _periods = [];

    private Project() { }

    public Project(EcrCode code, LocalizedText name, DateOnly periodStart, DateOnly periodEnd,
                   int templateVersionId, PeriodKind periodKind, int periodPolicyId, string timeZoneId)
    {
        Code = code.Value;
        NameL10n = name;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        TemplateVersionId = templateVersionId;
        PeriodKind = periodKind;
        PeriodPolicyId = periodPolicyId;

        // ⛔ Пояс перевіряється В КОНСТРУКТОРІ, а не лише в обробнику. Він
        // вічний (ФВ-1.1a), тобто помилка тут лишається з проєктом назавжди, а
        // шляхів створення більше одного: обробник, клонування, фікстури,
        // сідінг. Перевірка в одному з них означала б, що решта заводить
        // проєкти, межі періодів яких не порахує ніхто.
        TimeZoneId = SiteTimeZone.Create(timeZoneId);
        Status = ProjectStatus.Draft;
        CurrentPeriodMode = CurrentPeriodMode.Auto;
        YearGraceOffsetDays = 45;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Джерело істини про межі проєкту.</summary>
    public DateOnly PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }

    /// <summary>Лише підпис для UI. **Не ідентичність.**</summary>
    public short? Year { get; private set; }

    public string? TagsJson { get; private set; }
    public int TemplateVersionId { get; private set; }
    public PeriodKind PeriodKind { get; private set; }
    public int PeriodPolicyId { get; private set; }
    public int YearGraceOffsetDays { get; private set; }

    /// <summary>
    /// Пояс майданчика — ідентифікатор IANA (<c>Asia/Aqtau</c>). У ньому
    /// рахуються межі періодів, offsets і <c>IsLateEdit</c> — не в UTC (D-68).
    /// </summary>
    /// <remarks>
    /// ⛔ Саме IANA, а не зсув: зсув міняється (перехід на літній час), і
    /// збережене число почало б брехати рівно тоді, коли межа періоду
    /// зсунеться на годину. Тип лишається <c>string</c>, бо це колонка бази;
    /// правило про допустимі значення живе в <see cref="SiteTimeZone"/>.
    /// </remarks>
    public string TimeZoneId { get; private set; } = null!;

    /// <summary>Режим визначення поточного періоду (D-77).</summary>
    public CurrentPeriodMode CurrentPeriodMode { get; private set; }

    /// <summary>Поточний звітний період — **наша конфігурація**, а не значення з AF.</summary>
    public int? CurrentPeriodId { get; private set; }

    public string? CurrentPeriodPinnedReason { get; private set; }
    public DateTime? CurrentPeriodChangedAt { get; private set; }
    public int? CurrentPeriodChangedByUserId { get; private set; }

    /// <summary>Разові налаштування, зібрані ззовні при створенні. **Не постійна залежність.**</summary>
    public string? ExternalSettingsJson { get; private set; }

    public ProjectStatus Status { get; private set; }

    /// <summary>Прапорець виконання архівації: читання має брати джерело за станом, не за датою.</summary>
    public bool IsArchiving { get; private set; }

    public DateTime? ClosedAt { get; private set; }
    public int? ClosedByUserId { get; private set; }

    public IReadOnlyList<Period> Periods => _periods;

    /// <summary>
    /// Переводить проєкт із <c>Draft</c> у <c>Active</c>.
    /// </summary>
    /// <param name="utcNow">Момент переходу; лишається в журналі аудиту.</param>
    /// <exception cref="InvalidOperationException">Проєкт уже не чернетка.</exception>
    /// <remarks>
    /// ⛔ До `A7-25` цього переходу не існувало ЗОВСІМ. Проєкт створювався
    /// чернеткою, а <c>PeriodStateJob</c> обробляє лише активні — тобто
    /// періоди жодного проєкту не відкривалися ніколи, і жодну комірку не
    /// можна було заповнити. Стан <c>Active</c> траплявся тільки в
    /// параметрі за замовчуванням тестової фікстури: перевірки жили в
    /// системі, якої не існувало.
    ///
    /// ⚠ Чернетка — стан осмислений і лишається: між створенням проєкту й
    /// активацією заводять періоди й прив'язують версію шаблону. Активація —
    /// свідома дія людини, яка каже «налаштування завершено»; робити її
    /// автоматично при створенні означало б відкрити періоди раніше, ніж
    /// з'явиться те, що в них заповнювати.
    ///
    /// ⚠ Повторна активація — помилка, а не «нічого не сталося»: вона майже
    /// завжди означає, що викликач вважає стан іншим, ніж він є.
    /// </remarks>
    public void Activate(DateTime utcNow)
    {
        if (Status != ProjectStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Активувати можна лише чернетку; проєкт у стані {Status}.");
        }

        // ⚠ Момент переходу тут не зберігається: колонки під нього в
        // `doc.Project` немає (`02a` §Project), і додавати її заради підпису
        // означало б розійтися зі схемою. Хто і коли активував — у журналі
        // аудиту, який пише обробник.
        _ = utcNow;
        Status = ProjectStatus.Active;
    }

    /// <summary>
    /// Позначає проєкт заархівованим.
    /// </summary>
    /// <param name="utcNow">Момент операції.</param>
    /// <exception cref="InvalidOperationException">Проєкт не активний.</exception>
    /// <remarks>
    /// ⛔ Це ПОЗНАЧКА, а не перенесення даних: фізично в <c>arc.*</c> їх
    /// переносить <c>ArchiveJob</c>, і робить це окремим свідомим кроком.
    /// Об'єднати їх означало б, що натиснута кнопка одразу починає
    /// багатогодинну операцію над мільйонами рядків.
    ///
    /// ⚠ Умову «всі періоди закриті» перевіряє обробник, а не домен: періоди
    /// тут — навігація, яку могли не завантажити, і мовчазний дозвіл на
    /// неповному графі гірший за перевірку в одному місці.
    /// </remarks>
    public void Archive(DateTime utcNow)
    {
        if (Status != ProjectStatus.Active)
        {
            throw new InvalidOperationException(
                $"Заархівувати можна лише активний проєкт; стан {Status}.");
        }

        Status = ProjectStatus.Archived;
        ClosedAt = utcNow;
    }

    /// <summary>
    /// Фіксує поточний період вручну. Причина обов'язкова: стан неочевидний
    /// і має бути видимим в UI.
    /// </summary>
    /// <param name="periodId">Період, який фіксується поточним.</param>
    /// <param name="reason">Причина; показується в UI поруч зі станом.</param>
    /// <param name="userId">Хто зафіксував.</param>
    /// <param name="utcNow">Момент операції.</param>
    /// <exception cref="DomainException">Період чужий або причина порожня.</exception>
    public void PinCurrentPeriod(int periodId, string reason, int userId, DateTime utcNow)
    {
        if (_periods.All(p => p.Id != periodId))
        {
            throw new DomainException(
                "ECR-PRD-0422", $"Період {periodId} не належить проєкту {Code}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("ECR-PRD-0422", "Причина фіксації поточного періоду обов'язкова.");
        }

        CurrentPeriodMode = CurrentPeriodMode.Pinned;
        CurrentPeriodId = periodId;
        CurrentPeriodPinnedReason = reason;
        CurrentPeriodChangedAt = utcNow;
        CurrentPeriodChangedByUserId = userId;
    }

    /// <summary>Повертає автоматичне визначення поточного періоду.</summary>
    /// <param name="userId">Хто зняв фіксацію.</param>
    /// <param name="utcNow">Момент операції.</param>
    public void UnpinCurrentPeriod(int userId, DateTime utcNow)
    {
        CurrentPeriodMode = CurrentPeriodMode.Auto;
        CurrentPeriodPinnedReason = null;
        CurrentPeriodChangedAt = utcNow;
        CurrentPeriodChangedByUserId = userId;

        // CurrentPeriodId лишається як є: до наступного прогону PeriodStateJob
        // краще показувати останнє відоме значення, ніж порожнечу.
    }

    /// <summary>Оновлює поточний період у режимі <c>Auto</c>. Викликає лише <c>PeriodStateJob</c>.</summary>
    /// <param name="periodId">Обраний період; <c>null</c> — відкритих немає.</param>
    /// <param name="utcNow">Момент операції.</param>
    public void SetCurrentPeriodAutomatically(int? periodId, DateTime utcNow)
    {
        // ⚠ Ручний пін має пріоритет над задачею: людина зафіксувала період
        // свідомо і з причиною, і нічна задача не має права це скасувати.
        if (CurrentPeriodMode == CurrentPeriodMode.Pinned)
        {
            return;
        }

        CurrentPeriodId = periodId;
        CurrentPeriodChangedAt = utcNow;
    }

    /// <summary>
    /// Змінює пояс майданчика. Дозволено лише поки жоден період не відкривався.
    /// </summary>
    /// <param name="timeZoneId">Новий пояс — ідентифікатор IANA.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-CFG-4221</c> — значення не є відомим ідентифікатором IANA;
    /// <c>ECR-PRD-0409</c> — перший період уже відкривався.
    /// </exception>
    /// <remarks>
    /// ⚠ Ретроактивна зміна зсунула б межі **закритих** періодів і переписала
    /// б <c>IsLateEdit</c> на **поданих** формах (ФВ-1.1a, D-110). Тобто змінила
    /// б минуле: запис, який був вчасним, став би пізнім заднім числом.
    /// </remarks>
    public void ChangeTimeZone(string timeZoneId)
    {
        // ⚠ Незмінність перевіряється ПЕРШОЮ, а не після значення. Коли період
        // уже відкритий, операція відхиляється за будь-якого значення, і
        // відповідь «пояс не є IANA» обіцяла б, що з правильним значенням вона
        // пройде. Не пройде — це вже нічия властивість, а спільна.
        //
        // Ознака — не статус проєкту, а факт, що якийсь період уже виходив зі
        // Scheduled: саме з цієї миті межі стали чиїмись зобов'язаннями.
        if (_periods.Any(p => p.State != PeriodState.Scheduled))
        {
            throw new DomainException(
                "ECR-PRD-0409",
                "Пояс майданчика не змінюється після відкриття першого періоду (ФВ-1.1a).");
        }

        // ⛔ Перевірка та сама, що й у конструкторі, і зроблена тим самим
        // значеннєвим об'єктом. Тут стояла лише перевірка «непорожньо», тобто
        // цей шлях приймав і `Central Asia Standard Time`, і `+05:00`, і
        // будь-яке слово. Два формулювання одного правила розходяться на першій
        // правці одного з них, і розбіжність видно лише тоді, коли період
        // закриється не тоді, коли всі чекали.
        TimeZoneId = SiteTimeZone.Create(timeZoneId);
    }

    /// <summary>Перевіряє, що дата належить проєкту (ФВ-1.11).</summary>
    public bool ContainsDate(DateOnly date) => date >= PeriodStart && date <= PeriodEnd;
}
