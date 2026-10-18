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

    /// <summary>Публікує тип: <see cref="PermitTypeStatus.Draft"/> → <see cref="PermitTypeStatus.Published"/>.</summary>
    /// <param name="userId">Хто публікує.</param>
    /// <param name="utcNow">Момент публікації; має бути в UTC.</param>
    /// <exception cref="InvalidOperationException">Тип уже опубліковано: назад і вдруге перехід не діє.</exception>
    /// <exception cref="ArgumentException">Момент не в UTC.</exception>
    public void Publish(int userId, DateTime utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Момент публікації має бути в UTC.", nameof(utcNow));
        }

        // ⛔ Перехід лише вгору: опублікований тип заморожений, бо на нього вже
        // посилаються прив'язки аркушів і документи.
        EnsureDraft();

        Status = PermitTypeStatus.Published;
        PublishedAt = utcNow;
        PublishedByUserId = userId;
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
