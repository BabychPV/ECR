using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// <c>WR-05</c>: кожен запит до партиціонованої таблиці несе <c>PeriodKey</c>.
/// </summary>
/// <remarks>
/// ⛔ <b>Перевіряється ЗГЕНЕРОВАНИЙ SQL, а не текст джерела</b>, і це вимога
/// самої директиви №14 (частина 3, §3.3, рядок <c>WR-05</c>) — «з урахуванням
/// уроку <i>сторожі по тексту крихкі</i>». Урок тут не абстрактний: у цьому
/// репозиторії сторож по тексту вже двічі валив гейт — один червонів на
/// власному коментарі, другий на переносі рядка. І гірше за хибно-червоний
/// тут був би хибно-зелений: достатньо, щоб слово <c>PeriodKeyValue</c>
/// траплялося В ІНШОМУ предикаті того ж методу — і regex по джерелу
/// заспокоюється, хоч друга таблиця запиту лишилася без ключа партиції
/// (рівно цей випадок — <see cref="RowStore.OrphanedRowIdsQuery"/>: зовнішній
/// <c>doc.TableRow</c> ключ мав, внутрішній <c>doc.TableInstance</c> — ні).
///
/// <para>Як це влаштовано. Бойові запити винесені в <c>public static</c>
/// методи-фабрики, що повертають <c>IQueryable</c>; бойовий шлях ходить саме
/// через них, сторож бере з них <c>ToQueryString()</c>. Тобто перевіряється не
/// копія запиту в тесті (така розійшлася б із бойовою мовчки), а той самий
/// вираз, який піде в СУБД.</para>
///
/// <para>Чому це важливо саме тут. <c>doc.TableRow</c>, <c>doc.CellValue</c> і
/// <c>doc.TableInstance</c> лежать на <c>ps_ByPeriodKey</c>
/// (<c>07-partition-tables.sql:50-54</c>), їхні кластерні ключі починаються з
/// <c>PeriodKey</c>, і <c>07-partition-tables.sql</c> падає
/// (<c>THROW 50031</c>), якщо хоч один індекс цих таблиць не вирівняний по
/// схемі партиціонування. Тобто «додати індекс під інший стовпець» тут не
/// варіант у принципі: запит без <c>PeriodKey</c> читає ВСІ партиції завжди.
/// Заміряно на <c>RowStore.TouchRowsAsync</c>: 34 308 логічних читань проти
/// 60.</para>
///
/// ⚠ Сторож накриває лише запити, винесені у фабрики. Запити, що лишилися
/// всередині методів інших класів (<c>OrphanScanner</c>,
/// <c>NormalizedCellStore</c>, <c>Jobs/*</c>, <c>IntegrationCellPatcher</c>,
/// <c>ArchiveAwareCellReader</c>), він НЕ бачить — і це названо тут, а не
/// сховано: сторож, про обсяг якого брешуть, дає підставу вважати роботу
/// зробленою. Кожен наступний рядок <c>WR-*</c>/<c>RD-*</c> переносить свої
/// запити у фабрики, і обсяг росте разом із <see cref="MinimumFactories"/>.
/// </remarks>
public sealed partial class PartitionKeyQueryTests
{
    /// <summary>Таблиці на <c>ps_ByPeriodKey</c>.</summary>
    private static readonly string[] PartitionedTables = ["TableRow", "CellValue", "TableInstance"];

