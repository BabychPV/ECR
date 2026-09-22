using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Localization;

/// <summary>Помилка одного рядка імпорту.</summary>
/// <param name="Row">Номер запису у файлі; заголовок — 1.</param>
/// <param name="Key">Ключ, як його записано у файлі.</param>
/// <param name="MessageKey">Ключ тексту відмови в каталозі.</param>
public sealed record UiStringImportError(int Row, string Key, string MessageKey);

/// <summary>Звіт імпорту перекладу.</summary>
/// <param name="Added">Нових перекладів.</param>
/// <param name="Updated">Змінених.</param>
/// <param name="Unchanged">Тих самих, що вже в базі.</param>
/// <param name="Errors">Відхилені рядки; є хоч один — не застосовано нічого.</param>
/// <param name="Applied">Чи записано зміни.</param>
/// <param name="Revision">Версія каталогу після імпорту.</param>
public sealed record UiStringImportReport(
    int Added, int Updated, int Unchanged, IReadOnlyList<UiStringImportError> Errors, bool Applied, int Revision);

/// <summary>Експорт перекладу в CSV (<c>BE-13</c> ч.2); право <c>System.ManageLocalization</c>.</summary>
public sealed class ExportUiStringsCsvHandler(
    IUiStringCatalog catalog, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає вміст CSV (без BOM — його додає запис у відповідь).</summary>
    /// <param name="languageCode">Мова перекладу.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<string> HandleAsync(string languageCode, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, SetUiStringHandler.Permission, ct).ConfigureAwait(false);
        await UiStringImportHandler.RequireTranslationLanguageAsync(catalog, languageCode, ct).ConfigureAwait(false);

        var rows = await catalog.ListForExportAsync(languageCode, ct).ConfigureAwait(false);
        var csv = new System.Text.StringBuilder(CsvFormat.Row(
            "key", "scope", UiStringResolver.DefaultLanguage, languageCode, "updatedAt"));

        foreach (var row in rows)
        {
            csv.Append(CsvFormat.Row(
                row.Key,
                row.Scope == UiStringScope.Public ? "public" : "private",
                row.Reference,
                row.Value,
                row.ModifiedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        }

        return csv.ToString();
    }
}

/// <summary>Імпорт перекладу з CSV (<c>BE-13</c> ч.2); право <c>System.ManageLocalization</c>.</summary>
/// <remarks>
/// ⚠ Все або нічого: одна помилка — не записано жодного рядка, інакше
/// термінолог не знає, яка половина файлу вже в базі. Еталон (<c>en</c>)
/// імпортом не переписується: він — мірило плейсхолдерів для всіх інших мов.
/// </remarks>
public sealed class UiStringImportHandler(
    IUiStringCatalog catalog,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Стеля розміру файлу, коли конфіг не задає іншої.</summary>
    public const int DefaultMaxBytes = 1024 * 1024;

    /// <summary>Тип події журналу безпеки.</summary>
    public const string ImportedEventType = "UiStringsImported";

    private const int MaxValueLength = 1000;

    /// <summary>Перевіряє файл і, якщо не <paramref name="dryRun"/> і помилок немає, застосовує.</summary>
    /// <param name="languageCode">Мова перекладу.</param>
    /// <param name="content">Вміст CSV.</param>
    /// <param name="sizeBytes">Розмір файлу.</param>
    /// <param name="maxBytes">Стеля розміру.</param>
    /// <param name="dryRun">Лише звіт, без запису.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<UiStringImportReport> HandleAsync(
        string languageCode, string content, long sizeBytes, int maxBytes, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, SetUiStringHandler.Permission, ct).ConfigureAwait(false);
        await RequireTranslationLanguageAsync(catalog, languageCode, ct).ConfigureAwait(false);
        RequireSize(sizeBytes, maxBytes);

        var records = CsvReader.Parse(content);
        var header = records.Count > 0 ? records[0] : [];
        var keyColumn = IndexOf(header, "key");
        var valueColumn = IndexOf(header, languageCode);
        if (keyColumn < 0 || valueColumn < 0)
        {
            throw Invalid("err.ECR-REQ-0422.uiStringCsvHeader", "Заголовок CSV не має колонок key і мови.", languageCode);
        }

        var existing = (await catalog.ListForExportAsync(languageCode, ct).ConfigureAwait(false))
            .ToDictionary(r => r.Key, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<UiStringImportError>();
        var writes = new List<UiStringWrite>();
        var (added, updated, unchanged) = (0, 0, 0);
        var now = clock.UtcNow;
        var userId = currentUser.UserId;

        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            if (record.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var key = Cell(record, keyColumn).Trim();
            var value = CsvReader.UnescapeFormula(Cell(record, valueColumn));

            // Порожня клітинка — «ще не перекладено»: власний експорт неповної мови
            // має імпортуватися назад. Зняти переклад можна лише в редакторі.
            if (value.Length == 0)
            {
                continue;
            }

            var known = existing.TryGetValue(key, out var current);
            var messageKey =
                !seen.Add(key) ? "err.ECR-REQ-0422.uiStringDuplicateKey"
                : !known || current is null ? "err.ECR-REQ-0422.uiStringUnknownKey"
                : string.IsNullOrWhiteSpace(value) ? "err.ECR-REQ-0422.uiStringEmptyValue"
                : value.Length > MaxValueLength ? "err.ECR-REQ-0422.uiStringTooLong"
                : !UiStringResolver.SamePlaceholders(current.Reference, value) ? "err.ECR-REQ-0422.placeholderMismatch"
                : null;

            if (messageKey is not null || current is null)
            {
                errors.Add(new UiStringImportError(i + 1, key, messageKey ?? "err.ECR-REQ-0422.uiStringUnknownKey"));
                continue;
            }

            if (string.Equals(current.Value, value, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            _ = current.Value is null ? added++ : updated++;
            writes.Add(new UiStringWrite(key, languageCode, value, current.Scope, userId, now));
        }

        if (dryRun || errors.Count > 0 || writes.Count == 0)
        {
            var revision = await catalog.GetRevisionAsync(ct).ConfigureAwait(false);
            return new UiStringImportReport(added, updated, unchanged, errors, Applied: false, revision);
        }

        var after = await catalog.SetManyAsync(writes, ct).ConfigureAwait(false);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                now,
                ImportedEventType,
                TargetUserId: null,
                TargetRoleId: null,
                DetailsJson: JsonSerializer.Serialize(new { languageCode, added, updated }),
                ChangedByUserId: userId!.Value,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return new UiStringImportReport(added, updated, unchanged, errors, Applied: true, after);
    }

    private static void RequireSize(long length, int maxBytes)
    {
        if (length > maxBytes)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Файл {length} байт, стеля {maxBytes}.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-REQ-0422.uiStringCsvTooLarge",
                    ["size"] = length,
                    ["max"] = maxBytes,
                });
        }
    }

    /// <summary>Мова перекладу: не еталон і є в реєстрі (інакше FK дав би 500).</summary>
    internal static async Task RequireTranslationLanguageAsync(
        IUiStringCatalog catalog, string? languageCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(languageCode)
            || string.Equals(languageCode, UiStringResolver.DefaultLanguage, StringComparison.OrdinalIgnoreCase)
            || !await catalog.LanguageExistsAsync(languageCode, ct).ConfigureAwait(false))
        {
            throw Invalid("err.ECR-REQ-0422.uiStringCsvLanguage", "Мова не придатна для перекладу.", languageCode ?? string.Empty);
        }
    }

    private static BusinessRuleException Invalid(string messageKey, string message, string lang)
        => new(
            ErrorCodes.RequestInvalid,
            message,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["messageKey"] = messageKey, ["lang"] = lang });

    private static int IndexOf(IReadOnlyList<string> header, string name)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Cell(IReadOnlyList<string> record, int index)
        => index < record.Count ? record[index] : string.Empty;
}
