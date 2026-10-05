using System.Reflection;
using Ecr.Application.Integration;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Adapters.Tests.Sql;

/// <summary>
/// Гейт покриття для L3-05 (<c>D-279</c>): політика рядка з'єднання Sql-джерела перевіряється не
/// переліком відомих обходів, а повнотою — кожен ключ, який приймає драйвер, і кожен префікс шляху
/// з граматики роздільників.
/// </summary>
/// <remarks>
/// ⚠ Чому цей тест існує (рішення людини 05.10, «покриття гейтами»): L3-05 виправлявся тричі
/// (<c>09cd1a82</c> → <c>d0ec07cf</c> → <c>8f856c2c</c>), а ключі <c>Authentication</c> і
/// <c>Server SPN</c> додано ще пізніше (<c>fb8a16db</c>, <c>5d167e28</c>). Кожен раз обхід знаходило
/// рев'ю, а не гейт: тести перелічували лише ті рядки, які автор уже знав, тож проходили за побудовою.
/// <list type="bullet">
/// <item><see cref="Кожен_ключ_драйвера_класифіковано_і_політика_йому_відповідає"/> — ключ, який
/// з'явився в новій версії <c>Microsoft.Data.SqlClient</c> (або якого автор не помітив), червоніє,
/// доки його не віднесено до класу; клас перевіряється поведінкою, а не лише записом у таблиці.</item>
/// <item><see cref="Server_Certificate_лише_повний_шлях_на_локальному_диску"/> — усі префікси до
/// чотирьох символів з алфавіту роздільників; і <c>\/host</c> (обхід першої версії), і
/// <c>\??\UNC</c> (обхід другої) у корпусі є.</item>
/// </list>
/// </remarks>
public sealed class SqlClientOptionPolicyCoverageTests
{
    private enum OptionClass
    {
        /// <summary>Значення ключа — адреса сервера: link-local/metadata відхиляються.</summary>
        Host,

        /// <summary>Ключ заборонений з будь-яким значенням.</summary>
        Forbidden,

        /// <summary>Дозволене лише одне безпечне значення (білий список значень).</summary>
        RestrictedValue,

        /// <summary>Шлях до файлу на сервері застосунку: лише <c>X:\…</c>.</summary>
        LocalPath,

        /// <summary>Не адреса, не шлях, не спосіб входу: політика його не стосується.</summary>
        Inert,
    }

    /// <summary>
    /// Класифікація канонічних ключів <see cref="SqlConnectionStringBuilder"/>. Новий ключ драйвера без
    /// рядка тут — червоний тест: класифікувати свідомо, а не мовчки дозволити.
    /// </summary>
    private static readonly Dictionary<string, OptionClass> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Data Source"] = OptionClass.Host,
        ["Failover Partner"] = OptionClass.Host,

        ["AttachDbFilename"] = OptionClass.Forbidden,
        ["Enclave Attestation Url"] = OptionClass.Forbidden,
        ["Server SPN"] = OptionClass.Forbidden,
        ["Failover Partner SPN"] = OptionClass.Forbidden,

        ["User Instance"] = OptionClass.RestrictedValue,
        ["Authentication"] = OptionClass.RestrictedValue,

        ["Server Certificate"] = OptionClass.LocalPath,

