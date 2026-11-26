// src/Ecr.Application/Ports/ISourceImpactAssessor.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>Затверджений чи ні період залежного документа, якого зачепить дія над документом-джерелом.</summary>
/// <param name="BindingId">Зв'язка, за якою документ залежний.</param>
/// <param name="BindingCode">Код зв'язки.</param>
/// <param name="TemplateCode">Код шаблону залежного документа.</param>
/// <param name="DocumentId">Залежний документ.</param>
/// <param name="ProjectCode">Код проєкту залежного документа.</param>
/// <param name="DocumentKey">Бізнес-ключ залежного документа.</param>
/// <param name="PeriodKey">Період залежного документа, що читає зачеплені місяці джерела.</param>
/// <param name="Sheets">Аркуші складу документа за період (без рядка стану — не затверджені; приховані теж: їх затверджують так само).</param>
/// <param name="Approved">Усі аркуші періоду затверджено (такий період перерахунок не чіпає цілком).</param>
public sealed record SourceImpactConsumer(
    int BindingId,
    string BindingCode,
    string TemplateCode,
    long DocumentId,
    string ProjectCode,
    string DocumentKey,
    int PeriodKey,
    bool Approved,
    IReadOnlyList<SourceImpactSheet> Sheets)
{
    /// <summary>
    /// У періоді є хоч один затверджений аркуш (D-554): його перерахунок не чіпає, тож він застаріє мовчки. Саме ця ознака,
    /// а не <see cref="Approved"/> («усі»), вмикає політику зв'язки й лічиться в <see cref="SourceImpact.ApprovedCount"/>.
    /// </summary>
    public bool HasApprovedSheets => Approved || Sheets.Any(s => s.Approved);

    /// <summary>
    /// Період порахує каскад (той самий план, що виконає задача: <c>SourceCascadeRouting</c> + план каскаду споживача). Типово —
    /// не затверджений цілком.
    /// </summary>
    public bool WillRecalculate { get; init; } = !Approved;

    /// <summary>Чому каскад період не порахує: <c>approved</c> | <c>dependentsOff</c> | <c>notWritable</c>; <c>null</c> — порахує.</summary>
    public string? SkipReason { get; init; } = Approved ? "approved" : null;

    /// <summary>
    /// Чому період у переліку (код для діалогу): <c>sourceMonth</c> — період володіє зміненим місяцем джерела; <c>sourceYear</c>
    /// — зміну джерела не звужено до місяців (<see cref="SourceImpact.WholeYearReason"/>), будиться увесь рік; <c>readsYear</c> —
    /// шаблон читає джерело за рік чи за будь-який період (обсяг ребра графа), а не за свій місяць; <c>crossMonth</c> — формули
    /// періоду читають обчислені числа ранішого місяця плану (<see cref="ReadsPeriodKey"/>); <c>annualAnchor</c> — закритий якір
    /// року (лише річні значення); <c>readerPass</c> — методологія періоду читає числа місяця плану (<c>D-568</c>).
    /// </summary>
    public string Reason { get; init; } = SourceImpactReasons.SourceMonth;

    /// <summary>Для <c>crossMonth</c> — місяць плану, числа якого читає період; інакше <c>null</c>.</summary>
    public int? ReadsPeriodKey { get; init; }
}

/// <summary>Коди причин оцінки впливу (діалог показує їх людині, <c>sourceImpact.reason.*</c>, <c>sourceImpact.wholeYear.*</c>).</summary>
public static class SourceImpactReasons
{
    /// <summary>Період володіє зміненим місяцем джерела.</summary>
    public const string SourceMonth = "sourceMonth";

    /// <summary>Зміну джерела не звужено до місяців — увесь рік.</summary>
    public const string SourceYear = "sourceYear";

    /// <summary>Шаблон-споживач читає джерело за рік (чи будь-який період).</summary>
    public const string ReadsYear = "readsYear";

    /// <summary>Формули читають обчислені числа ранішого місяця плану.</summary>
    public const string CrossMonth = "crossMonth";

    /// <summary>Закритий якір річної розкладки (лише річні значення).</summary>
    public const string AnnualAnchor = "annualAnchor";

    /// <summary>Прохід читача результатів (методологія читає інший місяць).</summary>
    public const string ReaderPass = "readerPass";

    /// <summary>Пропуск: затверджено всі аркуші.</summary>
    public const string SkipApproved = "approved";

    /// <summary>Пропуск: у період писати не можна (архівний проєкт, період поза скоупом задачі).</summary>
    public const string SkipNotWritable = "notWritable";

    /// <summary>Пропуск: зв'язка не перераховує залежних (біт <c>RecalculateDependents</c> вимкнено).</summary>
    public const string SkipDependentsOff = "dependentsOff";

    /// <summary>Увесь рік: затвердження/повернення річного чи квартального періоду джерела (місяця немає).</summary>
    public const string WholeYearPeriodApproval = "periodApproval";

    /// <summary>Увесь рік: місяці правки невідомі (не-місячна колонка, рядок, місяці не передано).</summary>
    public const string WholeYearEditedMonthsUnknown = "editedMonthsUnknown";

