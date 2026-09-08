// src/Ecr.Application/Templates/FormulaDefHandlers.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Templates;

/// <summary>
/// Записує формулу колонки чи рядка таблиці версії-чернетки: наступний
/// вертикальний зріз авторства структури шаблону через API (<c>W5.3</c>), за
/// зразком <see cref="SaveSheetDefHandler"/> (<c>W5.0</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього зрізу <c>FormulaDef</c> міг завести лише офлайновий генератор
/// тестових даних чи тест напряму через конструктор — <c>ColumnDefId</c> і
/// <c>RowDefId</c> не мали навіть публічного сеттера (<c>CascadeRecalculationTests</c>
/// виставляв їх рефлексією). Валідація виразу (<c>POST
/// /expressions/validate</c>) вже існувала, але вона перевіряє текст
/// ІЗОЛЬОВАНО — вона не знає, що результат треба комусь приписати. Це і є
/// відсутня частина: реально ЗБЕРЕГТИ формулу на колонці чи рядку таблиці, щоб
/// вона з'явилася в графі залежностей і рушії перерахунку.
///
/// ⚠ Адреса — <c>(tableDefId, scope, target)</c>, а НЕ код, на відміну від
/// <see cref="SaveSheetDefHandler"/>. <see cref="FormulaDef"/> не несе
/// власного <c>EcrCode</c> взагалі (див. <c>FormulaDef.cs</c>): його
/// ідентичність — це САМЕ колонка чи рядок, який він обчислює.
///
/// ⛔ <c>target</c> означає РІЗНЕ залежно від <c>scope</c>, і це навмисно, а не
/// недогляд:
/// <list type="bullet">
/// <item><c>Column</c> — числовий <c>ColumnDefId</c> (той самий, що й
/// <c>TemplateColumnDto.Id</c> у структурі версії: він глобально унікальний і
/// вже показаний клієнту).</item>
/// <item><c>Row</c> — <c>RowKeyValue</c> (текст). <c>RowDef.Id</c> НЕ можна
/// було взяти за адресу: <c>TemplateRowDto</c> (`GetTemplateStructureHandler`)
/// взагалі не віддає клієнту числового ідентифікатора рядка — лише
/// <c>RowKey</c>, і так само описаний сам <c>RowDef</c> («ідентичність —
/// <c>RowKey</c>, <c>Ordinal</c> відповідає лише за порядок»). Адресація
/// числовим <c>Id</c> зробила б половину цього ендпоінта викликаною лише з
/// Swagger, ніколи — з реального клієнта.</item>
/// </list>
/// Через це <c>RowKey</c> унікальний лише В МЕЖАХ ТАБЛИЦІ (бізнес-ключ, а не
/// сурогатний), тому таблицю в адресі треба назвати явно — на відміну від
/// колонки, чий <c>Id</c> сам по собі однозначний. Один і той самий сегмент
/// <c>tableDefId</c> для обох областей — це узгодженість адреси, а не
/// технічна потреба Column-гілки.
///
/// ⛔ <c>TableDef.AddFormula</c> (написаний іншим зрізом, W5.1) не перевіряє
/// дублікат — на відміну від <c>AddColumn</c>/<c>AddRow</c> на тому самому
/// класі. Тому перевірку «на цій колонці чи рядку вже є формула» робить ЦЕЙ
/// обробник: шукає серед <c>table.Formulas</c> ту, що вказує на ту саму ціль,
/// і якщо є — оновлює її, а не додає другу.
///
/// ⛔ Читає й пише через <see cref="ITemplateVersionStore.GetWithStructureAsync"/>
/// і викликає <see cref="IMetadataCache.InvalidateAsync"/> явно — той самий
/// фікс, що й у <see cref="SaveSheetDefHandler"/>: структурна правка чернетки
/// не піднімає <c>PresentationRevision</c>, тож без інвалідації прогрітий кеш
/// віддавав би структуру без щойно доданої чи зміненої формули.
/// </remarks>
public sealed class SaveFormulaDefHandler(
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

    /// <summary>Створює або змінює формулу колонки чи рядка.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="tableDefId">Таблиця, якій належить ціль.</param>
    /// <param name="scope">
    /// <c>Column</c> чи <c>Row</c> — інші області цей зріз не приймає (<c>Cell</c>
    /// поєднує обидві адреси одразу і сюди не адресується).
    /// </param>
    /// <param name="target">
    /// <c>ColumnDefId</c> числом при <c>Column</c>; <c>RowKey</c> текстом при <c>Row</c>.
    /// </param>
    /// <param name="command">Вираз і діалект.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Збережена формула.</returns>
    /// <exception cref="NotFoundException">Версії, таблиці, колонки чи рядка немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Область не <c>Column</c>/<c>Row</c> (<c>ECR-TMPL-0422</c>).
    /// </exception>
    /// <exception cref="DomainException">
    /// Версія структурно заморожена (<c>ECR-TMPL-0409</c>).
    /// </exception>
    public async Task<FormulaDto> HandleAsync(
        int templateVersionId, int tableDefId, FormulaScope scope, string target,
        SaveFormulaDefCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireColumnOrRow(scope);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        // ⛔ Повний граф версії, ВІДСТЕЖУВАНИЙ — так само, як SaveSheetDefHandler:
        // порожній `IRepository.FindAsync` без `Include` не бачив би жодної
        // таблиці, колонки чи рядка, і пошук цілі нижче завжди провалювався б у 404.
        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⛔ ПЕРШИМ ділом і доменом — так само, як в усіх обробниках структури
        // чернетки: усе інше нижче має сенс лише там, де правка дозволена.
        version.EnsureStructurallyMutable();

        var (table, existing, resolvedId) = FindTarget(version, tableDefId, scope, target);

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Класифікація йде і для СТВОРЕННЯ теж — так само, як в SheetDef і
        // TableRelation: клас пишеться в аудит незалежно від того, чи міг він
        // щось зламати.
        var change = existing is null
            ? classifier.ClassifyAddition(nameof(FormulaDef))
            : classifier.Classify(nameof(FormulaDef), nameof(FormulaDef.Expression), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, Address(tableDefId, scope, target), hasDocuments, "Зміна");

        var oldJson = existing is null ? null : Describe(existing);

        if (existing is null)
        {
            existing = new FormulaDef(table.Id, scope, command.Expression, command.Dialect);

            if (scope == FormulaScope.Column)
            {
                existing.AssignColumn(resolvedId);
            }
            else
            {
                existing.AssignRow(resolvedId);
            }

            table.AddFormula(existing);

            // ⛔ Запис ПЕРЕД аудитом, і лише для створення — так само, як
            // SaveSheetDefHandler: аудит несе `EntityId`, якого в щойно
            // доданої сутності ще немає до `SaveChanges`.
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            existing.SetExpression(command.Expression);
            existing.SetDialect(command.Dialect);
        }

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                clock.UtcNow, templateVersionId, nameof(FormulaDef), existing.Id,
                change, oldJson is null ? "Create" : "Update",
                oldJson, Describe(existing), ChangeReason: null,
                ChangedByUserId: userId, CorrelationId: null),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Той самий фікс, що й у SaveSheetDefHandler: без цього виклику
        // `GET …/structure`, прочитаний хоч раз до цієї правки, віддавав би
        // знімок без щойно доданої чи зміненої формули, доки версію не
        // опублікують.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        return Map(existing);
    }

    /// <summary>Область формули, яку приймає цей зріз.</summary>
    /// <exception cref="BusinessRuleException"><c>Cell</c> чи інше значення поза цим переліком.</exception>
    private static void RequireColumnOrRow(FormulaScope scope)
    {
        if (scope is FormulaScope.Column or FormulaScope.Row)
        {
            return;
        }

        throw new BusinessRuleException(
            ErrorCodes.TemplateInvalid,
            $"Область формули {scope} тут не приймається: адресується лише Column або Row " +
            "(Cell поєднує обидві адреси одразу — для нього немає єдиної адреси).");
    }

    /// <summary>
    /// Шукає таблицю, наявну формулу й СПРАВЖНІЙ (сурогатний) ідентифікатор
    /// цілі за адресою <c>(tableDefId, scope, target)</c>.
    /// </summary>
    /// <returns>
    /// Таблиця; наявна не видалена формула на цій цілі, якщо є; і
    /// <c>ColumnDefId</c>/<c>RowDef.Id</c> — те, що вимагають
    /// <see cref="FormulaDef.AssignColumn"/>/<see cref="FormulaDef.AssignRow"/>
    /// (обидва — сурогатні ключі бази, а не бізнес-адреса запиту).
    /// </returns>
    /// <remarks>
    /// ⚠ Наявна формула шукається серед НЕ видалених: м'яко видалену адреса не
    /// зачіпає, і повторний <c>PUT</c> за тією самою ціллю заводить нову
    /// формулу, а не оживляє стару. <see cref="SaveSheetDefHandler"/> теж не
    /// має операції «оживити», тож ця поведінка узгоджена з тим самим
    /// пропуском в еталоні, а не новим рішенням, узятим лише тут.
    /// </remarks>
    /// <exception cref="NotFoundException">Таблиці, колонки чи рядка з такою адресою немає.</exception>
    internal static (TableDef Table, FormulaDef? Existing, int ResolvedId) FindTarget(
        TemplateVersion version, int tableDefId, FormulaScope scope, string target)
    {
        TableDef? table = null;
        foreach (var sheet in version.Sheets)
        {
            table = sheet.Tables.FirstOrDefault(t => t.Id == tableDefId);
            if (table is not null)
            {
                break;
            }
        }

        if (table is null)
        {
            throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Таблиці {tableDefId} у версії {version.Id} немає.");
        }

        if (scope == FormulaScope.Column)
        {
            if (!int.TryParse(target, out var columnDefId)
                || table.Columns.FirstOrDefault(c => c.Id == columnDefId) is not { } column)
            {
                throw new NotFoundException(
                    ErrorCodes.TemplateNotFound, $"Колонки «{target}» у таблиці {tableDefId} немає.");
            }

            var existingOnColumn = table.Formulas.FirstOrDefault(f =>
                !f.IsDeleted && f.Scope == FormulaScope.Column && f.ColumnDefId == column.Id);

            return (table, existingOnColumn, column.Id);
        }

        var row = table.Rows.FirstOrDefault(r => string.Equals(r.RowKeyValue, target, StringComparison.Ordinal))
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Рядка «{target}» у таблиці {tableDefId} немає.");

        var existingOnRow = table.Formulas.FirstOrDefault(f =>
            !f.IsDeleted && f.Scope == FormulaScope.Row && f.RowDefId == row.Id);

        return (table, existingOnRow, row.Id);
    }

    /// <summary>Текстова адреса формули — для повідомлень про руйнівну зміну.</summary>
    private static string Address(int tableDefId, FormulaScope scope, string target) => $"{tableDefId}/{scope}/{target}";

    /// <summary>Складає DTO формули для відповіді.</summary>
    internal static FormulaDto Map(FormulaDef formula)
        => new(
            formula.Id, formula.TableDefId, formula.Scope,
            formula.ColumnDefId, formula.RowDefId, formula.Dialect, formula.Expression);

    /// <summary>Стан формули для аудиту.</summary>
    internal static string Describe(FormulaDef formula)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            formula.TableDefId,
            Scope = formula.Scope.ToString(),
            formula.ColumnDefId,
            formula.RowDefId,
            Dialect = formula.Dialect.ToString(),
            formula.Expression,
        });
}

