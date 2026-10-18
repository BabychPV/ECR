// src/Ecr.Application/Permits/PermitTypeCodec.cs
using System.Text.Json;
using Ecr.Application.Permits.Dto;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits;

/// <summary>Читання й запис чернетки типу (<c>DraftJson</c>) та переклад перелічень у рядки контракту.</summary>
public static class PermitTypeCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Складає JSON чернетки.</summary>
    /// <param name="content">Вміст.</param>
    public static string Serialize(PermitTypeContent content) => JsonSerializer.Serialize(content, Json);

    /// <summary>Розбирає JSON чернетки.</summary>
    /// <param name="json">Текст з <c>DraftJson</c>.</param>
    public static PermitTypeContent Parse(string json)
        => JsonSerializer.Deserialize<PermitTypeContent>(json, Json)
           ?? new PermitTypeContent([], nameof(PermitCategory.Other), [], []);

    /// <summary>Рядок контракту для категорії.</summary>
    /// <param name="category">Категорія.</param>
    public static string CategoryName(PermitCategory category) => category.ToString();

    /// <summary>Рядок контракту для стану.</summary>
    /// <param name="status">Стан.</param>
    public static string StatusName(PermitTypeStatus status) => status.ToString();

    /// <summary>Категорія за рядком контракту (точний збіг, без числових значень).</summary>
    /// <param name="value">Рядок.</param>
    /// <param name="category">Результат.</param>
    public static bool TryCategory(string? value, out PermitCategory category)
    {
        category = default;
        return value is not null
               && !int.TryParse(value, out _)
               && Enum.TryParse(value, ignoreCase: false, out category)
               && Enum.IsDefined(category);
    }
}
