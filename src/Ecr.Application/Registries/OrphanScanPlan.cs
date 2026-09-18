// src/Ecr.Application/Registries/OrphanScanPlan.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>
/// Рішення нічної перевірки й точкового перерахунку: яким рядкам поставити
/// <c>IsOrphaned</c>, а яким — зняти (ФВ-8.13a, D-98).
/// </summary>
/// <remarks>
/// ⚠ Виділено окремо від сховища навмисно. Реалізація в базі — один
/// set-based <c>UPDATE</c> на мільйони рядків, і перевірити його правило
/// інакше, ніж піднявши SQL Server, неможливо. Тут те саме правило живе у
/// вигляді, який читається і перевіряється; SQL лишається його перекладом.
/// <para>
/// Механізм **симетричний**: те, що ставить ознаку, її ж і знімає. Асиметрія
/// тут — не половина функції, а пастка: користувач виправляє довідник, а
/// <c>Submit</c> лишається заблокованим із помилкою, причину якої вже усунуто.
/// </para>
/// </remarks>
public static class OrphanScanPlan
{
    /// <summary>Обчислює, що змінити.</summary>
    /// <param name="candidates">Рядки з посиланнями на довідник.</param>
    /// <returns>Розділені переліки: поставити і зняти.</returns>
    public static OrphanScanDecision Plan(IEnumerable<OrphanCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var toFlag = new List<OrphanRowRef>();
        var toClear = new List<OrphanRowRef>();

        foreach (var candidate in candidates)
        {
            // ⛔ Закриті періоди не чіпаються. Їхні дані вже подані й
            // погоджені; ознака нічого не розблокує і нічого не заборонить,
            // зате перепише рядок, що входить у контрольну суму зрізу.
            if (!IsScannable(candidate.PeriodState))
            {
                continue;
            }

            switch (candidate)
            {
                case { ReferenceIsValid: false, IsOrphaned: false }:
                    toFlag.Add(candidate.Row);
                    break;

                case { ReferenceIsValid: true, IsOrphaned: true }:
                    toClear.Add(candidate.Row);
                    break;

                default:
                    // Стан збігається з дійсністю — писати нічого. Це не
                    // оптимізація: зайвий UPDATE підняв би ModifiedAt і зламав
                    // оптимістичне блокування чужої відкритої форми.
                    break;
            }
        }

        return new OrphanScanDecision(toFlag, toClear);
    }

    /// <summary>Чи перевіряється період узагалі.</summary>
    /// <param name="state">Стан періоду.</param>
    public static bool IsScannable(PeriodState state)
        => state is PeriodState.Open or PeriodState.Grace;
}

/// <summary>
/// ПОВНА адреса рядка <c>doc.TableRow</c>: ключ партиції і <c>Id</c>.
/// </summary>
/// <param name="PeriodKeyValue">Ключ періоду — він же ключ партиції (R-A6).</param>
/// <param name="RowId">Рядок у межах періоду.</param>
/// <remarks>
/// ⛔ Окремий тип, а не голий <c>long</c>, і це не косметика. Первинний ключ
/// <c>doc.TableRow</c> — складений <c>(PeriodKey, Id)</c>, таблиця лежить на
/// <c>ps_ByPeriodKey</c>, і жодного індексу з <c>Id</c> попереду немає й бути
/// не може: <c>07-partition-tables.sql</c> вирівнює КОЖЕН індекс цих таблиць
/// по схемі партиціонування й падає (<c>THROW 50031</c>), якщо хоч один
/// лишився поза нею. Поки план ніс самі <c>Id</c>, ПЕРІОД БУВ НЕДОСТУПНИЙ НА
/// МІСЦІ ЗАПИСУ — і <c>OrphanScanner</c> писав <c>WHERE Id IN (…)</c>, тобто
/// щоночі проходив ВСІ партиції таблиці, розрахованої на ~108 млн рядків на
/// рік. Тип нижче прибирає саме цю прогалину: неможливо покласти рядок у
/// рішення, не назвавши його періоду.
/// <para>
/// Той самий дефект і те саме лікування, що в <c>RowStore.TouchRowsAsync</c>
/// (гілка <c>fix/touchrows-index</c>): нести ключ партиції у предикаті, а не
/// заводити під <c>Id</c> окремий індекс.
/// </para>
/// </remarks>
public sealed record OrphanRowRef(int PeriodKeyValue, long RowId);

