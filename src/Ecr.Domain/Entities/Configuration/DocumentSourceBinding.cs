// src/Ecr.Domain/Entities/Configuration/DocumentSourceBinding.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// B80: з якого стану документа-джерела споживачі беруть дані зв'язки (<see cref="DocumentSourceBinding.SourceState"/>).
/// </summary>
public enum DocumentSourceState : byte
{
    /// <summary>Будь-який стан, як до B80 (умовчання сіду; рішення людини: кожен стенд стартує тут).</summary>
    Any = 0,

    /// <summary>Лише затверджений місяць джерела («так лише з погодженого HSE400», 16.11); інший місяць — без даних.</summary>
    ApprovedOnly = 1,
}

/// <summary>B80: як поводитися з правкою джерела, що зачіпає залежні документи (споживає N5).</summary>
public enum DocumentSourcePolicy : byte
{
    /// <summary>Без запиту.</summary>
    None = 0,

    /// <summary>Запит без причини.</summary>
    Warn = 1,

    /// <summary>Запит із причиною (умовчання сіду, дизайн 13.11).</summary>
    ConfirmWithReason = 2,

    /// <summary>Відмова, доки є затверджений залежний місяць.</summary>
    Block = 3,
}

/// <summary>
/// B80 (N1): зв'язка «документ шаблону-джерела замінює рядки довідника для шаблонів-споживачів»
/// (<c>cfg.DocumentSourceBinding</c>). Перша й поки єдина — HSE400 → <c>LAND_HSE400_COMPOSITION</c> → Land.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Відображення документа на рядки довідника лишається в коді (<see cref="MappingKind"/> = 1 —
/// <c>Hse400CompositionMapping</c>); таблиця тримає лише ідентичність, політику, стан-ворота і два біти, які
/// замінюють ключі <c>Calculations:Hse400CompositionFromDocument</c> (✎ P-0118-31/33: виведено, не читається;
/// <see cref="ReadFromDocument"/> — єдиний вимикач читання складу з документа) і
/// <c>Calculations:Hse400RecalculateLandDependents</c> (✎ B179 L2: прибрано; <see cref="RecalculateDependents"/> — аварійний
/// вимикач автоматичних залежних).
/// </para>
/// <para>
/// ⛔ Перемикання <see cref="SourceState"/> — лише явна дія адміністратора (ендпоінт із перевіркою «проєкт джерела
/// місячний»); сід рядок, що вже є, не змінює.
/// </para>
/// </remarks>
public sealed class DocumentSourceBinding : Entity<int>
{
    /// <summary>Відображення складу газу HSE400 (<c>Hse400CompositionMapping</c>).</summary>
    public const byte Hse400CompositionMapping = 1;

    /// <summary>Найдовший код — як <c>nvarchar(64)</c> у схемі.</summary>
    public const int CodeMaxLength = 64;

    private readonly List<DocumentSourceBindingConsumer> _consumers = [];

    private DocumentSourceBinding() { }

    /// <summary>Нова зв'язка в стані <see cref="DocumentSourceState.Any"/> (для тестів; у базі рядок заводить сід).</summary>
    /// <param name="code">Код зв'язки.</param>
    /// <param name="sourceTemplateId">Шаблон документа-джерела.</param>
    /// <param name="registryDefId">Довідник, рядки якого замінює документ.</param>
    public DocumentSourceBinding(string code, int sourceTemplateId, int registryDefId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceTemplateId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registryDefId);

