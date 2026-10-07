// src/Ecr.Application/Documents/DocumentHeaderHandlers.cs
using System.Globalization;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Documents;

/// <summary>Поточні значення шапки документа — усі поля версії шаблону з їхнім станом.</summary>
public sealed class GetDocumentHeaderHandler(
    IDocumentStore documents,
    IMetadataCache metadata,
    IDocumentHeaderStore headers,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на перегляд документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.View";

    /// <summary>Повертає шапку документа.</summary>
    /// <exception cref="NotFoundException">Документа немає або він не видимий.</exception>
    public async Task<DocumentHeaderDto> HandleAsync(long documentId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireInAnyProjectAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        // ⛔ Той самий шлях, що ValidateDocumentHandler: право перевіряється
        // ТУТ, а не лише в контролері (A7-53) — шапка несе зміст документа.
        // ⛔ B-08: невидимий документ — 404, як і `GET /documents/{id}`, а не 403
        // «NoGrant»: різниця відповідей сама розкривала б, що документ існує.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);

        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);

        return HeaderVersion.ToDto(snapshot, values);
    }
}

/// <summary>Версія значень шапки і збирання відповіді — спільні для <c>GET</c> і <c>PATCH</c>.</summary>
internal static class HeaderVersion
{
    /// <summary>Відповідь: усі поля версії шаблону, значення і версія.</summary>
    public static DocumentHeaderDto ToDto(
        TemplateVersionSnapshot snapshot, IReadOnlyDictionary<int, Domain.ValueObjects.DocumentHeaderValueData> values)
    {
        var fields = snapshot.HeaderFields
            .OrderBy(f => f.Ordinal)
            .Select(f => new DocumentHeaderFieldDto(
                f.Id, f.Code, f.LabelL10n, f.DataType, f.IsRequired,
                values.TryGetValue(f.Id, out var value) ? HeaderValueMapping.ToRuleValue(value) : null,
                f.LookupRegistryDefId))
            .ToList();

        return new DocumentHeaderDto(fields, Of(values));
    }

