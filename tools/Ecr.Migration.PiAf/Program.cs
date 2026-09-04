namespace Ecr.Migration.PiAf;

/// <summary>
/// Переносить історичні дані з PI AF у <c>doc.*</c>.
/// </summary>
/// <remarks>
/// Критерій приймання — **побайтна валідація**: відтворений із наших даних
/// <c>;</c>-рядок має збігатися з тим, що лежить в AF. Без цього твердження
/// «історію перенесено» нічим не підтвердити.
/// </remarks>
internal static class Program
{
    /// <summary>Точка входу.</summary>
    /// <param name="args"><c>--source-id 1 --year 2025 --validate-only</c></param>
    private static Task<int> Main(string[] args)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) читати Event Frames через IExternalDataSource (RTQP для масового читання);\n" +
            "2) розібрати ;-рядки за ext.LegacyColumnMapping.LegacyFieldIndex;\n" +
            "3) зіставити рядки за RowKey через ext.LegacyRowMapping.AfAttributeName;\n" +
            "4) зіставлення записів реєстрів — ЗА БІЗНЕС-КЛЮЧЕМ; зовнішній GUID зберігати " +
            "   ПІСЛЯ зіставлення в dic.RegistryExternalKey, а не замість нього: " +
            "   GUID не переживає перенесення між AF-серверами (ER-I-05);\n" +
            "5) вантажити SqlBulkCopy батчами;\n" +
            "6) ВАЛІДАЦІЯ: для кожного перенесеного рядка відтворити ;-рядок через " +
            "   LegacyRowSerializer і порівняти побайтно з оригіналом; розбіжності — у звіт;\n" +
            "7) --validate-only: не писати, лише звірити вже перенесене.");
}