        Code = code;
        SourceTemplateId = sourceTemplateId;
        RegistryDefId = registryDefId;
        MappingKind = Hse400CompositionMapping;
        Policy = DocumentSourcePolicy.ConfirmWithReason;
        SourceState = DocumentSourceState.Any;
        ReadFromDocument = true;
        RecalculateDependents = true;
    }

    /// <summary>Код зв'язки (<c>HSE400_LAND_COMPOSITION</c>); унікальний.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Шаблон документа-джерела.</summary>
    public int SourceTemplateId { get; private set; }

    /// <summary>Довідник, рядки якого за роки з документом замінює документ.</summary>
    public int RegistryDefId { get; private set; }

    /// <summary>Відображення документа на рядки довідника (1 — склад HSE400).</summary>
    public byte MappingKind { get; private set; }

    /// <summary>JSON-масив кодів таблиць джерела; <c>null</c> — вирішує відображення.</summary>
    public string? SourceTableCodes { get; private set; }

    /// <summary>Політика згоди на зміну джерела (споживає N5).</summary>
    public DocumentSourcePolicy Policy { get; private set; }

    /// <summary>Стан-ворота: з якого стану джерела читають споживачі.</summary>
    public DocumentSourceState SourceState { get; private set; }

    /// <summary>Читати з документа взагалі (явний ключ конфігурації перекриває).</summary>
    public bool ReadFromDocument { get; private set; }

    /// <summary>Ставити перерахунок залежних (явний ключ конфігурації перекриває).</summary>
    public bool RecalculateDependents { get; private set; }

    /// <summary>Коли стан-ворота змінено востаннє, UTC; <c>null</c> — не змінювався.</summary>
    public DateTime? UpdatedAt { get; private set; }

    /// <summary>Хто змінив стан-ворота востаннє.</summary>
    public int? UpdatedByUserId { get; private set; }

    /// <summary>Шаблони-споживачі.</summary>
    public IReadOnlyList<DocumentSourceBindingConsumer> Consumers => _consumers;

    /// <summary>Перемикає стан-ворота.</summary>
    /// <param name="state">Новий стан.</param>
    /// <param name="userId">Хто перемикає.</param>
    /// <param name="utcNow">Момент у UTC.</param>
    /// <returns><c>true</c> — стан справді змінився.</returns>
    public bool SwitchState(DocumentSourceState state, int userId, DateTime utcNow)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Момент має бути в UTC.", nameof(utcNow));
        }

        if (state == SourceState)
        {
            return false;
        }

        SourceState = state;
        UpdatedAt = utcNow;
        UpdatedByUserId = userId;
        return true;
    }

    /// <summary>
    /// B179 L5: перемикає біти зв'язки — «читати з документа» і «ставити перерахунок залежних» (аварійний вимикач).
    /// <c>null</c> — біт не чіпати.
    /// </summary>
    /// <param name="readFromDocument">Новий біт «читати з документа» або <c>null</c>.</param>
    /// <param name="recalculateDependents">Новий біт «перераховувати залежних» або <c>null</c>.</param>
    /// <param name="userId">Хто перемикає.</param>
    /// <param name="utcNow">Момент у UTC.</param>
    /// <returns><c>true</c> — хоч один біт справді змінився.</returns>
    public bool SetFlags(bool? readFromDocument, bool? recalculateDependents, int userId, DateTime utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Момент має бути в UTC.", nameof(utcNow));
        }

        var changed = false;
        if (readFromDocument is { } read && read != ReadFromDocument)
        {
            ReadFromDocument = read;
            changed = true;
        }

        if (recalculateDependents is { } recalculate && recalculate != RecalculateDependents)
        {
            RecalculateDependents = recalculate;
            changed = true;
        }

        if (changed)
        {
            UpdatedAt = utcNow;
            UpdatedByUserId = userId;
        }

        return changed;
    }

    /// <summary>Додає шаблон-споживач (для тестів; у базі — сід).</summary>
    /// <param name="templateId">Шаблон.</param>
    public void AddConsumer(int templateId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(templateId);
        if (_consumers.TrueForAll(c => c.TemplateId != templateId))
        {
            _consumers.Add(new DocumentSourceBindingConsumer(templateId));
        }
    }
}

/// <summary>Шаблон-споживач зв'язки (<c>cfg.DocumentSourceBindingConsumer</c>); ключ — (зв'язка, шаблон).</summary>
public sealed class DocumentSourceBindingConsumer
{
    private DocumentSourceBindingConsumer() { }

    internal DocumentSourceBindingConsumer(int templateId) => TemplateId = templateId;

    /// <summary>Зв'язка.</summary>
    public int BindingId { get; private set; }

    /// <summary>Шаблон-споживач.</summary>
    public int TemplateId { get; private set; }
}