    /// <summary>
    /// Скільки фабрик запитів має знайти сторож — храповик обсягу.
    /// </summary>
    /// <remarks>
    /// ⛔ Без підлоги сторож хибно-зеленіє від будь-якої помилки в самому
    /// пошуку: нуль фабрик дає нуль порушників. Це та сама вада, через яку
    /// перша редакція заміру <c>WR-01</c> вийшла хибно-зеленою («0 ≤ 2»
    /// проходить і на невиправленому коді).
    ///
    /// ⚠ Число має РОСТИ, коли запити переносять у фабрики, і не має права
    /// падати: зменшити його означає вивести запит з-під сторожа.
    /// </remarks>
    private const int MinimumFactories = 7;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_запит_до_партиціонованої_таблиці_несе_PeriodKey()
    {
        var offenders = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var seen = 0;

        using var db = Context();

        foreach (var factory in Factories())
        {
            seen++;
            var sql = QueryStringOf(db, factory);

            foreach (var (table, alias) in PartitionedSources(sql))
            {
                covered.Add(table);

                if (!HasPeriodKeyPredicate(sql, alias))
                {
                    offenders.Add(
                        $"{factory.DeclaringType?.Name}.{factory.Name} → doc.{table} AS [{alias}]:"
                        + Environment.NewLine + Indent(sql));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Запити до партиціонованих таблиць без предиката на PeriodKey — "
            + "СУБД прочитає всі партиції:" + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, offenders));

        // ⛔ Обидві підлоги — після перевірки порушників і обидві обов'язкові.
        // Перша ловить зламаний пошук фабрик, друга — випадок, коли фабрики
        // знайшлися, але жодна з них до партиціонованої таблиці не звертається
        // (наприклад, після зміни схеми імен таблиць).
        Assert.True(
            seen >= MinimumFactories,
            $"Фабрик запитів знайдено {seen.ToString(CultureInfo.InvariantCulture)}, "
            + $"очікувалося щонайменше {MinimumFactories.ToString(CultureInfo.InvariantCulture)}. "
            + "Або пошук зламався, або запит вивели з-під сторожа.");

        Assert.Equal(
            PartitionedTables.OrderBy(t => t, StringComparer.Ordinal),
            covered.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// Контрольний постріл: сторож справді червоніє на запиті без ключа.
    /// </summary>
    /// <remarks>
    /// ⛔ Зелений тест не є доказом. Перевірка вище лишалася б зеленою і тоді,
    /// коли <see cref="HasPeriodKeyPredicate"/> помиляється в бік «усе добре» —
    /// наприклад, якби вона зараховувала <c>PeriodKey</c> зі СПИСКУ ВИБІРКИ
    /// (<c>SELECT [t].[PeriodKey], …</c>), який є в кожному запиті до цих
    /// таблиць незалежно від фільтра. Тому тут будується запит, у якого
    /// <c>PeriodKey</c> є в проєкції і НЕМАЄ у <c>WHERE</c>, і сторож мусить
    /// назвати його порушником.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сторож_червоніє_на_запиті_без_предиката_періоду()
    {
        using var db = Context();

        var withoutPeriod = db.TableRows
            .AsNoTracking()
            .Where(r => r.TableInstanceId == 1 && !r.IsDeleted)
            .ToQueryString();

        var sources = PartitionedSources(withoutPeriod).ToList();

        var source = Assert.Single(sources);
        Assert.Equal("TableRow", source.Table);

        // PeriodKey у тексті Є — його тягне проєкція сутності. Саме тому
        // перевірка дивиться на ПРЕДИКАТ, а не на присутність слова.
        Assert.Contains("[PeriodKey]", withoutPeriod, StringComparison.Ordinal);
        Assert.False(HasPeriodKeyPredicate(withoutPeriod, source.Alias));

        // А той самий запит із ключем — проходить. Без цієї половини перевірка
        // була б зеленою і на реалізації, яка відмовляє геть усьому.
        var withPeriod = RowStore
            .RowsQuery(db, tableInstanceId: 1, periodKey: new PeriodKey(202601))
            .ToQueryString();

        Assert.True(HasPeriodKeyPredicate(withPeriod, PartitionedSources(withPeriod).Single().Alias));
    }

    /// <summary>
    /// Контекст без з'єднання: <c>ToQueryString()</c> бази не торкається.
    /// </summary>
    /// <remarks>
    /// ⚠ Рядок підключення навмисно веде в нікуди. Сторож, якому потрібен
    /// живий SQL Server, не був би архітектурним: він падав би на машині без
    /// бази й на гейті <c>build</c>, а причина читалася б як дефект продукту.
    /// </remarks>
    private static EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer("Server=(guard-does-not-connect);Database=Ecr;Trusted_Connection=True")
            .Options);

    /// <summary>Фабрики бойових запитів у <c>Ecr.Infrastructure</c>.</summary>
    /// <returns>Методи, впорядковані стало, щоб повідомлення не стрибало.</returns>
    /// <remarks>
    /// ⚠ Ознака фабрики — форма, а не ім'я й не атрибут: <c>public static</c>,
    /// повертає <c>IQueryable&lt;…&gt;</c>, перший параметр —
    /// <see cref="EcrDbContext"/>. Домовленість про суфікс <c>Query</c> сторож
    /// НЕ використовує: перейменування не має права виводити запит з-під
    /// перевірки.
    /// </remarks>
    private static IEnumerable<MethodInfo> Factories()
        => typeof(EcrDbContext).Assembly
            .GetTypes()
            .Where(t => t.IsPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType.IsGenericType
                        && m.ReturnType.GetGenericTypeDefinition() == typeof(IQueryable<>))
            .Where(m => m.GetParameters().Length > 0
                        && m.GetParameters()[0].ParameterType == typeof(EcrDbContext))
            .OrderBy(m => m.DeclaringType!.FullName, StringComparer.Ordinal)
            .ThenBy(m => m.Name, StringComparer.Ordinal);

    /// <summary>SQL, який фабрика віддасть на зразкових аргументах.</summary>
    private static string QueryStringOf(EcrDbContext db, MethodInfo factory)
    {
        var arguments = factory.GetParameters()
            .Select(p => p.ParameterType == typeof(EcrDbContext) ? db : SampleOf(p))
            .ToArray();

        var query = (IQueryable?)factory.Invoke(null, arguments)
            ?? throw new InvalidOperationException($"{factory.Name} повернув null.");

        return query.ToQueryString();
    }

    /// <summary>
    /// Зразкове значення параметра фабрики.
    /// </summary>
    /// <param name="parameter">Параметр.</param>
    /// <returns>Значення для виклику.</returns>
    /// <exception cref="NotSupportedException">
    /// Тип параметра невідомий — сторож зупиняється, а не пропускає фабрику.
    /// </exception>
    /// <remarks>
    /// ⛔ Невідомий тип — це ПОМИЛКА, а не привід мовчки пропустити запит.
    /// Пропуск зробив би сторожа тим тихішим, чим більше в ньому роботи: нова
    /// фабрика з новим типом параметра просто випадала б із перевірки, і
    /// зелений колір означав би «сторож не зміг», а не «запит правильний».
    /// </remarks>
    private static object SampleOf(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;

        if (type == typeof(long))
        {
            return 1L;
        }

        if (type == typeof(int))
        {
            return 1;
        }

        if (type == typeof(PeriodKey))
        {
            return new PeriodKey(202601);
        }

        if (type == typeof(IReadOnlyList<long>) || type == typeof(IReadOnlyCollection<long>))
        {
            return new List<long> { 1L, 2L };
        }

        if (type == typeof(IReadOnlyList<int>) || type == typeof(IReadOnlyCollection<int>))
        {
            return new List<int> { 1, 2 };
        }

        throw new NotSupportedException(
            $"Сторож WR-05 не вміє будувати зразок для параметра "
            + $"'{parameter.Member.DeclaringType?.Name}.{parameter.Member.Name}({type.Name} {parameter.Name})'. "
            + "Додайте тип сюди — інакше запит лишиться неперевіреним.");
    }

    /// <summary>Партиціоновані таблиці запиту разом з їхніми псевдонімами.</summary>
    /// <param name="sql">Згенерований SQL.</param>
    /// <returns>Пари «таблиця → псевдонім».</returns>
    private static IEnumerable<(string Table, string Alias)> PartitionedSources(string sql)
        => SourceRegex.Matches(sql)
            .Select(m => (Table: m.Groups[1].Value, Alias: m.Groups[2].Value))
            .Where(x => PartitionedTables.Contains(x.Table, StringComparer.Ordinal))
            .Distinct();

    /// <summary>
    /// Чи фільтрується джерело <paramref name="alias"/> за ключем партиції.
    /// </summary>
    /// <param name="sql">Згенерований SQL.</param>
    /// <param name="alias">Псевдонім таблиці в цьому SQL.</param>
    /// <returns><c>true</c>, якщо <c>PeriodKey</c> стоїть у предикаті.</returns>
    /// <remarks>
    /// ⛔ Шукається саме ПРЕДИКАТ — <c>[a].[PeriodKey] =</c>,
    /// <c>[a].[PeriodKey] IN</c> або <c>= [a].[PeriodKey]</c> (права частина
    /// умови з'єднання). Проста присутність <c>[a].[PeriodKey]</c> не
    /// годиться: у запиті, що тягне сутність цілком, цей стовпець є в списку
    /// вибірки ЗАВЖДИ, і перевірка на присутність зеленіла б на будь-якому
    /// запиті до цих таблиць — тобто не перевіряла б нічого. Це і є та сама
    /// хиба, яку ловить
    /// <see cref="Сторож_червоніє_на_запиті_без_предиката_періоду"/>.
    /// </remarks>
    private static bool HasPeriodKeyPredicate(string sql, string alias)
    {
        var column = $@"\[{Regex.Escape(alias)}\]\.\[PeriodKey\]";

        return Regex.IsMatch(sql, column + @"\s*(=|IN\b|>=|<=|>|<)", RegexOptions.None, TimeSpan.FromSeconds(5))
               || Regex.IsMatch(sql, @"=\s*" + column, RegexOptions.None, TimeSpan.FromSeconds(5));
    }

    private static string Indent(string text)
        => string.Join(
            Environment.NewLine,
            text.Split('\n').Select(line => "    " + line.TrimEnd('\r')));

    /// <summary>Джерело запиту: <c>[схема].[Таблиця] AS [псевдонім]</c>.</summary>
    [GeneratedRegex(@"\[\w+\]\.\[(\w+)\]\s+AS\s+\[([^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex { get; }
}
