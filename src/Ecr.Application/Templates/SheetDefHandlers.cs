// src/Ecr.Application/Templates/SheetDefHandlers.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>
/// Записує аркуш версії-чернетки: перший вертикальний зріз авторства
/// структури шаблону через API (<c>ФВ-2.1</c>..<c>ФВ-2.5</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього зрізу <c>SheetDef</c>/<c>TableDef</c>/<c>ColumnDef</c> створював
/// лише <c>Ecr.DataGen</c>, а <c>RowDef</c> — лише тести: авторства структури
/// шаблону не існувало в API взагалі (аудит, <c>S-03</c>/<c>S-04</c>). Цей
/// обробник — еталон, за яким наступні зрізи (<c>TableDef</c>+<c>ColumnDef</c>,
/// <c>RowDef</c>, <c>FormulaDef</c>, <c>ValidationRule</c>,
/// <c>PeriodAccessRuleDef</c>) повторюють той самий патерн.
///
/// ⚠ <c>PUT</c>, а не <c>POST</c>: адресою аркуша є його <b>код</b>, і задає
/// його викликач (<c>D2-147</c>) — та сама форма, що й
/// <see cref="SaveTableRelationHandler"/>. Створення й зміна — одна дія:
/// повторний запит із тим самим тілом дає той самий стан.
///
/// ⛔ Читає й пише через <see cref="ITemplateVersionStore.GetWithStructureAsync"/>,
/// а НЕ через <see cref="IMetadataCache"/>. Кеш метаданих ключується
/// презентаційною ревізією (<c>v{id}:r{rev}</c>, <c>ФВ-2.5</c>) — вона
/// розрахована на те, що структура опублікованої версії незмінна. Чернетка ж
/// саме тут стає змінною: додавання аркуша не піднімає
/// <c>PresentationRevision</c> (це не презентаційна правка), тож без явного
/// <see cref="IMetadataCache.InvalidateAsync"/> нижче другий <c>GET
/// …/structure</c> у прогрітому кеші мовчки повернув би ЗНІМОК ДО ДОДАВАННЯ —
/// той самий клас дефекту, що й непрогрітий кеш чернетки, тільки у зворотний
/// бік (застаріле, а не порожнє). Перевірено експериментально: рядок нижче
/// прибирали і бачили саме це.
/// </remarks>
public sealed class SaveSheetDefHandler(
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює або змінює аркуш.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="code">Код аркуша.</param>
    /// <param name="command">Налаштування аркуша.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Збережений аркуш.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія структурно заморожена (<c>ECR-TMPL-0409</c>).
    /// </exception>
    public async Task<SheetDto> HandleAsync(
        int templateVersionId, string code, SaveSheetDefCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        // ⛔ Повний граф версії, ВІДСТЕЖУВАНИЙ — так само, як публікація
        // (`PublishTemplateVersionHandler`). Порожній `IRepository.FindAsync`
        // без `Include` віддав би `Sheets` порожнім, і перевірка коду на
        // дублікат не побачила б жодного наявного аркуша.
        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⛔ ПЕРШИМ ділом і доменом. Усе інше нижче має сенс лише там, де
        // правка взагалі дозволена (ФВ-7.1).
        version.EnsureStructurallyMutable();

        var ecrCode = EcrCode.Create(code);
        var name = new LocalizedText(new Dictionary<string, string>(command.NameL10n, StringComparer.OrdinalIgnoreCase));

        var existing = version.Sheets.FirstOrDefault(
            s => string.Equals(s.Code, ecrCode.Value, StringComparison.Ordinal));

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Класифікація йде і для СТВОРЕННЯ теж: клас пишеться в аудит
        // структурних змін незалежно від того, чи міг він щось зламати —
        // «додано» без класу виглядало б у журналі так само, як зміна, яку
        // ніхто не класифікував.
        var change = existing is null
            ? classifier.ClassifyAddition(nameof(SheetDef))
            : classifier.Classify(nameof(SheetDef), nameof(SheetDef.IsMandatory), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Зміна");

        var oldJson = existing is null ? null : Describe(existing);

        if (existing is null)
        {
            // ⚠ Ordinal, якщо не переданий явно, — за наявними аркушами: новий
            // аркуш стає ОСТАННІМ у порядку показу, а не вставляється навмання
            // всередину.
            var ordinal = command.Ordinal
                ?? (version.Sheets.Count == 0 ? 0 : version.Sheets.Max(s => s.Ordinal) + 1);

            existing = new SheetDef(templateVersionId, ecrCode, name, ordinal);
            existing.SetGroup(command.SheetGroup);
            existing.SetMandatory(command.IsMandatory);
            existing.SetVisible(command.IsVisible);

            version.AddSheet(existing);

            // ⛔ Запис ПЕРЕД аудитом, і лише для створення — так само, як
            // `SaveTableRelationHandler`. Аудит несе `EntityId`; у щойно
            // доданої сутності його ще немає до `SaveChanges`.
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            existing.Rename(name);
            existing.SetGroup(command.SheetGroup);
            existing.SetMandatory(command.IsMandatory);
            existing.SetVisible(command.IsVisible);

            if (command.Ordinal is { } ordinal)
            {
                existing.Reorder(ordinal);
            }
        }

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                clock.UtcNow, templateVersionId, nameof(SheetDef), existing.Id,
                change, oldJson is null ? "Create" : "Update",
                oldJson, Describe(existing), ChangeReason: null,
                ChangedByUserId: userId, CorrelationId: null),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Див. коментар класу: без цього виклику `GET …/structure`,
        // прочитаний хоч раз до цієї правки, віддавав би знімок без щойно
        // доданого чи зміненого аркуша, доки версію не опублікують.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        return Map(existing);
    }

    /// <summary>Складає DTO аркуша для відповіді.</summary>
    /// <remarks>
    /// ⚠ <c>Tables</c> — порожній перелік. Цей ендпоінт керує лише аркушем;
    /// таблиці на ньому заводить наступний зріз (<c>TableDef</c>), і показувати
    /// тут застарілий склад таблиць означало б тримати другий шлях їх читання
    /// поруч із <c>GET …/structure</c>.
    /// </remarks>
    internal static SheetDto Map(SheetDef sheet)
        => new(
            sheet.Id, sheet.Code, sheet.NameL10n, sheet.Ordinal,
            sheet.SheetGroup, sheet.IsMandatory, sheet.IsVisible,
            []);

    /// <summary>Стан аркуша для аудиту.</summary>
    /// <param name="sheet">Аркуш.</param>
    /// <returns>JSON із полями, які взагалі можуть змінитися.</returns>
    internal static string Describe(SheetDef sheet)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            sheet.Code,
            NameL10n = sheet.NameL10n.Values,
            sheet.Ordinal,
            sheet.SheetGroup,
            sheet.IsMandatory,
            sheet.IsVisible,
        });
}

