// src/Ecr.Domain/Entities/Configuration/PermitTypeDef.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Тип дозволу: визначення, що складається з реєстру шапки й таблиць-реєстрів
/// (<see cref="PermitTypeTable"/>), поверх механізму реєстрів
/// (FEATURE-REGISTRY-TABLES §3, §4.8; PM-02).
/// </summary>
/// <remarks>
/// <para>
/// Сама сутність <b>не змінює</b> <see cref="RegistryDef"/> / <see cref="RegistryFieldDef"/>:
/// усе, чого реєстру бракує для дозволу (група, точність, область переліку), лежить
/// у <see cref="PermitFieldMeta"/> поруч.
/// </para>
/// <para>
/// ⚠ Порушення передумов — <see cref="ArgumentException"/> / <see cref="InvalidOperationException"/>,
/// а не <see cref="DomainException"/>: відмову людині з власним <c>messageKey</c> дає обробник
/// (крок після PM-02) ДО зміни сутності. Сюди порушення доходить лише через помилку в коді,
/// і тоді 500 чесніший за вигаданий текст.
/// </para>
/// </remarks>
public sealed class PermitTypeDef : Entity<int>
{
    private PermitTypeDef() { }

    /// <summary>Створює тип дозволу в стані <see cref="PermitTypeStatus.Draft"/>.</summary>
    /// <param name="code">Код типу; унікальний (<c>UQ_PermitTypeDef</c>), після створення не змінюється.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="category">Категорія дозволу.</param>
    /// <param name="headerRegistryDefId">Реєстр шапки дозволу.</param>
    public PermitTypeDef(EcrCode code, LocalizedText name, PermitCategory category, int headerRegistryDefId)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(headerRegistryDefId);
        EnsureCategory(category);

