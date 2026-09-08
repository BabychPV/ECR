// src/Ecr.Application/Reporting/ReportDefHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Reporting;

/// <summary>
/// Авторство ОПИСУ звіту: <c>rpt.ReportDef</c> і <c>rpt.ReportVersion</c>
/// (<c>ФВ-10.4</c>, директива №09 <c>W7</c>).
/// </summary>
/// <remarks>
/// ⛔ Це <b>не</b> конструктор звітів. Веб-переглядач і конструктор ТЗ прямо
/// виносить за обсяг (<c>ФВ-10.6</c>): рендеринг лишається в SSRS
/// (<c>D-52</c>). Тут заводиться рівно те, без чого <c>POST
/// /reports/{code}/build</c> не має за що зачепитися, — рядок опису й версія
/// до нього.
///
/// ⛔ Причина, чому цей файл узагалі знадобився: <c>ReportDef</c> і
/// <c>ReportVersion</c> не створювало НІЩО — ні код, ні seed, ні тести. Тобто
/// побудова зрізу існувала і не могла завершитися успіхом жодного разу:
/// <see cref="BuildReportSnapshotHandler"/> резолвить версію за кодом і
/// відмовляє <c>ECR-RPT-0404</c>, бо резолвити нема чого. Той самий клас
/// дефекту, який сторож <c>Кожна_сутність_яку_система_створює_має_чим_її_заповнити</c>
/// ловить для <c>TableInstance</c> і <c>ResourceGrant</c>.
///
/// ⚠ Колонки й правила лишаються ДАНИМИ (<c>ФВ-10.4</c>): нова державна форма
/// не має потребувати релізу коду. Тому вони приходять СТРУКТУРОЮ, а не
/// довільним рядком JSON, — сериалізує їх обробник, і саме тому клієнт не може
/// покласти в опис звіту те, чого побудова не прочитає.
/// </remarks>
public static class ReportDefinitionSpec
{
    /// <summary>Налаштування серіалізації опису колонок і правил.</summary>
    /// <remarks>
    /// ⛔ <c>JsonSerializerDefaults.Web</c> дослівно, бо саме ними
    /// <c>ReportColumnSpec.Parse</c> (<c>Ecr.Infrastructure</c>) цей JSON
    /// ЧИТАЄ. Розбіжність у регістрі імен полів не зламала б нічого гучно:
    /// розбір просто повернув би колонки з порожніми кодами.
    /// </remarks>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Типи значення комірки зрізу, які вміє <c>rpt.ReportRow</c>.</summary>
    /// <remarks>
    /// Рівно три, бо рядок зрізу має рівно три колонки значення:
    /// <c>ValueString</c>, <c>ValueNumeric</c>, <c>ValueDate</c>. Четвертий
    /// тип не мав би куди лягти.
    /// </remarks>
    public static readonly string[] ColumnKinds = ["text", "number", "date"];

    /// <summary>
    /// Єдине джерело рядків, яке будівник зрізу справді вміє
    /// (<c>ReportSnapshotBuilder.AggregateAsync</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Значення перевіряється, а не приймається будь-яке. Правила відбору —
    /// дані, і поле <c>RulesJson</c> існує саме щоб їх колись стало більше;
    /// але поки будівник знає ОДНЕ джерело, опис із будь-яким іншим означав би
    /// звіт, який мовчки будується не з того, що в ньому написано. Відмова
    /// голосно каже правду; тиша — ні.
    /// </remarks>
    public const string CalculationResults = "CalculationResults";