        ["Initial Catalog"] = OptionClass.Inert,
        ["Integrated Security"] = OptionClass.Inert,
        ["Persist Security Info"] = OptionClass.Inert,
        ["User ID"] = OptionClass.Inert,
        ["Password"] = OptionClass.Inert,
        ["Enlist"] = OptionClass.Inert,
        ["Pooling"] = OptionClass.Inert,
        ["Min Pool Size"] = OptionClass.Inert,
        ["Max Pool Size"] = OptionClass.Inert,
        ["Pool Blocking Period"] = OptionClass.Inert,
        ["Multiple Active Result Sets"] = OptionClass.Inert,
        ["Replication"] = OptionClass.Inert,
        ["Connect Timeout"] = OptionClass.Inert,
        ["Encrypt"] = OptionClass.Inert,
        ["Host Name In Certificate"] = OptionClass.Inert,
        ["Trust Server Certificate"] = OptionClass.Inert,
        ["Load Balance Timeout"] = OptionClass.Inert,
        ["Packet Size"] = OptionClass.Inert,
        ["Type System Version"] = OptionClass.Inert,
        ["Application Name"] = OptionClass.Inert,
        ["Current Language"] = OptionClass.Inert,
        ["Workstation ID"] = OptionClass.Inert,
        ["Transaction Binding"] = OptionClass.Inert,
        ["Application Intent"] = OptionClass.Inert,
        ["Multi Subnet Failover"] = OptionClass.Inert,
        ["Connect Retry Count"] = OptionClass.Inert,
        ["Connect Retry Interval"] = OptionClass.Inert,
        ["Column Encryption Setting"] = OptionClass.Inert,
        ["Attestation Protocol"] = OptionClass.Inert,
        ["Command Timeout"] = OptionClass.Inert,
        ["IP Address Preference"] = OptionClass.Inert,
        ["Context Connection"] = OptionClass.Inert,
    };

    /// <summary>Безпечне значення для класу <see cref="OptionClass.RestrictedValue"/> і заборонене.</summary>
    private static readonly Dictionary<string, (string Allowed, string Denied)> RestrictedValues =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["User Instance"] = ("false", "true"),
            ["Authentication"] = ("SqlPassword", "Active Directory Default"),
        };

    [Fact]
    public void Таблиця_класів_збігається_з_ключами_драйвера()
    {
        var canonical = new SqlConnectionStringBuilder().Keys.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unclassified = canonical.Where(k => !Classes.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var stale = Classes.Keys.Where(k => !canonical.Contains(k)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unclassified.Count == 0,
            "Ключі SqlClient без класу в політиці L3-05 (D-279) — віднеси кожен до класу в "
            + $"{nameof(SqlClientOptionPolicyCoverageTests)}.{nameof(Classes)} і, якщо треба, в "
            + $"DataSourceEndpointPolicy.IsForbiddenSqlClientOption: {string.Join(", ", unclassified)}");
        Assert.True(stale.Count == 0, $"Ключів уже немає в драйвері — прибери з таблиці: {string.Join(", ", stale)}");
    }

    public static TheoryData<string, string> Spellings()
    {
        var data = new TheoryData<string, string>();

        foreach (var (spelling, canonical) in DriverSpellings())
        {
            data.Add(spelling, canonical);
            data.Add(spelling.ToUpperInvariant(), canonical);
        }

        return data;
    }

    /// <summary>
    /// Кожне написання ключа (синоніми драйвера: <c>addr</c>, <c>network address</c>, <c>ServerSPN</c>,
    /// <c>initial file name</c>…) поводиться за класом свого канонічного ключа.
    /// </summary>
    [Theory]
    [MemberData(nameof(Spellings))]
    public void Кожен_ключ_драйвера_класифіковано_і_політика_йому_відповідає(string spelling, string canonical)
    {
        Assert.True(Classes.TryGetValue(canonical, out var cls), $"«{canonical}» без класу (див. тест таблиці)");

        switch (cls)
        {
            case OptionClass.Host:
                Assert.Equal(EndpointVerdict.HostForbidden, Check($"Server=flert;{spelling}=169.254.169.254"));
                Assert.Equal(EndpointVerdict.HostForbidden, Check($"Server=flert;{spelling}=metadata.google.internal"));
                Assert.Equal(EndpointVerdict.Allowed, Check($"Server=flert;{spelling}=flert-2.corp.local"));
                break;

            case OptionClass.Forbidden:
                Assert.Equal(EndpointVerdict.ForbiddenOption, Check($"Server=flert;{spelling}=C:\\data\\x"));
                break;

            case OptionClass.RestrictedValue:
                var (allowed, denied) = RestrictedValues[canonical];
                Assert.Equal(EndpointVerdict.Allowed, Check($"Server=flert;{spelling}={allowed}"));
                Assert.Equal(EndpointVerdict.ForbiddenOption, Check($"Server=flert;{spelling}={denied}"));
                break;

            case OptionClass.LocalPath:
                Assert.Equal(EndpointVerdict.Allowed, Check($"Server=flert;{spelling}=C:\\certs\\flert.cer"));
                Assert.Equal(EndpointVerdict.ForbiddenOption, Check($"Server=flert;{spelling}=\\\\attacker\\share\\c.cer"));
                break;

            case OptionClass.Inert:
                Assert.Equal(EndpointVerdict.Allowed, Check($"Server=flert;{spelling}={TypicalValue(canonical)}"));
                break;

            default:
                Assert.Fail($"Невідомий клас {cls}");
                break;
        }
    }

    /// <summary>
    /// Специфікація (білий список, D-279): <c>Server Certificate</c> — лише <c>X:\…</c> чи <c>X:/…</c>
    /// з ASCII-літерою диска. Корпус — усі префікси довжиною 0–4 з алфавіту, з якого складаються UNC,
    /// NT-шляхи (<c>\??\</c>, <c>\\?\</c>, <c>\\.\</c>), корінь диска й відносні шляхи, перед
    /// <c>attacker\share\c.cer</c>. Оракул — специфікація, а не перелік відомих обходів.
    /// </summary>
    [Fact]
    public void Server_Certificate_лише_повний_шлях_на_локальному_диску()
    {
        const string alphabet = "\\/?.:C ";
        const string tail = @"attacker\share\c.cer";

        var misjudged = new List<string>();
        var count = 0;

        foreach (var prefix in Prefixes(alphabet, maxLength: 4))
        {
            foreach (var value in new[] { prefix + tail, "file:" + prefix + tail })
            {
                count++;
                var expected = IsDriveAbsolute(value.Trim()) ? EndpointVerdict.Allowed : EndpointVerdict.ForbiddenOption;

                if (Check($"Server=flert;Server Certificate={value}") != expected)
                {
                    misjudged.Add($"«{value}» → очікувано {expected}");
                }
            }
        }

        Assert.True(count > 5000, $"Корпус несподівано малий: {count}");
        Assert.True(
            misjudged.Count == 0,
            $"Server Certificate: {misjudged.Count} із {count} значень оцінено не за специфікацією, напр.: "
            + string.Join("; ", misjudged.Take(10)));
    }

    private static EndpointVerdict Check(string connectionString)
        => DataSourceEndpointPolicy.CheckSqlClientConnectionString(connectionString);

    private static bool IsDriveAbsolute(string path)
        => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    private static IEnumerable<string> Prefixes(string alphabet, int maxLength)
    {
        IEnumerable<string> level = [string.Empty];

        for (var length = 0; length <= maxLength; length++)
        {
            foreach (var p in level)
            {
                yield return p;
            }

            level = level.SelectMany(p => alphabet.Select(c => p + c)).ToList();
        }
    }

    private static string TypicalValue(string canonical)
    {
        var value = new SqlConnectionStringBuilder()[canonical]?.ToString();

        // `;`, `=` і лапки в значенні за замовчуванням зламали б рядок; таких немає, але перевіримо.
        Assert.False(value?.IndexOfAny([';', '=', '"', '\'']) >= 0, $"«{canonical}»: незручне значення «{value}»");

        return string.IsNullOrEmpty(value) ? "ecr" : value;
    }

    /// <summary>
    /// Усі написання ключів, які розбирає драйвер, з канонічним ключем. Синоніми SqlClient публічно не
    /// віддає, тож — внутрішній <c>SqlConnectionString.GetParseSynonyms()</c>.
    /// ⛔ Якщо його перейменують в оновленні пакета, тест червоніє явно, а не тихо звужується до
    /// канонічних ключів: гейт не має слабшати мовчки.
    /// </summary>
    private static List<(string Spelling, string Canonical)> DriverSpellings()
    {
        var type = typeof(SqlConnectionStringBuilder).Assembly.GetType("Microsoft.Data.SqlClient.SqlConnectionString")
                   ?? throw new InvalidOperationException("Microsoft.Data.SqlClient.SqlConnectionString не знайдено — онови DriverSpellings");
        var method = type.GetMethod("GetParseSynonyms", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("SqlConnectionString.GetParseSynonyms не знайдено — онови DriverSpellings");

        var synonyms = (IReadOnlyDictionary<string, string>)method.Invoke(null, null)!;

        return synonyms.Select(e => (e.Key, e.Value)).OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
    }
}