/// <summary>Рядок-кандидат для перевірки осиротілості.</summary>
/// <param name="Row">Адреса рядка: період і <c>Id</c>.</param>
/// <param name="PeriodState">Стан періоду рядка.</param>
/// <param name="IsOrphaned">Ознака, що зараз збережена на рядку.</param>
/// <param name="ReferenceIsValid">
/// Чи чинний запис довідника, на який рядок посилається, на дату його періоду.
/// Обчислюється <c>RegistryResolver.IsSelectable</c> — тим самим правилом, що
/// формує випадний список.
/// </param>
public sealed record OrphanCandidate(
    OrphanRowRef Row, PeriodState PeriodState, bool IsOrphaned, bool ReferenceIsValid)
{
    /// <summary>Рядок <c>doc.TableRow</c> у межах свого періоду.</summary>
    public long RowId => Row.RowId;

    /// <summary>Ключ періоду (він же — ключ партиції) цього рядка.</summary>
    public int PeriodKeyValue => Row.PeriodKeyValue;
}

/// <summary>
/// Скільки одна ніч має право оглянути й скільки часу на це витратити.
/// </summary>
/// <param name="CursorCode">
/// Код курсора в <c>itg.ScanCursor</c>: де сканування лишає позицію між
/// прогонами.
/// </param>
/// <param name="RowBudget">Стеля рядків на один прогін.</param>
/// <param name="BatchCells">Стеля посилань в одному читанні з бази.</param>
/// <param name="TimeBudget">Стеля часу на один прогін.</param>
/// <remarks>
/// ⛔ ОБИДВА бюджети, і жоден із двох не зайвий.
/// <list type="bullet">
/// <item><b>Рядковий</b> — це те, що робить просування ДОВІДНИМ. Скільки
/// рядків ніч огляне, відомо наперед, тож «наступна ніч бере наступні
/// <c>N</c>» перевіряється тестом, а не спостереженням за продом. Сам по собі
/// він, однак, нічого не обіцяє про тривалість: ті самі <c>N</c> рядків на
/// завантаженому сервері можуть іти вдесятеро довше.</item>
/// <item><b>Часовий</b> — це те, що тримає прогін у вікні обслуговування.
/// Сам по собі він зробив би покриття невідтворюваним: ніч оглядала б щоразу
/// іншу кількість рядків, і жодне твердження про просування не можна було б
/// ні довести, ні спростувати.</item>
/// </list>
/// Разом вони дають рівно те, що потрібно: передбачувану порцію, яка не
/// перетворюється на багатогодинний прогін, коли база повільніша за очікувану.
/// <para>
/// ⚠ <paramref name="BatchCells"/> — інша одиниця, і навмисно. Ціна запиту
/// визначається КОМІРКАМИ (рядок може мати їх десятки), а покриття
/// вимірюється РЯДКАМИ. Межа читання стоїть там, де ціна; бюджет ночі — там,
/// де сенс.
/// </para>
/// </remarks>
public sealed record OrphanScanBudget(
    string CursorCode, int RowBudget, int BatchCells, TimeSpan TimeBudget)
{
    /// <summary>Бюджет нічного проходу.</summary>
    /// <remarks>
    /// ⚠ Числа обрані, а не успадковані. <c>500 000</c> рядків за ніч проти
    /// набору «рядки ВІДКРИТИХ періодів, що посилаються на довідник» (а не
    /// проти всіх ~108 млн рядків на рік) дає повний обхід за одиниці ночей
    /// навіть тоді, коли звітують усі проєкти одразу. <c>20 000</c> посилань
    /// на читання — та сама стеля, що стояла тут і до появи курсора: вона
    /// ніколи не була проблемою сама по собі, проблемою було те, що читання
    /// було ОДНЕ й БЕЗ порядку. Двадцять хвилин — частка нічного вікна
    /// обслуговування, якої вистачає з запасом і після якої прогін краще
    /// продовжити завтра, ніж тримати базу.
    /// </remarks>
    public static OrphanScanBudget Nightly { get; } =
        new("orphan-scan", RowBudget: 500_000, BatchCells: 20_000, TimeBudget: TimeSpan.FromMinutes(20));
}

