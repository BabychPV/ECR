using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Іменований набір структур звітності. Сам по собі структури не містить:
/// вона живе у <see cref="TemplateVersion"/>, бо мусить бути версійною.
/// </summary>
public sealed class Template : Entity<int>
{
    private readonly List<TemplateVersion> _versions = [];

    private Template() { }   // для EF Core

    /// <summary>Створює шаблон.</summary>
    public Template(EcrCode code, LocalizedText name, int createdByUserId, DateTime utcNow)
    {
        Code = code.Value;
        NameL10n = name;
        CreatedByUserId = createdByUserId;
        CreatedAt = utcNow;
        IsActive = true;
    }

    /// <summary>Код шаблону, унікальний у системі.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Локалізована назва.</summary>
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Довільні теги (<c>["ECR","Land"]</c>) — замість предметних колонок у ядрі.</summary>
    public string? TagsJson { get; private set; }

    /// <summary>Чи пропонується шаблон для НОВИХ документів. Архівований — ні.</summary>
    public bool IsActive { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public int CreatedByUserId { get; private set; }

    /// <summary>
    /// Момент архівування, UTC; <c>null</c> — шаблон в обігу або заархівований до появи колонки
    /// (тоді момент невідомий).
    /// </summary>
    public DateTime? ArchivedAt { get; private set; }

    /// <summary>
    /// ✎ B151 (рішення людини 16.11): у проєкті (рік + об'єкт) — щонайбільше ОДИН документ цього шаблону.
    /// <c>false</c> — документів скільки завгодно (Land); <c>true</c> — один (HSE400, HSE401).
    /// </summary>
    /// <remarks>
    /// Видалений документ не рахується (видалення фізичне), архівований — рахується. Правило перевіряє
    /// <c>CreateDocumentHandler</c> під блоком рядка проєкту; увімкнення при наявних дублікатах відмовляє.
    /// </remarks>
    public bool SingleDocumentPerProject { get; private set; }

    /// <summary>
    /// ✎ b14 document-move (рішення людини 25.11, D-577): річні дані документів шаблону в місячному проєкті правляться
    /// у «вікні року проєкту + зміщення» (<c>Project.PeriodStart…PeriodEnd + YearGraceOffsetDays</c>), а не за станом
    /// закритого січня-якоря (<see cref="Services.YearEditWindowRule"/>). <c>false</c> — як доти (Land, HSE400);
    /// <c>true</c> — HSE401 (таблиця вводу без подання й погодження).
    /// </summary>
    public bool YearEditWindow { get; private set; }

    /// <summary>Версії шаблону.</summary>
    public IReadOnlyList<TemplateVersion> Versions => _versions;

    /// <summary>Задає, скільки документів цього шаблону дозволено в проєкті (B151).</summary>
    /// <param name="single"><c>true</c> — один на проєкт; <c>false</c> — скільки завгодно.</param>
    public void SetSingleDocumentPerProject(bool single) => SingleDocumentPerProject = single;

    /// <summary>Задає «вікно редагування = рік проєкту + зміщення» для річних даних (D-577).</summary>
    /// <param name="enabled"><c>true</c> — вікно року; <c>false</c> — стан періоду, як доти.</param>
    public void SetYearEditWindow(bool enabled) => YearEditWindow = enabled;

    /// <summary>
    /// Змінює назву шаблону. <b>Код не змінюється ніколи</b>.
    /// </summary>
    /// <remarks>
    /// ⛔ <see cref="Code"/> — бізнес-ключ: на нього посилаються проєкти, а
    /// підказка форми створення (<c>templates.codeHint</c>) прямо обіцяє
    /// «змінити потім не можна». Тому картка коду не приймає взагалі: поле,
    /// яке приймають і мовчки ігнорують, гірше за відсутнє.
    ///
    /// ⚠ Опису мовами в моделі НЕМАЄ: у <c>cfg.Template</c> є лише
    /// <c>NameL10n</c> і <c>TagsJson</c>. Колонка під опис — це міграція,
    /// тобто окремий PR.
    /// </remarks>
    /// <param name="name">Назва мовами каталогу; хоча б одна мова непорожня.</param>
    /// <exception cref="DomainException"><c>ECR-TMPL-0422</c> — назви немає.</exception>
    public void Rename(LocalizedText name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // ⚠ Порожній перелік і перелік із самих пробілів — це однаково
        // «назви немає». Шаблон без назви показується в переліку порожнім
        // рядком, і знайти його потім можна лише за кодом.
        if (name.Values.Count == 0 || name.Values.Values.All(string.IsNullOrWhiteSpace))
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                "Назва шаблону обов'язкова хоча б однією мовою каталогу.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-TMPL-0422.templateNameRequired" });
        }

        NameL10n = name;
    }

    /// <summary>
    /// Архівує шаблон: для НОВИХ документів він більше не пропонується.
    /// </summary>
    /// <remarks>
    /// ⛔ Наявні документи працюють далі, і це не поступка, а вимога. Документ
    /// назавжди лишається на своїй версії шаблону (рішення людини на
    /// <c>Q15-05</c>, директива №15, рішення 4), тож архівування, яке
    /// зупиняло б заповнення, обірвало б звітний період посеред роботи.
    /// Архів каже «нового на цьому шаблоні не заводимо», а не «старе зникло».
    ///
    /// ⚠ Момент архівування — <see cref="ArchivedAt"/>; хто архівував — лише в журналі
    /// безпеки (<c>ArchivedByUserId</c> у сутності немає).
    /// </remarks>
    /// <exception cref="DomainException">
    /// <c>ECR-TMPL-0409</c> — шаблон уже архівований.
    /// </exception>
    /// <param name="utcNow">Момент архівування (UTC) — пишеться в <see cref="ArchivedAt"/>.</param>
    public void Archive(DateTime utcNow)
    {
        // ⚠ Повторне архівування — помилка, а не «нічого не сталося»: воно
        // майже завжди означає, що викликач вважає стан іншим, ніж він є, а в
        // журналі безпеки з'явився б другий запис про подію, якої не було.
        if (!IsActive)
        {
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Шаблон {Code} уже архівований.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0409.templateAlreadyArchived",
                    ["code"] = Code,
                });
        }

        IsActive = false;
        ArchivedAt = utcNow;
    }

    /// <summary>Повертає архівований шаблон в обіг.</summary>
    /// <remarks>
    /// ⚠ Дія існує навмисно: без неї архівування було б дверима в один бік, і
    /// помилкове натискання виправлялося б лише запитом до бази повз продукт.
    /// </remarks>
    /// <exception cref="DomainException">
    /// <c>ECR-TMPL-0409</c> — шаблон і так в обігу.
    /// </exception>
    public void Restore()
    {
        if (IsActive)
        {
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Шаблон {Code} не архівований — повертати в обіг нема чого.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0409.templateNotArchived",
                    ["code"] = Code,
                });
        }

        IsActive = true;
        ArchivedAt = null;
    }

    /// <summary>
    /// Перевіряє, що на цьому шаблоні можна заводити НОВІ документи.
    /// </summary>
    /// <remarks>
    /// ⛔ Правило живе в домені, а не лише у фільтрі переліку. «Не пропонується»
    /// у списку — це подання; сервер приймає <c>templateVersionId</c> із тіла
    /// запиту, і без цієї перевірки архівований шаблон лишався б повністю
    /// придатним для всіх, хто знає число.
    /// </remarks>
    /// <exception cref="DomainException">
    /// <c>ECR-TMPL-0409</c> — шаблон архівований.
    /// </exception>
    public void EnsureOfferedForNewDocuments()
    {
        if (!IsActive)
        {
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Шаблон {Code} архівований: нові документи на ньому не заводяться. " +
                "Наявні документи цього шаблону працюють далі.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0409.templateArchived",
                    ["code"] = Code,
                });
        }
    }
}
