using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Templates;

/// <summary>
/// Патчить презентаційний шар **опублікованої** версії «на льоту»: підписи,
/// стилі, <c>Ordinal</c>, формати (ФВ-7.2).
/// </summary>
/// <remarks>
/// Це та операція, заради якої існує <c>PresentationRevision</c>: користувач
/// може виправити підпис колонки без клонування версії і без міграції даних.
/// </remarks>
public sealed class PatchPresentationHandler(
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions,
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Застосовує презентаційні зміни.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="patchJson">Перелік змін у форматі <c>{entityType, entityId, field, value}</c>.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Нове значення <c>PresentationRevision</c>.</returns>
    /// <exception cref="BusinessRuleException">
    /// Серед змін є структурна — <c>ECR-TMPL-0409</c>.
    /// </exception>
    /// <exception cref="ConcurrencyConflictException">
    /// Серед змін є <c>Breaking</c>, а на версії вже є документи —
    /// <c>ECR-SCHM-0409</c> (ФВ-7.4).
    /// </exception>
    public async Task<int> PatchAsync(int templateVersionId, string patchJson, int userId, CancellationToken ct)
    {
        // ⛔ Право перевіряється ТУТ (`A7-53`). До цього ендпоінт мав лише
        // `[Authorize]`, тобто оголошене контрактом право не перевіряв ніхто.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Template.Edit", ct)
            .ConfigureAwait(false);

        ArgumentException.ThrowIfNullOrWhiteSpace(patchJson);

        var version = await versions.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var changes = Parse(patchJson);

        if (changes.Count == 0)
        {
            throw new BusinessRuleException(
                "ECR-TMPL-0422",
                "Порожній патч: змінювати нічого.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-TMPL-0422.emptyPatch" });
        }

        // Прохід по відмовах 2: форма кожної зміни і значення за типом поля — ДО бази. Без цього
        // `[null]` давав NullReferenceException, `IsHidden: "abc"` — помилку CONVERT, а підпис
        // `"abc"` ЗБЕРІГАВСЯ (200) і далі кожне читання структури версії падало на розборі JSON.
        RequireWellFormed(changes);

        // ФВ-2.6: порядок — ціле від 0. Без перевірки «abc» доїхало б до
        // `CONVERT(int, @value)` у сховищі і вийшло б 500 замість 422.
        foreach (var change in changes.Where(c => c.Field == "Ordinal"))
        {
            if (!int.TryParse(change.Value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var ordinal)
                || ordinal > MaxOrdinal)
            {
                throw new BusinessRuleException(
                    "ECR-TMPL-0422",
                    $"Порядок має бути цілим числом від 0 до {MaxOrdinal}: '{change.Value}'.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-0422.ordinalInvalid",
                        ["value"] = change.Value ?? string.Empty,
                    });
            }
        }

        // D-234: ширина колонки — ціле 40..800 px; `null` скидає до типової за
        // типом. Той самий ключ, що й у `ColumnDef.SetWidth`.
        foreach (var change in changes.Where(c => c.Field == "WidthPx" && c.Value is not null))
        {
            if (!int.TryParse(change.Value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var width)
                || width < Domain.Entities.Configuration.ColumnDef.MinWidthPx
                || width > Domain.Entities.Configuration.ColumnDef.MaxWidthPx)
            {
                throw new BusinessRuleException(
                    "ECR-TMPL-0422",
                    $"Ширина колонки має бути цілим від {Domain.Entities.Configuration.ColumnDef.MinWidthPx} "
                    + $"до {Domain.Entities.Configuration.ColumnDef.MaxWidthPx} px: '{change.Value}'.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-0422.widthOutOfRange",
                        ["widthPx"] = change.Value ?? string.Empty,
                    });
            }
        }

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Спершу класифікуємо ВСІ зміни і лише потім вирішуємо. Часткове
        // застосування неприпустиме: користувач надіслав патч як одне ціле, і
        // побачити половину застосованих правок гірше, ніж не побачити жодної.
        var classified = changes
            .Select(c => (Name: $"{c.EntityType}.{c.Field}",
                          Class: classifier.Classify(c.EntityType, c.Field, hasDocuments)))
            .ToList();

        // ⛔ ФВ-7.4: `Breaking`-зміна у версії, до якої вже прив'язані
        // документи, — це ВІДМОВА ОПЕРАЦІЇ, а не попередження. Перевірка йде
        // ПЕРЕД загальною забороною ФВ-7.1, і саме тому окремим кодом: ФВ-7.1
        // радить «внесіть це клонуванням версії», і для перейменування
        // `ColumnDef.Code` ця порада ХИБНА. Клон із новим кодом не рятує вже
        // введені дані — комірка посилається на код колонки, і після переходу
        // документів на такий клон значення просто перестають знаходитися.
        // Тобто користувач, який слухняно виконав пораду, дізнався б про
        // втрату не з відмови, а з порожньої форми через місяць.
        //
        // ⚠ Доки цей код не кидав ніхто, `ChangeClassifier` розрізняв
        // `Breaking` лише для діагностики версій (`DiffTemplateVersionsHandler`),
        // і обидві відповіді — «перестав місцями колонки» і «перейменував код
        // на версії з тисячею документів» — доїжджали до клієнта однаковим
        // `ECR-TMPL-0409`.
        var breaking = classified
            .Where(c => c.Class == ChangeClass.Breaking)
            .Select(c => c.Name)
            .ToList();

        if (breaking.Count > 0)
        {
            // ⚠ Саме `ConcurrencyConflictException`: статус відповіді береться
            // з ТИПУ винятку, а цифри коду кажуть 409. `BusinessRuleException`
            // дав би 422 при коді `…0409` — рівно та суперечність усередині
            // одного коду, через яку переписано `ECR-PRD-0422` (`P-25`).
            throw new ConcurrencyConflictException(
                ErrorCodes.BreakingChange,
                "Патч містить зміни ідентичності на версії, до якої вже прив'язані документи: " +
                string.Join(", ", breaking) +
                ". Це відмова, а не попередження (ФВ-7.4): комірки посилаються на код колонки " +
                "і ключ рядка, тому після перейменування вже введені значення перестають знаходитися. " +
                "Клонування версії тут не допомагає — переносити документи буде нікуди.",
                new Dictionary<string, object?>
                {
                    // ⚠ Сам перелік полів лишається структурою `Details["breakingFields"]`
                    // окремим полем для клієнта (той самий прийом, що
                    // `periodKeys`/`structuralFields`): резолвер підставляє лише
                    // string, тому в тексті — лише кількість.
                    ["messageKey"] = "err.ECR-SCHM-0409.presentationPatchBreaking",
                    ["fieldCount"] = breaking.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["breakingFields"] = breaking,
                    ["hasDocuments"] = hasDocuments,
                });
        }

        var violations = classified
            .Where(c => c.Class != ChangeClass.Presentation)
            .Select(c => c.Name)
            .ToList();

        if (violations.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-TMPL-0409",
                "Патч містить структурні зміни, які в опублікованій версії заборонені: " +
                string.Join(", ", violations) + ". Структурні зміни вносяться клонуванням версії (ФВ-7.1).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0409.presentationPatchStructural",
                    ["fieldCount"] = violations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["structuralFields"] = violations,
                });
        }

        // ⛔ ЗМІНА ЗАСТОСОВУЄТЬСЯ. Цього рядка тут не було: обробник розбирав
        // патч, класифікував кожну зміну, відхиляв структурні, піднімав
        // ревізію і писав аудит — і не міняв жодного поля. Підпис колонки
        // лишався старим, ключ кешу ставав новим, і всі клієнти перечитували
        // структуру, щоб побачити те саме. Аудит при цьому запевняв, що зміна
        // відбулася: у `NewJson` лежало значення, якого в базі не було.
        //
        // ⚠ ПЕРЕД інкрементом ревізії: якщо запис упаде, ключ кешу не має
        // змінитися. Новий ключ на стару структуру — це те саме розходження,
        // тільки навпаки.
        // ⛔ Застосування + інкремент ревізії + аудит — ОДНА транзакція (аудит
        // 2026-09-16, §4.2). Без неї обидва raw-SQL виклики
        // (`ApplyPresentationAsync`, `IncrementPresentationRevisionAsync`)
        // комітилися самостійно й одразу, бо `db.Database.CurrentTransaction ==
        // null`: падіння (або виняток у `SaveChangesAsync`) після них, але до
        // запису аудиту лишало ЗМІНЕНИЙ вміст і НОВИЙ ключ кешу ревізії БЕЗ
        // жодного рядка аудиту. Це рівно той дефект «зміна без відповідного
        // рядка аудиту», який `Q-244` називає виправленим в УСІХ сусідніх
        // структурних обробниках (`SheetDefHandlers`, `ColumnDefHandlers`,
        // `TableDefHandlers`, `RowDefHandlers`, `FormulaDefHandlers`,
        // `SaveValidationRuleHandler`, `SwitchRegistrySourceHandler`,
        // `SaveRegistryDefinitionHandler`) — і лише тут він лишався.
        var newRevision = 0;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ C5, порядок блокувань: рядок версії ПЕРШИМ, як у публікації й
            // структурних обробниках чернетки. Без цього патч брав блоки на
            // рядках колонок/аркушів і лише потім (інкрементом ревізії) — на
            // рядку версії, тобто у зворотному порядку до правки чернетки, що
            // тримає версію й хоче той самий рядок колонки: взаємне
            // очікування. Стан тут не перевіряється — презентаційний патч
            // законний і на опублікованій версії.
            await store.LockVersionForUpdateAsync(templateVersionId, innerCt).ConfigureAwait(false);

            await store.ApplyPresentationAsync(templateVersionId, changes, innerCt).ConfigureAwait(false);

            // Інкремент — атомарний statement із OUTPUT (R-B7). Застосунок не
            // призначає нову ревізію, а дізнається її: інстансів ≥ 2.
            newRevision = await store
                .IncrementPresentationRevisionAsync(templateVersionId, innerCt)
                .ConfigureAwait(false);
            version.ApplyPresentationRevision(newRevision);

            var now = clock.UtcNow;
            foreach (var change in changes)
            {
                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        now, templateVersionId, change.EntityType, change.EntityId,
                        ChangeClass.Presentation, "Update",
                        OldJson: null, NewJson: change.Value, ChangeReason: null,
                        ChangedByUserId: userId, CorrelationId: null),
                    innerCt).ConfigureAwait(false);
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⛔ R-11: без інвалідації `GET …/structure` читав прогрітий знімок зі
        // СТАРОЮ ревізією й старими підписами — екран показував «Appearance
        // revision» на крок позаду, і щойно збережений підпис з'являвся лише
        // після наступної правки. Той самий виклик, що в усіх структурних
        // обробниках (`SaveColumnDefHandler`, `SaveSheetDefHandler` та ін.);
        // тут його не було єдиного. Ключ кешу несе ревізію, але знімок під
        // ключем версії лишався попереднім.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        return newRevision;
    }

    /// <summary>Поля-підписи: JSON-об'єкт «мова → текст», колонка NOT NULL.</summary>
    private static readonly HashSet<string> LocalizedFields = new(StringComparer.Ordinal) { "HeaderL10n", "LabelL10n", "NameL10n" };

    /// <summary>Поля-прапорці: <c>CONVERT(bit, …)</c> у сховищі, колонка NOT NULL.</summary>
    private static readonly HashSet<string> FlagFields = new(StringComparer.Ordinal) { "IsHidden", "IsVisible" };

    /// <summary>Межа <c>cfg.ColumnDef.DisplayFormat</c> (<c>nvarchar(50)</c>).</summary>
    private const int MaxDisplayFormatLength = 50;

    /// <summary>
    /// Кожна зміна має тип сутності й поле, а значення — форму, яку прийме колонка: інакше
    /// відмова приходила 500-кою з бази або (для підписів) не приходила зовсім.
    /// </summary>
    private static void RequireWellFormed(List<PresentationChange> changes)
    {
        foreach (var change in changes)
        {
            if (change is null || string.IsNullOrWhiteSpace(change.EntityType) || string.IsNullOrWhiteSpace(change.Field))
            {
                throw new BusinessRuleException(
                    "ECR-TMPL-0422",
                    "Кожна зміна патча має містити entityType, entityId і field.",
                    new Dictionary<string, object?> { ["messageKey"] = "err.ECR-TMPL-0422.patchChangeInvalid" });
            }

            if (!ValueFits(change.Field, change.Value))
            {
                throw new BusinessRuleException(
                    "ECR-TMPL-0422",
                    $"Значення {change.EntityType}.{change.Field} має неприйнятну форму для цього поля.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-0422.presentationValueInvalid",
                        ["entityType"] = change.EntityType,
                        ["field"] = change.Field,
                    });
            }
        }
    }

    private static bool ValueFits(string field, string? value)
    {
        if (LocalizedFields.Contains(field))
        {
            return IsLocalizedText(value);
        }

        if (FlagFields.Contains(field))
        {
            return value is "0" or "1" || bool.TryParse(value, out _);
        }

        return field switch
        {
            "StyleId" => value is null || int.TryParse(
                value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _),
            "DisplayFormat" => value is null || value.Length <= MaxDisplayFormatLength,
            _ => true,
        };
    }

    /// <summary>
    /// Підпис читається <c>LocalizedText.FromJson</c>: об'єкт рядків без повтору мови з точністю
    /// до регістру. <c>"abc"</c>, число чи масив там кидають — і кидали б на КОЖНОМУ читанні версії.
    /// </summary>
    private static bool IsLocalizedText(string? value)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(value);
            return values is not null
                && values.Values.All(v => v is not null)
                && values.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Count;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private const int MaxOrdinal = 1_000_000;

    private static readonly JsonSerializerOptions PatchJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static List<PresentationChange> Parse(string patchJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PresentationChange>>(patchJson, PatchJsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new BusinessRuleException(
                "ECR-TMPL-0422",
                $"Патч не є коректним JSON: {ex.Message}",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-TMPL-0422.patchNotJson" });
        }
    }
}
