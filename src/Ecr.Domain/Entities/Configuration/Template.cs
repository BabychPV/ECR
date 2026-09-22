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

    /// <summary>Версії шаблону.</summary>
    public IReadOnlyList<TemplateVersion> Versions => _versions;

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
    /// ⚠ Хто і коли архівував — у журналі безпеки, а не в колонках сутності:
    /// <c>cfg.Template</c> не має ні <c>ArchivedAt</c>, ні
    /// <c>ArchivedByUserId</c>, і заводити їх означало б міграцію.
    /// </remarks>
    /// <exception cref="DomainException">
    /// <c>ECR-TMPL-0409</c> — шаблон уже архівований.
    /// </exception>
    public void Archive()
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
