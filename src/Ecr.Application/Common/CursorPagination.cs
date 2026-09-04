namespace Ecr.Application.Common;

/// <summary>Параметри курсорної пагінації.</summary>
/// <param name="Limit">Розмір сторінки: типово 50, максимум 500.</param>
/// <param name="Cursor">Курсор попередньої сторінки.</param>
public sealed record CursorRequest(int Limit = 50, string? Cursor = null)
{
    /// <summary>Максимум, більше якого запит відхиляється з <c>400</c>.</summary>
    public const int MaxLimit = 500;

    /// <summary>Перевіряє межі.</summary>
    public bool IsValid => Limit is > 0 and <= MaxLimit;
}
