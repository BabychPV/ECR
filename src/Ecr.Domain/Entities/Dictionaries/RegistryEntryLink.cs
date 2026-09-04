// src/Ecr.Domain/Entities/Dictionaries/RegistryEntryLink.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Зв'язок M:N між записами довідників (ФВ-8.4). <see cref="PayloadJson"/>
/// тримає атрибути самого зв'язку — наприклад частку або пріоритет, — щоб не
/// заводити окрему сутність під кожен вид відношення.
/// </summary>
/// <remarks>
/// ⚠ Вид зв'язку — рядок <see cref="LinkKind"/>, а не посилання на
/// <c>RegistryRelationDef</c>, як було в скелеті: такої таблиці у схемі
/// **немає взагалі** (`02a-db-schema.md` §5), і зовнішній ключ не було б на що
/// покласти. Це `dic`-частина `Q-027`.
/// </remarks>
public sealed class RegistryEntryLink : Entity<long>
{
    private RegistryEntryLink() { }

    /// <summary>Створює зв'язок.</summary>
    /// <param name="leftEntryId">Запис-джерело: той, що звужує (напр. дозвіл).</param>
    /// <param name="rightEntryId">Запис-ціль: той, що звужується (напр. водний об'єкт).</param>
    /// <param name="linkKind">Вид відношення; входить у ключ унікальності.</param>
    public RegistryEntryLink(long leftEntryId, long rightEntryId, string linkKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkKind);

        // Зв'язок запису із самим собою — не «порожній каскад», а помилка
        // конфігурації: він звузив би список до самого себе і виглядав би як
        // працюючий фільтр.
        if (leftEntryId == rightEntryId)
        {
            throw new DomainException(
                "ECR-REG-0422", $"Запис {leftEntryId} не можна зв'язати сам із собою.");
        }

        LeftEntryId = leftEntryId;
        RightEntryId = rightEntryId;
        LinkKind = linkKind;
    }

    /// <summary>Запис, який звужує вибір.</summary>
    public long LeftEntryId { get; private set; }

    /// <summary>Запис, який потрапляє у звужений список.</summary>
    public long RightEntryId { get; private set; }

    /// <summary>Вид відношення: <c>permit-water-body</c>, <c>permit-pollutant</c>.</summary>
    public string LinkKind { get; private set; } = null!;

    /// <summary>Атрибути зв'язку: ліміт, коефіцієнт, частка.</summary>
    public string? PayloadJson { get; private set; }

    /// <summary>Записує атрибути зв'язку.</summary>
    /// <param name="json">JSON-об'єкт або <c>null</c>.</param>
    /// <exception cref="DomainException">Рядок не є валідним JSON — <c>ECR-REG-0422</c>.</exception>
    /// <remarks>
    /// Перевіряється лише синтаксис: схеми атрибутів у базі немає (див. вище
    /// про <c>RegistryRelationDef</c>), тож звіряти зміст нема з чим. Але
    /// зберегти зламаний JSON означало б, що помилка виявиться при читанні —
    /// у того, хто її не робив.
    /// </remarks>
    public void SetPayload(string? json)
    {
        if (json is null)
        {
            PayloadJson = null;
            return;
        }

        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new DomainException(
                    "ECR-REG-0422", "Атрибути зв'язку мають бути JSON-об'єктом.");
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new DomainException(
                "ECR-REG-0422", $"Атрибути зв'язку не є валідним JSON: {ex.Message}");
        }

        PayloadJson = json;
    }
}
