namespace Ecr.Bootstrap.Excel;

/// <summary>
/// Витягує позиції полів у <c>;</c>-рядку з процедур <c>MapAttributes</c>.
/// </summary>
/// <remarks>
/// Порядок полів **індивідуальний для кожної з ~90 таблиць** — саме він
/// потрібен для валідації міграції історії. Парсер робить best-effort:
/// усе, що не розібралося однозначно, іде у звіт, а не в БД.
/// </remarks>
public sealed class VbaMapAttributesParser
{
    /// <summary>Розбирає модуль.</summary>
    /// <param name="vbaSource">Текст модуля <c>.bas</c>.</param>
    /// <returns>Мапінги і перелік місць, які треба звірити руками.</returns>
    public (IReadOnlyList<LegacyFieldMapping> Mappings, IReadOnlyList<string> ManualReview) Parse(string vbaSource)
        => throw new NotImplementedException(
            "TODO: знайти процедури MapAttributes; розібрати Select Case templateRow; " +
            "для кожної гілки зібрати послідовність полів. " +
            "Умовні гілки, обчислювані індекси і все, що не є простим переліком, — " +
            "у ManualReview. Вгадувати заборонено: помилка в порядку полів дає " +
            "правдоподібні, але неправильні дані при міграції історії.");
}

/// <summary>Позиція поля в legacy-рядку.</summary>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="FieldIndex">Позиція в <c>;</c>-рядку, 0-based.</param>
public sealed record LegacyFieldMapping(string TableCode, string ColumnCode, int FieldIndex);