        Code = code.Value;
        NameL10n = name;
        Category = category;
        HeaderRegistryDefId = headerRegistryDefId;
        Version = 1;
        Status = PermitTypeStatus.Draft;
    }

    /// <summary>Код типу; не змінюється після створення.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Назва мовами каталогу.</summary>
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Категорія дозволу.</summary>
    public PermitCategory Category { get; private set; }

    /// <summary>Реєстр шапки дозволу.</summary>
    public int HeaderRegistryDefId { get; private set; }

    /// <summary>Версія визначення типу.</summary>
    public int Version { get; private set; }

    /// <summary>Стан: чернетка чи опубліковано.</summary>
    public PermitTypeStatus Status { get; private set; }

    /// <summary>Момент публікації в UTC; <c>null</c> для чернетки.</summary>
    public DateTime? PublishedAt { get; private set; }

    /// <summary>Хто опублікував; <c>null</c> для чернетки.</summary>
    public int? PublishedByUserId { get; private set; }

    /// <summary>Токен оптимістичної конкуренції (<c>If-Match</c>); ставить база.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Чернетка визначення типу (склад таблиць, стовпців, списків і перевірок) у вигляді JSON;
    /// <c>null</c> — незавершених змін немає (PM-03, міграція <c>PRM02</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Чернетка живе тут, а не в чернетках довідників: поле довідника, якого ще не існує, не може мати
    /// <see cref="PermitFieldMeta"/> (ключ — саме поле), тож групу, точність і область списку для нових
    /// стовпців тримати більше ніде. Реєстри типу змінюються лише при публікації, однією транзакцією.
    /// </remarks>
    public string? DraftJson { get; private set; }

    /// <summary>Момент останньої зміни чернетки чи публікації в UTC.</summary>
    public DateTime? UpdatedAt { get; private set; }

    /// <summary>Хто змінив чернетку чи опублікував востаннє.</summary>
    public int? UpdatedByUserId { get; private set; }

    /// <summary>Чи є незавершені зміни.</summary>
    public bool HasDraft => DraftJson is not null;

    /// <summary>Змінює назву й категорію (лише чернетка).</summary>
    /// <param name="name">Нова назва.</param>
    /// <param name="category">Нова категорія.</param>
    /// <exception cref="InvalidOperationException">Тип уже опубліковано.</exception>
    public void Update(LocalizedText name, PermitCategory category)
    {
        ArgumentNullException.ThrowIfNull(name);
        EnsureCategory(category);
        EnsureDraft();

        NameL10n = name;
        Category = category;
    }

    /// <summary>Зберігає чернетку визначення (перша чернетка типу чи заміна наявної).</summary>
    /// <param name="json">Визначення типу як JSON.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="utcNow">Момент зміни в UTC.</param>
    public void SaveDraft(string json, int userId, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        EnsureUtc(utcNow);

        DraftJson = json;
        UpdatedAt = utcNow;
        UpdatedByUserId = userId;
    }

    /// <summary>Відкриває нову версію опублікованого типу: чернетка стартує з його поточного складу.</summary>
    /// <param name="json">Поточне опубліковане визначення як JSON.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="utcNow">Момент у UTC.</param>
    /// <exception cref="InvalidOperationException">Тип ще не опубліковано або чернетка вже є.</exception>
    public void OpenNewVersion(string json, int userId, DateTime utcNow)
    {
        if (Status != PermitTypeStatus.Published)
        {
            throw new InvalidOperationException($"Тип дозволу «{Code}» ще не опубліковано: нової версії немає з чого відкривати.");
        }

        if (HasDraft)
        {
            throw new InvalidOperationException($"У типу дозволу «{Code}» уже є чернетка нової версії.");
        }

        SaveDraft(json, userId, utcNow);
    }

    /// <summary>Скидає чернетку (після публікації чи скасування).</summary>
    public void DiscardDraft() => DraftJson = null;

    /// <summary>
    /// Застосовує назву й категорію з чернетки при публікації — на відміну від <see cref="Update"/>, для типу
    /// в будь-якому стані: нова версія опублікованого типу може змінити й підпис.
    /// </summary>
    /// <param name="name">Назва.</param>
    /// <param name="category">Категорія.</param>
    public void ApplyDetails(LocalizedText name, PermitCategory category)
    {
        ArgumentNullException.ThrowIfNull(name);
        EnsureCategory(category);

        NameL10n = name;
        Category = category;
    }

    /// <summary>
    /// Публікує нову версію вже опублікованого типу: <see cref="Version"/> зростає, чернетка зникає.
    /// </summary>
    /// <param name="userId">Хто публікує.</param>
    /// <param name="utcNow">Момент публікації в UTC.</param>
    /// <exception cref="InvalidOperationException">Тип ще не опубліковано (перша публікація — <see cref="Publish"/>).</exception>
    public void Republish(int userId, DateTime utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        EnsureUtc(utcNow);

        if (Status != PermitTypeStatus.Published)
        {
            throw new InvalidOperationException($"Тип дозволу «{Code}» ще не опубліковано: перша публікація — {nameof(Publish)}.");
        }

        Version++;
        PublishedAt = utcNow;
        PublishedByUserId = userId;
        UpdatedAt = utcNow;
        UpdatedByUserId = userId;
        DraftJson = null;
    }

    /// <summary>Публікує тип: <see cref="PermitTypeStatus.Draft"/> → <see cref="PermitTypeStatus.Published"/>.</summary>
    /// <param name="userId">Хто публікує.</param>
    /// <param name="utcNow">Момент публікації; має бути в UTC.</param>
    /// <exception cref="InvalidOperationException">Тип уже опубліковано: назад і вдруге перехід не діє.</exception>
    /// <exception cref="ArgumentException">Момент не в UTC.</exception>
    public void Publish(int userId, DateTime utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        EnsureUtc(utcNow);

        // ⛔ Перехід лише вгору: опублікований тип заморожений, бо на нього вже
        // посилаються прив'язки аркушів і документи.
        EnsureDraft();

        Status = PermitTypeStatus.Published;
        PublishedAt = utcNow;
        PublishedByUserId = userId;
        UpdatedAt = utcNow;
        UpdatedByUserId = userId;
        DraftJson = null;
    }

    private static void EnsureUtc(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Момент має бути в UTC.", nameof(utcNow));
        }
    }

    private void EnsureDraft()
    {
        if (Status != PermitTypeStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Тип дозволу «{Code}» уже опубліковано (стан {Status}): змінити його не можна.");
        }
    }

    private static void EnsureCategory(PermitCategory category)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Невідома категорія дозволу.");
        }
    }
}