/// <summary>Налаштування аркуша, що приходять із форми.</summary>
/// <param name="NameL10n">Назва аркуша мовами каталогу.</param>
/// <param name="Ordinal"><c>null</c> — новий аркуш стає останнім за порядком.</param>
/// <param name="SheetGroup">Група для правил складу документа; <c>null</c> — поза групами.</param>
/// <param name="IsMandatory">Чи обов'язковий аркуш для складу документа.</param>
/// <param name="IsVisible">Видимість аркуша.</param>
public sealed record SaveSheetDefCommand(
    IReadOnlyDictionary<string, string> NameL10n,
    int? Ordinal,
    string? SheetGroup,
    bool IsMandatory,
    bool IsVisible);

/// <summary>
/// Прибирає аркуш із версії-чернетки — м'яко (<c>ФВ-7.6</c>).
/// </summary>
/// <remarks>
/// ⛔ Аркуш не зникає фізично: на нього можуть посилатися формули й правила
/// інших частин структури навіть у чернетці (наприклад, посилання ще не
/// перевірене публікацією). <see cref="SheetDef.SoftDelete"/> лишає запис,
/// щоб такі посилання лишалися видимими, а не провалювалися у порожнечу.
/// </remarks>
public sealed class DeleteSheetDefHandler(
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Видаляє аркуш.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="code">Код аркуша.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або аркуша немає.</exception>
    /// <exception cref="BusinessRuleException">Версія структурно заморожена.</exception>
    public async Task HandleAsync(int templateVersionId, string code, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var sheet = version.Sheets.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.Ordinal))
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Аркуша «{code}» у версії {templateVersionId} немає.");

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
        var change = classifier.ClassifyDeletion(nameof(SheetDef), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Видалення");

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                clock.UtcNow, templateVersionId, nameof(SheetDef), sheet.Id,
                change, "Delete",
                SaveSheetDefHandler.Describe(sheet), NewJson: null, ChangeReason: null,
                ChangedByUserId: userId, CorrelationId: null),
            ct).ConfigureAwait(false);

        sheet.SoftDelete(userId, clock.UtcNow);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }
}