    /// <summary>Версія — хеш збережених значень шапки документа.</summary>
    /// <remarks>
    /// ⚠ Хеш ЗНАЧЕНЬ, а не лічильник: окремої колонки версії в
    /// <c>doc.DocumentHeaderValue</c> немає (міграція поза цією задачею), а
    /// <c>doc.Document.RowVersion</c> змінюється від кожної правки комірки —
    /// шапка тоді «конфліктувала б» із сусідом, який її не торкався. Наслідок
    /// хешу чесний і безпечний: правка, що повернула рівно те саме значення
    /// (A → B → A), конфліктом не вважається — затирати тут нічого.
    ///
    /// ⚠ Рядок кодується з довжиною (<c>7:Kashagan</c>), а число —
    /// нормалізованим: «12.5» і «12.5000000000000000» — одне значення.
    /// </remarks>
    public static string Of(IReadOnlyDictionary<int, Domain.ValueObjects.DocumentHeaderValueData> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var text = new System.Text.StringBuilder();
        foreach (var (fieldId, value) in values.OrderBy(pair => pair.Key))
        {
            text.Append(fieldId.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(value.IsEmpty ? '1' : '0')
                .Append('|').Append(value.ValueString is { } s ? $"{s.Length.ToString(CultureInfo.InvariantCulture)}:{s}" : "-")
                .Append('|').Append(value.ValueNumeric is { } n ? Number(n) : "-")
                .Append('|').Append(value.ValueDate is { } d ? d.ToString("O", CultureInfo.InvariantCulture) : "-")
                .Append('|').Append(value.ValueBool is { } b ? (b ? "1" : "0") : "-")
                .Append('|').Append(value.ValueRegistryEntryId?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append('|').Append(value.ValueUnitId?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append('\n');
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    /// <summary>Значення поля текстом для журналу; <c>null</c> — рядка немає або явна порожнеча.</summary>
    public static string? AuditText(Domain.ValueObjects.DocumentHeaderValueData? value)
        => HeaderValueMapping.ToRuleValue(value) switch
        {
            null => null,
            decimal n => Number(n),
            DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };

    private static string Number(decimal value)
        => value.ToString("0.############################", CultureInfo.InvariantCulture);
}

/// <summary>
/// Оновлює значення полів шапки документа.
/// </summary>
/// <remarks>
/// ⚠ Судження про право (немає готового прецеденту рівно для цього рівня
/// грануляції): без окремого функціонального права, лише грант <c>Write</c>
/// на проєкт документа — той самий, грантово-орієнтований підхід (без
/// <c>PermissionCheck.RequireAsync</c>), що вже застосовує
/// <c>PatchCellsHandler</c> для звичайного редагування даних документа
/// (`02-contracts.md` §9: <c>PATCH …/cells</c> — «через
/// <c>IAccessDecisionService</c>», без права). Право
/// <c>Document.ChangeKey</c> навмисно НЕ використовується: воно означає
/// контрольовану, окремо аудитовану операцію зміни бізнес-ключа (ФВ-3.9), а
/// шапка — звичайне редагування даних, ближче до комірок, ніж до рекею.
/// В моделі доступу немає <c>ResourceKind.Document</c>, тож найближчий
/// рівень грануляції — проєктний, дослівно як у
/// <see cref="ChangeDocumentKeyHandler"/> для 404-проти-403.
///
/// ⛔ C2 (enterprise-аудит коректності). Доти грант був ЄДИНОЮ перевіркою:
/// шапку поданого чи затвердженого документа правили з <c>200</c>, у
/// журналі не лишалося нічого, формули з <c>HDR.*</c> не перераховувалися, а
/// дві правки з однієї версії мовчки затирали одна одну. Тепер:
/// <list type="number">
/// <item><b>Стан</b> — <see cref="EditRules.CanEdit"/>, те саме правило, що
/// для комірки. Шапка не має ні аркуша, ні періоду й належить документу
/// цілком, тому в рішення йде НАЙСУВОРІШИЙ стан серед усіх аркуш × період
/// документа (той самий принцип, що <c>DocumentKeyChange.EnsureChangeable</c>
/// для ключа, ФВ-3.9: подане вже бачили погоджувачі, а зріз подання несе й
/// шапку). Закритий період окремо шапку не замикає — закриті періоди не
/// перераховуються (ФВ-9.7), тож правка до них не доходить; замикає, лише
/// коли закрито ВСІ періоди проєкту.</item>
/// <item><b>Аудит</b> — <c>aud.SecurityEvent</c> <see cref="EventType"/> зі
/// старим і новим значенням кожного зміненого поля, у ТІЙ САМІЙ транзакції,
/// що й запис (як <c>DocumentKeyChanged</c>, але до коміту).</item>
/// <item><b>Перерахунок</b> — після коміту, повний (<c>IRecalculationJob</c>)
/// на кожен період, куди перерахунок має право писати: <c>HDR.*</c> читають і
/// формули шаблону (<c>RecalculationService</c>), і методології, а насіння
/// «змінена комірка» в шапки немає.</item>
/// <item><b>Конкурентність</b> — обов'язкова <c>baseVersion</c> проти
/// <see cref="HeaderVersion.Of"/>, звірена ПІД <c>UPDLOCK</c> на рядку
/// документа: дві одночасні правки з однієї версії — одна <c>200</c>, друга
/// <c>409</c>.</item>
/// </list>
/// </remarks>
public sealed class PatchDocumentHeaderHandler(
    IDocumentStore documents,
    IMetadataCache metadata,
    IDocumentHeaderStore headers,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    IPeriodStore periods,
    IDocumentKeyStore documentLock,
    IDocumentDeletionStore workflowFacts,
    IUnitOfWork uow,
    IAuditWriter audit,
    IBackgroundJobScheduler jobs,
    Domain.Abstractions.IClock clock,
    ISheetEditGate documentGate,
    IRegistryStore registries)
{
    /// <summary>Тип події журналу безпеки.</summary>
    public const string EventType = "DocumentHeaderChanged";

    /// <summary>Оновлює шапку і повертає її повний, щойно збережений стан.</summary>
    /// <exception cref="NotFoundException">Документа немає, він не видимий, або код поля невідомий.</exception>
    /// <exception cref="AccessDeniedException">
    /// Анонімний запит; немає гранта на запис у проєкт документа; або стан документа
    /// правку не допускає — <c>ECR-ACCS-0403</c> з <c>reason</c> від <see cref="EditRules"/>.
    /// </exception>
    /// <exception cref="BusinessRuleException">
    /// Значення не відповідає типу чи обов'язковості поля; або немає <c>baseVersion</c>.
    /// </exception>
    /// <exception cref="ConcurrencyConflictException">
    /// <c>baseVersion</c> застаріла — <c>ECR-DOC-0409</c>.
    /// </exception>
    public async Task<DocumentHeaderDto> HandleAsync(
        long documentId, PatchDocumentHeaderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Потрібна автентифікація.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var document = await documents.FindAsync(documentId, new PeriodKeyFilter(null), ct).ConfigureAwait(false);
        if (document is null || !profile.SeesDocumentsOf(document.ProjectId))
        {
            throw new NotFoundException(
                ErrorCodes.DocumentNotFound, $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Write)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта на запис у проєкт {document.ProjectId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectWriteGrant",
                    ["projectId"] = document.ProjectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (string.IsNullOrWhiteSpace(request.BaseVersion))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Запит на зміну шапки має нести baseVersion — версію, з якої почалася правка.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.headerBaseVersion" });
        }

        var project = await periods.FindProjectAsync(document.ProjectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.DocumentNotFound, $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });

        // ⚠ Швидка відмова ДО блокувань: симуляція, архів, закритий рік, грант.
        // Стан аркушів тут ще не відомий — його читає транзакція нижче під
        // блокуванням, і те саме правило питається вдруге вже з ним.
        EnsureEditable(profile, project, DocumentStatus.Draft);

        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var fieldsByCode = snapshot.HeaderFields.ToDictionary(f => f.Code, StringComparer.Ordinal);

        var toSave = new Dictionary<int, Domain.ValueObjects.DocumentHeaderValueData>();

        // ✎ 2026-09-29: число текстом читається за мовою користувача.
        var culture = Localization.NumberCulture.ForLanguage(currentUser.Language);

        foreach (var change in request.Fields)
        {
            if (!fieldsByCode.TryGetValue(change.Code, out var field))
            {
                throw new NotFoundException(
                    ErrorCodes.HeaderFieldNotFound,
                    $"У версії шаблону {templateVersionId} немає поля шапки з кодом «{change.Code}».",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-HDR-0404.headerField",
                        ["templateVersionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
                        ["headerFieldCode"] = change.Code,
                    });
            }

            var data = change.IsEmpty
                ? Domain.ValueObjects.DocumentHeaderValueData.Empty
                : HeaderValueReader.Read(change.Value, field, culture) ?? Domain.ValueObjects.DocumentHeaderValueData.Empty;

            if (field.ValidateValue(data) is { } errorCode)
            {
                throw new BusinessRuleException(
                    errorCode,
                    $"Значення поля шапки «{field.Code}» не відповідає типу чи обов'язковості.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-HDR-0422.validationBlocked",
                        ["headerFieldCode"] = field.Code,
                    });
            }

            toSave[field.Id] = data;
        }

        // ⛔ D-12: неіснуючий запис довідника в полі Lookup — 422 до запису, а не сире
        // порушення FK_DocumentHeaderValue_Entry (500). Один запит на весь батч.
        var lookupEntries = toSave
            .Where(p => p.Value.ValueRegistryEntryId is not null)
            .Select(p => (FieldId: p.Key, EntryId: p.Value.ValueRegistryEntryId!.Value))
            .ToList();
        if (lookupEntries.Count > 0)
        {
            var existing = await registries.FindExistingEntryIdsAsync(
                [.. lookupEntries.Select(e => e.EntryId).Distinct()], ct).ConfigureAwait(false);
            var missing = lookupEntries.FirstOrDefault(e => !existing.Contains(e.EntryId));
            if (missing != default)
            {
                var missingCode = snapshot.HeaderFields.First(f => f.Id == missing.FieldId).Code;
                throw new BusinessRuleException(
                    ErrorCodes.HeaderValueInvalid,
                    $"Значення поля шапки «{missingCode}» посилається на неіснуючий запис довідника {missing.EntryId}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-HDR-0422.validationBlocked",
                        ["headerFieldCode"] = missingCode,
                    });
            }
        }

        var codeById = snapshot.HeaderFields.ToDictionary(f => f.Id, f => f.Code);
        var fieldsById = snapshot.HeaderFields.ToDictionary(f => f.Id);
        var changed = 0;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ L6-02 / L6-06: першими діями — структура документа (спільно; перенос
            // версії прибирає й переносить поля шапки) і шапка (винятково: подання
            // бере її спільно, валідує й кладе у зріз). Без цього правка, що
            // комітилась посеред подання, давала зріз із невалідованою шапкою або
            // змінювала шапку вже поданого аркуша. Порядок той самий, що в подання:
            // `doc-structure` → `doc-header`, далі стани аркушів і рядок документа.
            DocumentStructure.EnsureUnchanged(
                await documentGate.EnterStructureAsync(documentId, exclusive: false, innerCt).ConfigureAwait(false),
                templateVersionId,
                documentId);
            await documentGate.EnterHeaderAsync(documentId, exclusive: true, innerCt).ConfigureAwait(false);

            changed = await PersistAsync(
                documentId, project, profile, request.BaseVersion, toSave, codeById, fieldsById, userId, innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⚠ Після коміту й поза транзакцією — як у PatchCellsHandler: задача,
        // поставлена до коміту, під RCSI прочитала б стару шапку.
        if (changed > 0)
        {
            // ⛔ L1-17: витісняти виконуваний перерахунок (Exclusive) може лише власник Calculation.Recalculate —
            // як на RecalculateDocumentHandler; решта (лише Write на проєкт) ставить без витіснення (Coalesced).
            var mayPreempt = PermissionCheck.IsGrantedIn(profile, RecalculateDocumentHandler.PreemptPermission, project.Id);
            await EnqueueRecalculationAsync(documentId, project, userId, mayPreempt, ct).ConfigureAwait(false);
        }

        var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
        return HeaderVersion.ToDto(snapshot, values);
    }

    /// <summary>
    /// Тіло транзакції: блокування, стан, версія, запис, «дотик» документа й
    /// аудит. Повертає, скільки полів справді змінилося.
    /// </summary>
    /// <remarks>
    /// ⛔ Порядок блокувань — ЄДИНИЙ для всіх, хто бере обидва ресурси
    /// (<see cref="ChangeDocumentKeyHandler"/>, <see cref="DeleteDocumentHandler"/>):
    /// спершу стани аркушів (<c>UPDLOCK, HOLDLOCK</c> по <c>DocumentId</c>), потім
    /// рядок документа (<c>UPDLOCK</c>). Перше не дає паралельному поданню
    /// вставити чи змінити стан аркуша, доки правка не зафіксована, — тобто
    /// подання або бачить уже нову шапку, або правка бачить уже поданий аркуш і
    /// відмовляє. Друге серіалізує дві правки шапки між собою (звірка версії й
    /// запис — атомарні).
    ///
    /// ⛔ Що було: спершу документ, потім стани — і це дедлок (1205,
    /// <c>DocumentLockOrderDeadlockTests</c>). Правка тримала U на документі й
    /// чекала на стан, який тримає подання чи видалення; ті, своєю чергою,
    /// чекали на документ: видалення — прямо (<c>DELETE doc.Document</c> після
    /// станів), подання — через перевірку ключа <c>FK_ApprEvent_Doc</c> (S),
    /// що стає в чергу за винятковим очікувачем на документі (дотик від правки
    /// комірки будь-якого аркуша; під оптимізованим блокуванням <c>UPDATE</c>
    /// чекає одразу в X). Колишній рядок «правкам комірок ні те, ні те не
    /// заважає» був хибним: дотик документа — теж учасник циклу.
    ///
    /// ⚠ Тепер правка, що чекає на стани, не тримає нічого на документі, і
    /// подання, видалення й дотики проходять повз неї.
    ///
    /// ⚠ Дві правки шапки серіалізує вже діапазон <c>HOLDLOCK</c> по
    /// <c>DocumentId</c> (мутація «без <c>UPDLOCK</c> на документі» лишала
    /// одночасний тест зеленим, «без обох» — червоним). <c>UPDLOCK</c> на
    /// документі лишається свідомо: він не залежить від того, чи є в документа
    /// рядки стану й індекс під діапазон.
    /// </remarks>
    private async Task<int> PersistAsync(
        long documentId,
        Domain.Entities.Documents.Project project,
        AccessProfile profile,
        string baseVersion,
        Dictionary<int, Domain.ValueObjects.DocumentHeaderValueData> requested,
        Dictionary<int, string> codeById,
        IReadOnlyDictionary<int, HeaderFieldDef> fieldsById,
        int userId,
        CancellationToken ct)
    {
        var facts = await workflowFacts.LockWorkflowFactsAsync(documentId, ct).ConfigureAwait(false);
        _ = await documentLock.FindForUpdateAsync(documentId, ct).ConfigureAwait(false);

        // ⛔ Причина відмови не називає стан (і наявність) схованого від читача аркуша: найсуворіший
        // стан рахується по видимих, а схований поданий/погоджений аркуш дає ту саму загальну
        // «подано» (Submitted), без розрізнення Submitted/Approved.
        var hidden = await DocumentSheetVisibility
            .HiddenStatesAsync(access, profile, documentId, facts.SheetStates, ct).ConfigureAwait(false);
        var strictest = Strictest(facts.SheetStates.Except(hidden));
        if (strictest == DocumentStatus.Draft && hidden.Any(s => s.Status is DocumentStatus.Submitted or DocumentStatus.Approved))
        {
            strictest = DocumentStatus.Submitted;
        }

        EnsureEditable(profile, project, strictest);

        // ⚠ Читання ПІСЛЯ блокування: під RCSI знімок береться на початку
        // оператора, тож цей оператор бачить правку, яка зафіксувалася, поки
        // ми чекали на UPDLOCK.
        var current = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
        var actual = HeaderVersion.Of(current);
        if (!string.Equals(actual, baseVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.DocumentSubmitted,
                $"Шапку документа {documentId} змінили після того, як її прочитали.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0409.headerStale",
                    ["version"] = actual,
                });
        }

        // ⚠ Лише справді змінені поля: запис «того самого» не лишає ні рядка
        // в журналі, ні перерахунку, ні нової дати зміни документа.
        var changes = requested
            .Where(pair => !Equals(current.GetValueOrDefault(pair.Key), pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        if (changes.Count == 0)
        {
            return 0;
        }

        await EnsureLookupEntriesUsableAsync(project, changes, fieldsById, ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        await headers.SaveValuesAsync(documentId, changes, ct).ConfigureAwait(false);
        await documents.TouchAsync(documentId, userId, now, ct).ConfigureAwait(false);

        var fields = changes
            .OrderBy(pair => codeById.GetValueOrDefault(pair.Key, string.Empty), StringComparer.Ordinal)
            .Select(pair => new
            {
                code = codeById.GetValueOrDefault(pair.Key, string.Empty),
                oldValue = HeaderVersion.AuditText(current.GetValueOrDefault(pair.Key)),
                newValue = HeaderVersion.AuditText(pair.Value),
            })
            .ToList();

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                now, EventType, TargetUserId: null, TargetRoleId: null,
                System.Text.Json.JsonSerializer.Serialize(new { documentId, projectId = project.Id, fields }),
                userId, currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return changes.Count;
    }

    /// <summary>
    /// PS-P1D (D-11): поле шапки <c>Lookup</c> (наприклад, <c>Permit</c>) не бере запис, якого пікер не
    /// пропонує: чужий довідник, видалений, вимкнений чи нечинний жодного дня звітного вікна документа.
    /// </summary>
    /// <remarks>
    /// ⛔ Дзеркало <c>PatchCellsHandler.CheckLookupStandings</c> (<c>C7</c>), код <c>ECR-HDR-4223</c>.
    /// Доти шапка перевіряла лише існування запису (D-12), тож дозвіл із закінченим строком лягав у шапку.
    ///
    /// ⚠ Дата чинності — не «сьогодні» і не кінець періоду (у шапки періоду немає), а ВІКНО проєкту
    /// <c>[PeriodStart, PeriodEnd]</c>: запис придатний, якщо чинний бодай один день вікна — той самий
    /// відрізковий принцип, що в ФВ-5.20 (дозвіл, чинний до 15 червня, червень покриває). «Сьогодні»
    /// відхиляло б дозвіл, чинний увесь минулий звітний рік, при внесенні шапки документа за цей рік.
    ///
    /// ⚠ Перевіряються лише ЗМІНЕНІ поля (<paramref name="changes"/> уже без незмінених): запис, що став
    /// нечинним ПІСЛЯ вибору, не блокує правку інших полів — повтор того самого не є новим вибором.
    /// Очищення поля сюди не потрапляє (порожнє значення відхиляє <c>ValidateValue</c> для обов'язкового).
    /// </remarks>
    private async Task EnsureLookupEntriesUsableAsync(
        Domain.Entities.Documents.Project project,
        Dictionary<int, Domain.ValueObjects.DocumentHeaderValueData> changes,
        IReadOnlyDictionary<int, HeaderFieldDef> fieldsById,
        CancellationToken ct)
    {
        var lookups = changes
            .Where(pair => pair.Value.ValueRegistryEntryId is not null && fieldsById.ContainsKey(pair.Key))
            .Select(pair => (Field: fieldsById[pair.Key], EntryId: pair.Value.ValueRegistryEntryId!.Value))
            .ToList();

        if (lookups.Count == 0)
        {
            return;
        }

        var standings = (await registries
                .FindEntryStandingsAsync([.. lookups.Select(l => l.EntryId).Distinct()], ct)
                .ConfigureAwait(false))
            .ToDictionary(standing => standing.Id);

        var windowEnd = project.PeriodEnd.AddDays(1);

        foreach (var (field, entryId) in lookups.OrderBy(l => l.Field.Code, StringComparer.Ordinal))
        {
            // Немає запису — це вже відхилив D-12 (`ECR-HDR-0422`) до транзакції.
            if (!standings.TryGetValue(entryId, out var standing))
            {
                continue;
            }

            var messageKey =
                field.LookupRegistryDefId is { } registryId && standing.RegistryDefId != registryId
                    ? "err.ECR-HDR-4223.foreignRegistry"
                : standing.IsDeleted ? "err.ECR-HDR-4223.deletedEntry"
                : !standing.IsActive ? "err.ECR-HDR-4223.inactiveEntry"
                : !new Domain.ValueObjects.ValidityWindow(standing.ValidFrom, standing.ValidTo)
                    .OverlapsSegment(project.PeriodStart, windowEnd) ? "err.ECR-HDR-4223.entryNotValidInWindow"
                : null;

            if (messageKey is null)
            {
                continue;
            }

            throw new BusinessRuleException(
                ErrorCodes.HeaderEntryNotUsable,
                $"Запис довідника {entryId}, обраний для поля шапки «{field.Code}», не можна використати ({messageKey}).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = messageKey,
                    ["headerFieldCode"] = field.Code,
                    ["windowFrom"] = project.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["windowTo"] = project.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>
    /// Рішення «чи можна правити шапку» — <see cref="EditRules.CanEdit"/> на
    /// рівні проєкту (без аркуша, таблиці й колонки).
    /// </summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="project">Проєкт документа разом із періодами.</param>
    /// <param name="sheetStatus">Найсуворіший стан аркуша × період документа.</param>
    private static void EnsureEditable(
        AccessProfile profile, Domain.Entities.Documents.Project project, DocumentStatus sheetStatus)
    {
        // ⚠ Шапка не належить періоду: закритий рік (усі періоди Closed)
        // замикає її, окремий закритий місяць — ні (див. remarks класу).
        var periodState = project.Periods.Count > 0 && project.Periods.All(p => p.State == PeriodState.Closed)
            ? PeriodState.Closed
            : PeriodState.Open;

        var decision = EditRules.CanEdit(profile, new CellAccessContext(
            project.Id, SheetDefId: 0, TableDefId: 0, ColumnDefId: 0,
            project.Status, project.IsArchiving, periodState, OutOfAccessWindow: false,
            sheetStatus, ColumnIsComputed: false, ColumnIsReadOnly: false, RowIsReadOnly: false));

        if (decision.IsAllowed)
        {
            return;
        }

        if (decision.Reason is EditDenyReason.NoGrant or EditDenyReason.InsufficientGrantLevel)
        {
            // ⚠ Той самий код і ключ, що й перевірка гранта вище: для
            // користувача «немає гранта» — одна відмова, хоч би хто її спіймав.
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта на запис у проєкт {project.Id}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectWriteGrant",
                    ["projectId"] = project.Id.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⚠ Код — той самий, що для комірок поданого аркуша (`ECR-ACCS-0403` із
        // `reason`); відрізняє лише ключ тексту: «комірок у батчі» тут немає.
        throw new AccessDeniedException(
            "ECR-ACCS-0403",
            $"Шапку документа не можна змінити: {decision.Reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.headerLocked",
                ["reason"] = decision.Reason.ToString(),
            });
    }

    /// <summary>Найсуворіший стан серед аркуш × період: <c>Approved</c> над <c>Submitted</c> над рештою.</summary>
    private static DocumentStatus Strictest(IEnumerable<Domain.Entities.Workflow.ApprovalState> states)
    {
        var result = DocumentStatus.Draft;
        foreach (var state in states)
        {
            if (state.Status == DocumentStatus.Approved)
            {
                return DocumentStatus.Approved;
            }

            if (state.Status == DocumentStatus.Submitted)
            {
                result = DocumentStatus.Submitted;
            }
        }

        return result;
    }

    /// <summary>
    /// Повний перерахунок документа за кожен період, куди перерахунок має право
    /// писати (<see cref="Calculations.RecalculationWritePolicy"/>).
    /// </summary>
    /// <remarks>
    /// ⚠ Поданих аркушів тут немає за побудовою — інакше правку відхилило б
    /// <see cref="EnsureEditable"/>; закриті періоди відсіює правило (ФВ-9.7), а
    /// <c>Scheduled</c> пропускається, бо даних там ще немає. Сама задача
    /// перевіряє те саме правило вдруге на шляху запису.
    ///
    /// ⚠ Ціль витіснення — та сама пара «документ × період», що в
    /// <see cref="RecalculateDocumentHandler"/>: дві задачі над тим самим
    /// перемикали б актуальність прогону навперегін.
    /// </remarks>
    private async Task EnqueueRecalculationAsync(
        long documentId, Domain.Entities.Documents.Project project, int userId, bool mayPreempt, CancellationToken ct)
    {
        foreach (var period in project.Periods.OrderBy(p => p.PeriodKeyValue))
        {
            if (period.State == PeriodState.Scheduled
                || Calculations.RecalculationWritePolicy.Check(
                    period.State, hasSubmittedSheets: false, hasClosedPeriodApproval: false)
                != Calculations.RecalculationWriteDenial.None)
            {
                continue;
            }

            var target = RecalculateDocumentHandler.TargetOf(documentId, period.Key);
            var payload = new
            {
                DocumentId = documentId,
                PeriodKey = period.PeriodKeyValue,
                TriggeredByUserId = (int?)userId,
                SheetDefId = (int?)null,
            };

            if (mayPreempt)
            {
                await jobs.EnqueueExclusiveAsync<IRecalculationJob>(target, payload, ct, userId).ConfigureAwait(false);
            }
            else
            {
                await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, payload, ct, userId).ConfigureAwait(false);
            }
        }
    }
}
