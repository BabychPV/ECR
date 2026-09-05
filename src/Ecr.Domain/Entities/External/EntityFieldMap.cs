// src/Ecr.Domain/Entities/External/EntityFieldMap.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.External;

/// <summary>Куди лягає поле джерела.</summary>
public enum FieldTargetKind : byte
{
    /// <summary>Колонка таблиці документа.</summary>
    Column = 0,

    /// <summary>Поле запису довідника.</summary>
    RegistryField = 1,
}

/// <summary>
/// Як згорнути точки періоду в одне число (<c>D-118</c>).
/// </summary>
/// <remarks>
/// ⛔ Значення за замовчуванням **немає і не буде**. Система не знає, чи
/// величина миттєва (концентрація → <see cref="Last"/>) чи накопичувальна
/// (обсяг → <see cref="Sum"/>); це знає той, хто налаштовує мапінг. Підставити
/// «найімовірніше» означало б отримати ЧИСЛО, а не відмову, — і розбіжність
/// знайшли б на звірці через місяць, коли звіт уже подано.
/// </remarks>
public enum AggregationKind : byte
{
    /// <summary>Сума точок періоду: обсяги, маси.</summary>
    Sum = 0,

    /// <summary>Середнє: концентрації, температури.</summary>
    Avg = 1,

    /// <summary>Мінімум за період.</summary>
    Min = 2,

    /// <summary>Максимум за період.</summary>
    Max = 3,

    /// <summary>Остання точка: показник лічильника на кінець періоду.</summary>
    Last = 4,

    /// <summary>Перша точка: показник на початок періоду.</summary>
    First = 5,
}

/// <summary>
/// Мапінг поля джерела на поле ECR (<c>ext.EntityFieldMap</c>).
/// </summary>
/// <remarks>
/// ⚠ Одиниці **на межі інтеграції**: атрибути PI AF мають власний UOM, і це
/// найчастіше джерело мовчазних розбіжностей у числах (ФВ-16.9). Тому
/// <see cref="SourceUnitId"/> і <see cref="TargetUnitId"/> зберігаються обидва,
/// а конверсія виконується при завантаженні з журналом — інакше повторний
/// перерахунок з архіву дасть інший результат (ФВ-16.10, D-79).
/// </remarks>
public sealed class EntityFieldMap : Entity<int>
{
    private EntityFieldMap() { }

    private EntityFieldMap(int sourceEntityId, string sourceField)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceField);

        SourceEntityId = sourceEntityId;
        SourceField = sourceField;
        IsActive = true;
    }

    /// <summary>Мапінг на колонку документа.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourceField">Поле в джерелі.</param>
    /// <param name="columnDefId">Колонка-ціль.</param>
    public static EntityFieldMap ToColumn(int sourceEntityId, string sourceField, int columnDefId)
        => new(sourceEntityId, sourceField)
        {
            TargetKind = FieldTargetKind.Column,
            TargetColumnDefId = columnDefId,
        };

    /// <summary>Мапінг на поле довідника.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourceField">Поле в джерелі.</param>
    /// <param name="registryFieldDefId">Поле довідника-ціль.</param>
    public static EntityFieldMap ToRegistryField(
        int sourceEntityId, string sourceField, int registryFieldDefId)
        => new(sourceEntityId, sourceField)
        {
            TargetKind = FieldTargetKind.RegistryField,
            TargetRegistryFieldDefId = registryFieldDefId,
        };

    public int SourceEntityId { get; private set; }
    public string SourceField { get; private set; } = null!;

    /// <summary>Одиниця ДЖЕРЕЛА; її зміна зупиняє збір (ФВ-16.9).</summary>
    public int? SourceUnitId { get; private set; }

    public FieldTargetKind TargetKind { get; private set; }
    public int? TargetColumnDefId { get; private set; }
    public int? TargetRegistryFieldDefId { get; private set; }

    /// <summary>Одиниця, у якій значення лягає в ECR.</summary>
    public int? TargetUnitId { get; private set; }

    /// <summary>Іменоване перетворення зі списку; довільний код заборонений.</summary>
    public string? TransformCode { get; private set; }

    /// <summary>
    /// У ЯКИЙ рядок лягає значення (<c>D-118</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього поля мапінг не має адресата: комірка адресується трійкою
    /// «період, рядок, колонка», а мапінг називав лише колонку. У таблиці на
    /// 500 рядків неможливо сказати, у котрий із них лягає значення тега — і
    /// будь-яка реалізація тут ВИГАДАЛА б правило («перший рядок», «рядок із
    /// таким самим кодом»), усі однаково правдоподібні на вигляд.
    ///
    /// ⚠ Точка з AF завжди має фіксованого адресата: тег <c>Flare_01_CO</c>
    /// належить рядку <c>Flare_01</c> завжди — це і є суть мапінгу. Варіанту
    /// «ключ рядка з поля джерела» немає навмисно: у чинній системі такого
    /// немає, і він відкрив би шлях до рядків, яких у шаблоні не існує.
    ///
    /// ⚠ <c>null</c> означає рівно одне: **мапінг не матеріалізується**.
    /// Точки лишаються сирими в <c>ext.RawDataPoint</c> для звірки, і це
    /// легальний стан — тег може збиратися для контролю, а не для форми.
    /// </remarks>
    public string? TargetRowKey { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Чи переносяться точки цього мапінгу в комірки.</summary>
    public bool IsMaterialized => TargetRowKey is not null;

    /// <summary>Ставить одиниці межі.</summary>
    /// <param name="sourceUnitId">Одиниця джерела.</param>
    /// <param name="targetUnitId">Одиниця цілі.</param>
    public void SetUnits(int? sourceUnitId, int? targetUnitId)
    {
        SourceUnitId = sourceUnitId;
        TargetUnitId = targetUnitId;
    }

    /// <summary>Ставить іменоване перетворення.</summary>
    /// <param name="transformCode">Код перетворення; <c>null</c> — без нього.</param>
    public void SetTransform(string? transformCode) => TransformCode = transformCode;

    /// <summary>
    /// Задає рядок-адресат і спосіб згортання точок (<c>D-118</c>).
    /// </summary>
    /// <param name="targetRowKey">Ключ рядка; <c>null</c> — не матеріалізувати.</param>
    /// <param name="aggregation">Як згортати; обов'язково при заданому рядку.</param>
    /// <exception cref="DomainException">Рядок заданий без агрегації.</exception>
    /// <remarks>
    /// ⛔ Пара нерозривна. Мапінг із рядком і без агрегації — помилка
    /// конфігурації, а не «збережемо, розберемося при зборі»: під час збору
    /// вибір довелося б робити коду, а він його зробити не може. Ловиться
    /// «Перевіркою конфігурації» (`ФВ-13.17`), не збором.
    /// </remarks>
    public void SetMaterialization(string? targetRowKey, AggregationKind? aggregation)
    {
        if (targetRowKey is not null && aggregation is null)
        {
            throw new DomainException(
                "ECR-INT-0422",
                $"Мапінг поля «{SourceField}» називає рядок «{targetRowKey}», але не каже, "
                + "як згортати точки періоду. Система не знає, величина миттєва чи накопичувальна.");
        }

        TargetRowKey = targetRowKey;
        TransformCode = aggregation?.ToString();
    }

    /// <summary>Спосіб згортання; <c>null</c> — мапінг не матеріалізується.</summary>
    public AggregationKind? Aggregation
        => Enum.TryParse<AggregationKind>(TransformCode, out var kind) ? kind : null;

    /// <summary>Вимикає мапінг.</summary>
    public void Deactivate() => IsActive = false;
}