    /// <summary>Увесь рік: черга задач не несе місяців правки.</summary>
    public const string WholeYearQueueWithoutMonths = "queueWithoutMonths";

    /// <summary>Увесь рік: формула джерела пише річну (не-місячну) колонку з місячних — зміна місяця змінює річне значення.</summary>
    public const string WholeYearAnnualColumn = "annualColumn";

    /// <summary>Увесь рік: методології чи формули джерела переносять зміну в інші місяці (доказ PERF-06b не пройшов).</summary>
    public const string WholeYearSourceSpreadsMonths = "sourceSpreadsMonths";
}

/// <summary>Аркуш-оригінал залежного документа в оцінці впливу: затверджений він не перераховується (D-554).</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="Approved">Аркуш затверджено; аркуш без рядка стану — <c>Draft</c>, тобто не затверджений.</param>
public sealed record SourceImpactSheet(int SheetDefId, string SheetCode, bool Approved);

/// <summary>
/// B80 N5 (D-554): вплив затвердження чи повернення із затвердженого аркуша документа-джерела на залежні документи.
/// </summary>
/// <param name="Change">Дія над джерелом.</param>
/// <param name="DocumentId">Документ-джерело.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період джерела.</param>
/// <param name="Policy">Найсуворіша політика серед зв'язок, що дали затверджених залежних (інакше найсуворіша серед усіх).</param>
/// <param name="SourceMonths">Зачеплені місяці джерела <c>yyyyMM</c>.</param>
/// <param name="Consumers">Залежні документи × періоди, що читають ці місяці (за зростанням зв'язки, документа, періоду).</param>
/// <param name="Hash">Відбиток впливу: підтвердження діє лише на нього (не на застарілий).</param>
public sealed record SourceImpact(
    SourceApprovalChange Change,
    long DocumentId,
    int SheetDefId,
    int PeriodKey,
    DocumentSourcePolicy Policy,
    IReadOnlyList<int> SourceMonths,
    IReadOnlyList<SourceImpactConsumer> Consumers,
    string Hash)
{
    /// <summary>
    /// ✎ Місяці (1–12), які зміна джерела донесе до каскаду; <c>null</c> — увесь рік (<see cref="WholeYearReason"/>).
    /// </summary>
    public IReadOnlyList<int>? CascadeMonths { get; init; }

    /// <summary>Чому зміну не звужено до місяців (<see cref="SourceImpactReasons"/>, <c>WholeYear*</c>); <c>null</c> — звужено.</summary>
    public string? WholeYearReason { get; init; }

    /// <summary>Скільки залежних періодів мають затверджені аркуші (їх перерахунок не чіпає; період, затверджений частково, теж рахується).</summary>
    public int ApprovedCount => Consumers.Count(c => c.HasApprovedSheets);

    /// <summary>
    /// Дія потребує рішення людини (підтвердження чи відмови): політика не <c>None</c> і є хоч один затверджений залежний
    /// період — його перерахунок не чіпає, тож він застаріє мовчки. Незатверджені залежні просто перерахуються.
    /// </summary>
    public bool Required => Policy != DocumentSourcePolicy.None && ApprovedCount > 0;
}

/// <summary>Підтвердження впливу, що надсилає клієнт разом із дією.</summary>
/// <param name="Hash">Відбиток впливу, який бачила людина (<see cref="SourceImpact.Hash"/>).</param>
/// <param name="Reason">Причина; обов'язкова за політики <c>ConfirmWithReason</c> і завжди — за <paramref name="RecalculateApproved"/>.</param>
/// <param name="RecalculateApproved">
/// B80 N5 (D-554, рішення людини 19.11): «перерахувати також затверджені» — лише з правом
/// <c>Calculation.RecalculateApprovedDependents</c> (сервер перевіряє), причина обов'язкова, усе логується.
/// </param>
public sealed record SourceImpactConfirmation(string? Hash, string? Reason, bool RecalculateApproved = false);

/// <summary>
/// B80 N5: оцінка впливу дії над документом-джерелом на залежні документи — за зв'язками в стані <c>ApprovedOnly</c>,
/// споживачі — з графа залежностей (<see cref="IDependencyGraphProvider"/>).
/// </summary>
public interface ISourceImpactAssessor
{
    /// <summary>Вплив дії; без зв'язки (чи за <c>Any</c>) — порожній, політика <c>None</c>.</summary>
    /// <param name="documentId">Документ-джерело.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період аркуша.</param>
    /// <param name="change">Затвердження чи повернення із затвердженого.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<SourceImpact> AssessAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, CancellationToken ct);

    /// <summary>
    /// ✎ Вплив правки з відомими місяцями: правка МІСЯЧНИХ колонок річного/квартального джерела (<paramref name="editedMonths"/>,
    /// 1–12) — ті самі місяці, які PERF-06b (<c>D-572</c>) донесе до каскаду, якщо доказ пройде; інакше — увесь рік.
    /// </summary>
    /// <param name="documentId">Документ-джерело.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період аркуша.</param>
    /// <param name="change">Дія.</param>
    /// <param name="editedMonths">Місяці правки; <c>null</c> чи порожньо — невідомо.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<SourceImpact> AssessAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, IReadOnlyCollection<int>? editedMonths,
        CancellationToken ct);
}
