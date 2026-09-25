using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Останній збережений результат перевірки документа за період (ФВ-5.19).
/// </summary>
/// <remarks>
/// ⛔ Читання додане `W8` (директива №09 п.3, `S-19`). Підсумок ЗБЕРІГАВСЯ
/// давно — <c>ValidateDocumentHandler</c> кладе його в <c>wf.ValidationResult</c>
/// саме тому, що подання не має перевалідовувати документ, — але прочитати
/// його не міг ніхто: <c>IValidationResultStore.GetLatestAsync</c> не мав
/// жодного виклику у всій системі.
///
/// ⚠ Ціна відсутності видна на екрані: перелік зауважень жив рівно до
/// перезавантаження сторінки, і щоб побачити його знову, оператор мусив
/// ЗАПУСТИТИ перевірку заново — на великому документі це три секунди й
/// повний перерахунок правил заради списку, який уже було пораховано.
///
/// ⛔ B-11 (follow-up): <c>MessagesJson</c> зберігає <c>Message</c> уже
/// ВІДРЕНДЕРЕНИМ мовою того, хто ЗАПУСТИВ перевірку
/// (<c>ValidateDocumentHandler</c> → <c>TableValidation.Run(..., currentUser.Language)</c>
/// автора запуску). Читач з іншою мовою інтерфейсу бачив би чужий текст.
/// Тому тут кожне повідомлення ПЕРЕРЕЗОЛВЛЮЄТЬСЯ мовою ПОТОЧНОГО читача за
/// збереженими <c>RuleCode</c>+<c>TableDefId</c> проти живого знімка версії
/// шаблону (той самий снепшот, що вже читає <c>ValidateDocumentHandler</c>
/// через <see cref="IMetadataCache"/>) — формула ідентична
/// <c>ValidationEngine.ValidateScope</c>: <c>rule.MessageL10n.Get(language)
/// ?? rule.Code</c>. Правило, якого в поточному знімку вже немає (видалили
/// чи перейменували з часу останньої перевірки) — лишає збережений текст:
/// застаріле речення краще порожнього чи винятку.
/// </remarks>
public sealed class GetValidationResultHandler(
    IValidationResultStore results,
    IDocumentStore documents,
    IMetadataCache metadata,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на перегляд документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.View";

    /// <summary>
    /// Останній результат; <c>null</c> — перевірку за цей період ще не
    /// запускали.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<ValidationMessage>?> HandleAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        // ⛔ Ті самі дві перевірки, що й у самої валідації (`A7-53`, `ФВ-6.13`):
        // список зауважень несе підписи рядків і колонок, тобто ЗМІСТ
        // документа. Читати збережений результат має право рівно той, хто мав
        // би право його порахувати.
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ B-08: невидимий документ — 404, як і `GET /documents/{id}`, а не 403
        // «NoGrant»: різниця відповідей сама розкривала б, що документ існує.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var summary = await results
            .GetLatestAsync(documentId, periodKey.Value, ct).ConfigureAwait(false);

        if (summary is null)
        {
            return null;
        }

        // ⚠ «Перевірку не запускали» і «перевірка не знайшла зауважень» —
        // РІЗНІ відповіді, і другу не можна показувати замість першої: зелений
        // напис «зауважень немає» під документом, який ніхто не перевіряв, —
        // це та сама неправда, що й порожній дашборд замість збою (`A7-04`).
        var messages = JsonSerializer.Deserialize<List<ValidationMessage>>(summary.MessagesJson) ?? [];

        if (messages.Count == 0)
        {
            // ⚡ Дешевий шлях «зауважень немає»: жодного походу в
            // IDocumentStore/IMetadataCache не потрібно.
            return messages;
        }

        // ⛔ B-11 follow-up: перерезолвити Message мовою ПОТОЧНОГО читача за
        // живим знімком версії шаблону, а не довіряти застарілому тексту,
        // збереженому мовою того, хто запустив перевірку.
        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        var rulesByKey = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .SelectMany(t => t.ValidationRules)
            .ToDictionary(r => (r.TableDefId, r.Code), r => r);

        var language = currentUser.Language;

        return messages
            .Select(m => rulesByKey.TryGetValue((m.TableDefId, m.RuleCode), out var rule)
                ? m with { Message = rule.MessageL10n.Get(language) ?? rule.Code }
                // Правило видалили/перейменували з часу останньої перевірки —
                // лишаємо збережений текст (запасний варіант, не виняток).
                : m)
            .ToList();
    }
}