/// <summary>Чим закінчився один нічний прохід.</summary>
/// <param name="Changed">Скільки рядків змінили ознаку <c>IsOrphaned</c>.</param>
/// <param name="ExaminedRows">Скільки рядків прохід справді оглянув.</param>
/// <param name="CycleCompleted">Чи дійшов прохід до кінця набору цієї ночі.</param>
/// <param name="CyclesCompleted">Скільки повних обходів набору зроблено всього.</param>
/// <param name="LastCycleCompletedAt">Коли завершився останній повний обхід.</param>
/// <remarks>
/// ⛔ Раніше прохід повертав саме́ лише число змінених рядків — і цього НЕ
/// ДОСИТЬ, щоб відрізнити три різні стани, які виглядають однаково:
/// «оглянув усе, міняти не було чого», «оглянув шматок і вичерпав бюджет»,
/// «не оглянув нічого, бо не запустився». Усі три дають нуль.
///
/// ⚠ Саме через це запис у журналі «змінено 0» нічого не вартий сам по собі:
/// він однаково описує здорову систему й сканер, що застряг. Різницю дає
/// <paramref name="ExaminedRows"/> (чи була робота взагалі) і
/// <paramref name="CyclesCompleted"/> (чи змикається обхід — тобто чи кожен
/// рядок колись перевіряється, чи сканер відстає від зростання таблиці
/// назавжди).
///
/// ⚠ Це НЕ вимір продуктивності. Тут немає тривалості й не може бути:
/// прохід обмежений двома бюджетами одразу, тож його час — величина, яку
/// задали, а не яку виміряли.
/// </remarks>
/// <remarks>
/// ⛔ Саме <c>readonly record struct</c>, а не клас, і причина конкретна:
/// підсумок повертається з порту, який у тестах підміняють. Для посилального
/// типу ненастроєна підміна віддала б <c>null</c>, і перший же
/// <c>summary.Changed</c> у задачі впав би <c>NullReferenceException</c> —
/// помилкою про порожнє посилання замість чесного «нічого не оглянуто».
/// Так і сталося при першій спробі: чотири тести задачі звірки впали не на
/// своїй причині. У значенні за замовчуванням усе нульове, і це рівно те,
/// що означає «прохід нічого не зробив».
/// </remarks>
public readonly record struct OrphanScanSummary(
    int Changed,
    int ExaminedRows,
    bool CycleCompleted,
    int CyclesCompleted,
    DateTime? LastCycleCompletedAt)
{
    /// <summary>Порожній підсумок: дивитися не було де.</summary>
    /// <remarks>
    /// ⚠ Окрема назва, а не `default` у місці виклику: «немає жодного
    /// відкритого періоду» — це передбачений стан (жоден проєкт ще не
    /// звітує), а не збій, і читач мусить бачити це з імені.
    /// </remarks>
    public static OrphanScanSummary Nothing => default;
}

/// <summary>Що змінити за підсумком перевірки.</summary>
/// <param name="ToFlag">Рядкам поставити <c>IsOrphaned</c>.</param>
/// <param name="ToClear">Рядкам зняти ознаку і занулити <c>OrphanedAt</c>.</param>
/// <remarks>
/// ⚠ Обидва переліки несуть <see cref="OrphanRowRef"/>, а не <c>long</c>, і
/// цілком можуть змішувати періоди: один прохід сканера бере кандидатів з
/// УСІХ відкритих періодів одразу. Тому сховище зобов'язане групувати їх за
/// <c>PeriodKeyValue</c> і писати по одному <c>UPDATE</c> на період — див.
/// <see cref="OrphanRowRef"/> про те, чому один спільний <c>UPDATE</c> по
/// самих <c>Id</c> тут коштує сканування всієї таблиці.
/// </remarks>
public sealed record OrphanScanDecision(
    IReadOnlyList<OrphanRowRef> ToFlag, IReadOnlyList<OrphanRowRef> ToClear)
{
    /// <summary>Скільки рядків буде змінено.</summary>
    public int Total => ToFlag.Count + ToClear.Count;
}
