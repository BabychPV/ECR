// src/Ecr.Domain/Entities/Calculations/Methodology.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Методологія розрахунку — **контейнер версій**, а не сама формула
/// (ФВ-9.1). Обчислює завжди конкретна версія, обрана за датою періоду.
/// </summary>
public sealed class Methodology : Entity<int>
{
    private readonly List<MethodologyVersion> _versions = [];

    private Methodology() { }

    public Methodology(EcrCode code, LocalizedText name)
    {
        Code = code.Value;
        NameL10n = name;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public bool IsActive { get; private set; }

    /// <summary>Група методологій; вона ж одиниця перемикання master (ФВ-13.10).</summary>
    public string? Group { get; private set; }

    public IReadOnlyList<MethodologyVersion> Versions => _versions;

    /// <summary>Додає версію до контейнера.</summary>
    /// <param name="version">Версія цієї методології.</param>
    /// <exception cref="DomainException">Номер версії вже зайнятий.</exception>
    public void AddVersion(MethodologyVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        // Номер версії — те, чим на неї посилаються в аудиті й у зрізах
        // подання. Два однакові номери роблять посилання неоднозначним
        // заднім числом (`UQ_MethodologyVersion`).
        if (_versions.Any(v => string.Equals(v.Version, version.Version, StringComparison.Ordinal)))
        {
            throw new DomainException(
                "ECR-CALC-0409", $"Версія {version.Version} методології {Code} уже існує.");
        }

        _versions.Add(version);
    }

    /// <summary>Ставить групу методології.</summary>
    /// <param name="group">Назва групи; <c>null</c> — поза групами.</param>
    public void SetGroup(string? group) => Group = group;

    /// <summary>
    /// Публікує версію з перевіркою, що вікна дії не перетинаються (ФВ-13.3).
    /// </summary>
    /// <param name="version">Версія-чернетка цієї методології.</param>
    /// <param name="publishedByUserId">Хто публікує; не автор (D-40).</param>
    /// <param name="changeReason">Причина зміни (ФВ-14.7).</param>
    /// <param name="effectiveFrom">Від якої дати версія чинна.</param>
    /// <param name="testsPassed">Чи зелений останній прогін тестів (ФВ-9.12).</param>
    /// <param name="utcNow">Час публікації в UTC.</param>
    /// <exception cref="DomainException">
    /// Версія належить іншій методології, або дату початку вже зайнято.
    /// </exception>
    /// <remarks>
    /// ⚠ Перевіряється саме **збіг дат початку**, а не перекриття інтервалів:
    /// кінця вікна не існує, версія чинна до початку наступної. Тому перетин
    /// можливий рівно в один спосіб — дві опубліковані версії від однієї дати,
    /// — і тоді <see cref="VersionOn"/> не має відповіді.
    /// </remarks>
    public void PublishVersion(
        MethodologyVersion version,
        int publishedByUserId,
        string changeReason,
        DateOnly effectiveFrom,
        bool testsPassed,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(version);

        // ⚠ ReferenceEquals, а не Contains. Entity<TId>.Equals порівнює Id і
        // повертає false для НЕЗБЕРЕЖЕНОЇ сутності — навіть проти себе самої.
        // Contains тут мовчки не знаходив би щойно доданої версії, і публікація
        // падала б із «не належить методології» на цілком правильному коді.
        if (!_versions.Any(v => ReferenceEquals(v, version)))
        {
            throw new DomainException(
                "ECR-CALC-0422",
                $"Версія {version.Version} не належить методології {Code}.");
        }

        var clash = _versions.FirstOrDefault(
            v => v.IsPublished && v.EffectiveFrom == effectiveFrom);

        if (clash is not null)
        {
            throw new DomainException(
                "ECR-CALC-0409",
                $"Версія {clash.Version} уже чинна від {effectiveFrom:yyyy-MM-dd}: "
                + "дві опубліковані версії від однієї дати роблять вибір методології неоднозначним "
                + "(ФВ-13.3).");
        }

        version.Publish(publishedByUserId, changeReason, effectiveFrom, testsPassed, utcNow);
    }

    /// <summary>Версія, чинна на дату звітного періоду (ФВ-9.3).</summary>
    /// <param name="periodDate">Дата періоду, а не «сьогодні».</param>
    /// <returns>Версія або <c>null</c>, якщо на цю дату жодна не чинна.</returns>
    /// <remarks>
    /// ⚠ Береться остання опублікована версія, що почалася **не пізніше** за
    /// дату: кінця вікна не існує, і версія діє до початку наступної. Такий
    /// вибір однозначний за побудовою — саме тому <see cref="PublishVersion"/>
    /// не дає двом версіям почати одного дня.
    /// </remarks>
    public MethodologyVersion? VersionOn(DateOnly periodDate)
        => _versions
            .Where(v => v.Status == TemplateVersionStatus.Published
                        || v.Status == TemplateVersionStatus.Deprecated)
            .Where(v => v.EffectiveFrom is not null && v.EffectiveFrom <= periodDate)
            .OrderByDescending(v => v.EffectiveFrom)
            .FirstOrDefault();
}