/// <summary>Налаштування формули, що приходять із форми.</summary>
/// <param name="Dialect">Діалект, за яким читається вираз.</param>
/// <param name="Expression">Текст виразу.</param>
public sealed record SaveFormulaDefCommand(ExpressionDialect Dialect, string Expression);

/// <summary>Формула для відповіді клієнту.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="TableDefId">Таблиця, якій належить формула.</param>
/// <param name="Scope">Область: <c>Column</c> чи <c>Row</c>.</param>
/// <param name="ColumnDefId">Колонка, якщо <c>Scope == Column</c>.</param>
/// <param name="RowDefId">Рядок, якщо <c>Scope == Row</c> (сурогатний ключ; адреса запиту — <c>RowKey</c>).</param>
/// <param name="Dialect">Діалект виразу.</param>
/// <param name="Expression">Текст виразу.</param>
public sealed record FormulaDto(
    int Id, int TableDefId, FormulaScope Scope,
    int? ColumnDefId, int? RowDefId, ExpressionDialect Dialect, string Expression);

/// <summary>
/// Прибирає формулу з версії-чернетки — м'яко (<c>ФВ-7.6</c>).
/// </summary>
/// <remarks>
/// ⛔ Формула не зникає фізично: на неї може посилатися граф залежностей
/// інших формул навіть у чернетці. <see cref="FormulaDef.SoftDelete"/> лишає
/// запис, щоб такі посилання лишалися видимими, а не провалювалися в
/// порожнечу — той самий принцип, що й <see cref="SheetDef.SoftDelete"/>.
/// </remarks>
public sealed class DeleteFormulaDefHandler(
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

    /// <summary>Видаляє формулу колонки чи рядка.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="tableDefId">Таблиця, якій належить ціль.</param>
    /// <param name="scope"><c>Column</c> чи <c>Row</c>.</param>
    /// <param name="target"><c>ColumnDefId</c> числом при <c>Column</c>; <c>RowKey</c> текстом при <c>Row</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії, цілі чи формули на ній немає.</exception>
    /// <exception cref="BusinessRuleException">Версія структурно заморожена.</exception>
    public async Task HandleAsync(
        int templateVersionId, int tableDefId, FormulaScope scope, string target, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var (_, formula, _) = SaveFormulaDefHandler.FindTarget(version, tableDefId, scope, target);

        if (formula is null)
        {
            var kind = scope == FormulaScope.Column ? "колонці" : "рядку";
            throw new NotFoundException(
                ErrorCodes.TemplateNotFound,
                $"На {kind} «{target}» у таблиці {tableDefId} немає формули.");
        }

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
        var change = classifier.ClassifyDeletion(nameof(FormulaDef), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(
            change, $"{tableDefId}/{scope}/{target}", hasDocuments, "Видалення");

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                clock.UtcNow, templateVersionId, nameof(FormulaDef), formula.Id,
                change, "Delete",
                SaveFormulaDefHandler.Describe(formula), NewJson: null, ChangeReason: null,
                ChangedByUserId: userId, CorrelationId: null),
            ct).ConfigureAwait(false);

        formula.SoftDelete();

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }
}