    /// <summary>Складає <c>ColumnsJson</c> з опису колонок.</summary>
    /// <param name="columns">Колонки зрізу.</param>
    /// <exception cref="BusinessRuleException">Колонок немає, або опис колонки зламаний.</exception>
    public static string ColumnsJson(IReadOnlyList<ReportColumnCommand> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.ReportInvalid,
                "Версія звіту без жодної колонки описує зріз, у якому нема чого показати.");
        }

        foreach (var column in columns)
        {
            // Код колонки — ключ рядка зрізу (`rpt.ReportRow.ColumnCode`), і
            // саме за ним SSRS шукає значення. Обмеження те саме, що й у
            // решти кодів конфігурації.
            _ = EcrCode.Create(column.Code);

            if (!Array.Exists(ColumnKinds, k => string.Equals(k, column.Kind, StringComparison.Ordinal)))
            {
                throw new BusinessRuleException(
                    ErrorCodes.ReportInvalid,
                    $"Тип колонки «{column.Kind}» невідомий: рядок зрізу зберігає лише "
                    + $"{string.Join(", ", ColumnKinds)}.");
            }
        }

        var duplicate = columns
            .GroupBy(c => c.Code, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            // ⛔ Ключ рядка зрізу — `(SnapshotId, RowNo, ColumnCode)`. Дві
            // колонки з одним кодом не «перезаписали б» одна одну, а впали б
            // порушенням первинного ключа посеред нічної побудови.
            throw new BusinessRuleException(
                ErrorCodes.ReportInvalid,
                $"Колонка «{duplicate.Key}» описана двічі: код колонки входить у ключ рядка зрізу.");
        }

        return JsonSerializer.Serialize(columns, Options);
    }

    /// <summary>Складає <c>RulesJson</c> з правил відбору рядків.</summary>
    /// <param name="rules">Правила; <c>null</c> — джерело за замовчуванням.</param>
    /// <exception cref="BusinessRuleException">Джерело рядків невідоме будівнику.</exception>
    public static string RulesJson(ReportRulesCommand? rules)
    {
        var effective = rules ?? new ReportRulesCommand(CalculationResults);

        if (!string.Equals(effective.RowSource, CalculationResults, StringComparison.Ordinal))
        {
            throw new BusinessRuleException(
                ErrorCodes.ReportInvalid,
                $"Джерело рядків «{effective.RowSource}» побудова зрізу не вміє: "
                + $"на сьогодні є одне — «{CalculationResults}» (результати чинного прогону).");
        }

        return JsonSerializer.Serialize(effective, Options);
    }

    /// <summary>Складає назву мовами каталогу.</summary>
    /// <param name="nameL10n">Назва; має бути непорожня хоча б однією мовою.</param>
    /// <exception cref="BusinessRuleException">Назви немає жодною мовою.</exception>
    public static LocalizedText Name(IReadOnlyDictionary<string, string> nameL10n)
    {
        ArgumentNullException.ThrowIfNull(nameL10n);

        if (!nameL10n.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            throw new BusinessRuleException(
                ErrorCodes.ReportInvalid,
                "Дайте звіту назву хоча б однією мовою: у переліку він адресується саме нею.");
        }

        return new LocalizedText(new Dictionary<string, string>(nameL10n, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Перевіряє номер версії проти межі колонки <c>rpt.ReportVersion.Version</c>.</summary>
    /// <param name="version">Номер версії.</param>
    /// <exception cref="BusinessRuleException">Номер порожній або задовгий.</exception>
    public static string Version(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > MaxVersionLength)
        {
            throw new BusinessRuleException(
                ErrorCodes.ReportInvalid,
                $"Номер версії звіту — від 1 до {MaxVersionLength} символів "
                + "(`UQ_ReportVersion` адресує версію саме ним).");
        }

        return version;
    }

    /// <summary>Межа колонки <c>Version</c> у <c>ReportVersionConfiguration</c>.</summary>
    private const int MaxVersionLength = 20;
}

/// <summary>Колонка зрізу в описі версії звіту.</summary>
/// <param name="Code">Код колонки; він же ключ у рядку зрізу.</param>
/// <param name="Kind">Тип значення: <c>text</c>, <c>number</c> або <c>date</c>.</param>
public sealed record ReportColumnCommand(string Code, string Kind);

/// <summary>Правила відбору рядків зрізу.</summary>
/// <param name="RowSource">
/// Звідки беруться рядки. Єдине відоме будівнику значення —
/// <see cref="ReportDefinitionSpec.CalculationResults"/>.
/// </param>
public sealed record ReportRulesCommand(string RowSource);

/// <summary>
/// Перелік описів звітів. Право <c>Report.ViewRegulatory</c>.
/// </summary>
/// <remarks>
/// ⚠ Право те саме, що й у переліку зрізів, а не окреме: обидва відповідають
/// на одне питання — «що взагалі є в регламентній звітності». Той, кому можна
/// бачити побудований зріз, і так бачить у ньому <c>ReportVersionId</c>;
/// приховати від нього назву звіту означало б показати число замість імені.
/// </remarks>
public sealed class ListReportDefsHandler(
    IReportDefinitionStore definitions,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на перегляд регуляторної звітності (`02-contracts.md` §9).</summary>
    public const string Permission = "Report.ViewRegulatory";

    /// <summary>Віддає описи разом із версіями.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<ReportDefinitionDto>> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        return await definitions.ListAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Заводить опис звіту разом із його ПЕРШОЮ версією-чернеткою.
/// Право <c>Report.EditDefinition</c>.
/// </summary>
/// <remarks>
/// ⛔ Опис і перша версія створюються ОДНІЄЮ дією, і це не зручність.
/// <c>ReportDef</c> без жодної версії не можна ні побудувати
/// (<see cref="BuildReportSnapshotHandler"/> шукає версію, а не опис), ні
/// пояснити: у переліку він виглядав би як робочий звіт і мовчки відмовляв би
/// на кожну побудову. Два кроки замість одного означали б, що між ними існує
/// стан, у якому система показує звіт, якого немає.
///
/// ⚠ Версія створюється ЧЕРНЕТКОЮ, а публікується окремою дією
/// (<see cref="PublishReportVersionHandler"/>). Це не церемонія: сховище
/// бере лише <c>Published</c> НАВМИСНО (<c>IReportDefinitionStore</c>) — опис
/// правлять саме тоді, коли ще не впевнені в ньому, і зріз за чернеткою
/// потрапив би в регуляторну вʼюху нарівні зі справжнім.
/// </remarks>
public sealed class CreateReportDefHandler(
    IReportDefinitionStore definitions,
    IRepository<ReportDef, int> defs,
    IRepository<ReportVersion, int> versions,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на авторство опису звіту (`02-contracts.md` §9).</summary>
    public const string Permission = "Report.EditDefinition";

    /// <summary>Створює опис і його першу версію-чернетку.</summary>
    /// <param name="command">Код, назва, ознака регуляторності й перша версія.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Створений опис разом із версією.</returns>
    /// <exception cref="BusinessRuleException">Код зайнятий або опис не складається.</exception>
    public async Task<ReportDefinitionDto> HandleAsync(CreateReportDefCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var code = EcrCode.Create(command.Code);
        var name = ReportDefinitionSpec.Name(command.NameL10n);
        var versionNumber = ReportDefinitionSpec.Version(command.Version);
        var columnsJson = ReportDefinitionSpec.ColumnsJson(command.Columns);
        var rulesJson = ReportDefinitionSpec.RulesJson(command.Rules);

        if (await definitions.ExistsAsync(code.Value, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                ErrorCodes.ReportDefDuplicate,
                $"Звіт з кодом «{code.Value}» уже описаний: побудова адресує звіт саме кодом.");
        }

        var def = new ReportDef(code, name, command.IsRegulatory);
        defs.Add(def);

        // ⛔ Запис ПЕРЕД створенням версії: щойно доданому опису ще бракує
        // `Id`, а `ReportVersion` тримає його зовнішнім ключем (`FK_RV_Def`).
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        var version = new ReportVersion(def.Id, versionNumber, columnsJson, rulesJson, clock.UtcNow);
        versions.Add(version);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return Map(def, [version]);
    }

    /// <summary>Складає відповідь із щойно створених сутностей.</summary>
    internal static ReportDefinitionDto Map(ReportDef def, IReadOnlyList<ReportVersion> versions)
        => new(
            def.Id,
            def.Code,
            def.NameL10n,
            def.IsRegulatory,
            def.IsActive,
            [.. versions.Select(v => new ReportVersionDto(
                v.Id, v.Version, v.Status.ToString(), v.ColumnsJson, v.RulesJson, v.CreatedAt))]);
}

/// <summary>Запит на створення опису звіту разом із першою версією.</summary>
/// <param name="Code">Код звіту; ним адресується побудова зрізу.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="IsRegulatory">
/// Чи звіт іде регулятору. Від цього залежить фільтр статусів у вʼюсі
/// <c>rpt.v_*</c> (<c>D-65</c>), а не «важливість».
/// </param>
/// <param name="Version">Номер першої версії.</param>
/// <param name="Columns">Колонки зрізу.</param>
/// <param name="Rules">Правила відбору рядків; <c>null</c> — джерело за замовчуванням.</param>
public sealed record CreateReportDefCommand(
    string Code,
    IReadOnlyDictionary<string, string> NameL10n,
    bool IsRegulatory,
    string Version,
    IReadOnlyList<ReportColumnCommand> Columns,
    ReportRulesCommand? Rules);

/// <summary>
/// Заводить НОВУ версію-чернетку наявного опису звіту.
/// Право <c>Report.EditDefinition</c>.
/// </summary>
/// <remarks>
/// ⛔ Єдиний спосіб змінити опублікований опис звіту — так само, як у
/// методології (<c>ФВ-9.1</c>): опублікована версія незмінна, бо на її колонки
/// вже посилаються побудовані зрізи, які SSRS читає. «Зміна» — це нова версія,
/// і саме тому окремої дії «правити версію» тут немає взагалі.
/// </remarks>
public sealed class CreateReportVersionHandler(
    IRepository<ReportDef, int> defs,
    IRepository<ReportVersion, int> versions,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на авторство опису звіту (`02-contracts.md` §9).</summary>
    public const string Permission = "Report.EditDefinition";

    /// <summary>Створює версію-чернетку.</summary>
    /// <param name="reportDefId">Опис звіту.</param>
    /// <param name="command">Номер версії, колонки й правила.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Створена версія.</returns>
    /// <exception cref="NotFoundException">Опису звіту немає.</exception>
    /// <exception cref="BusinessRuleException">Опис версії не складається.</exception>
    public async Task<ReportVersionDto> HandleAsync(
        int reportDefId, CreateReportVersionCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        _ = await defs.FindAsync(reportDefId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.ReportNotFound, $"Опису звіту {reportDefId} немає.");

        var version = new ReportVersion(
            reportDefId,
            ReportDefinitionSpec.Version(command.Version),
            ReportDefinitionSpec.ColumnsJson(command.Columns),
            ReportDefinitionSpec.RulesJson(command.Rules),
            clock.UtcNow);

        versions.Add(version);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ReportVersionDto(
            version.Id, version.Version, version.Status.ToString(),
            version.ColumnsJson, version.RulesJson, version.CreatedAt);
    }
}

/// <summary>Запит на створення версії-чернетки опису звіту.</summary>
/// <param name="Version">Номер версії; унікальний у межах опису (<c>UQ_ReportVersion</c>).</param>
/// <param name="Columns">Колонки зрізу.</param>
/// <param name="Rules">Правила відбору рядків; <c>null</c> — джерело за замовчуванням.</param>
public sealed record CreateReportVersionCommand(
    string Version, IReadOnlyList<ReportColumnCommand> Columns, ReportRulesCommand? Rules);

/// <summary>
/// Публікує версію опису звіту. Право <c>Report.EditDefinition</c>.
/// </summary>
/// <remarks>
/// ⚠ Публікація тут НЕ вимагає окремого права, на відміну від методології
/// (<c>Calculation.Publish</c>, <c>D-40</c>, правило чотирьох очей). Різниця
/// не в дисципліні, а в наслідках: публікація методології тихо змінює числа у
/// ВЖЕ ПОДАНИХ формах, а публікація версії звіту не змінює жодного
/// побудованого зрізу — зріз незмінний і назавжди прив'язаний до тієї версії,
/// за якою його побудували (<c>ФВ-9.17</c>). Наступна побудова візьме нову
/// версію, і це буде НОВИЙ зріз із власною контрольною сумою.
/// </remarks>
public sealed class PublishReportVersionHandler(
    IRepository<ReportVersion, int> versions,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на авторство опису звіту (`02-contracts.md` §9).</summary>
    public const string Permission = "Report.EditDefinition";

    /// <summary>Публікує версію.</summary>
    /// <param name="reportDefId">Опис звіту — власник версії.</param>
    /// <param name="reportVersionId">Версія.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Опублікована версія.</returns>
    /// <exception cref="NotFoundException">Версії немає, або вона належить іншому опису.</exception>
    /// <exception cref="DomainException">Версія вже не чернетка.</exception>
    public async Task<ReportVersionDto> HandleAsync(
        int reportDefId, int reportVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await versions.FindAsync(reportVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.ReportNotFound, $"Версії звіту {reportVersionId} немає.");

        // ⛔ Належність перевіряється, а не мається на увазі: `{id}` у шляху
        // інакше був би декорацією, і публікація чужої версії проходила б за
        // адресою, яка каже, що публікує СВОЮ.
        if (version.ReportDefId != reportDefId)
        {
            throw new NotFoundException(
                ErrorCodes.ReportNotFound,
                $"Версія {reportVersionId} належить опису {version.ReportDefId}, а не {reportDefId}.");
        }

        version.Publish();
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ReportVersionDto(
            version.Id, version.Version, version.Status.ToString(),
            version.ColumnsJson, version.RulesJson, version.CreatedAt);
    }
}
