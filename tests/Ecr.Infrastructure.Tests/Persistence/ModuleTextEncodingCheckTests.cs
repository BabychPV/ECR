using System.Text;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Перевірка <c>PC|mojibake_modules</c> із <c>tools/land/postcheck/PostCheck.sql</c> (вердикт <c>UTF8</c> у
/// <c>Run-PostCheck.ps1</c>): знаходить модулі, чиї тексти записано з кирилицею, прочитаною як ANSI.
/// </summary>
/// <remarks>
/// ⛔ W15h (06.11, живий Land, лише читання): тексти <c>arc.usp_*</c> лишились «Ð…» після #743 — шлях без
/// <c>-f i:65001</c> у репозиторії вже закрито (<c>SqlcmdInputCodePageTests</c>), але процедури з 03/04 вікно не
/// перезастосовувало, і жодна перевірка вікна цього не бачила. Тест виконує блок між маркерами
/// <c>MOJIBAKE:begin/end</c> рівно як він лежить у файлі. Мутація (прогнано): замінити <c>NCHAR(208) + NCHAR(209)</c>
/// на <c>NCHAR(1055)</c> → тест червоний. <c>COLLATE Latin1_General_BIN2</c> — захист від зіставлення без
/// урахування діакритики (там Ð = D); на зіставленні CI_AS тестового сервера його прибирання нічого не міняє.
/// </remarks>
[Collection("SqlServer")]
public sealed class ModuleTextEncodingCheckTests(SqlServerFixture sql)
{
    private const string Begin = "-- MOJIBAKE:begin";
    private const string End = "-- MOJIBAKE:end";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перевірка_вікна_знаходить_модуль_із_кирилицею_прочитаною_як_ANSI_і_не_чіпає_UTF8()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var garbled = $"dbo.zz_mojibake_bad_{suffix}";
        var clean = $"dbo.zz_mojibake_ok_{suffix}";

        // Те, що робив sqlcmd без -f: байти UTF-8 прочитано однобайтовою кодовою сторінкою.
        const string message = "Період уже заархівовано: ґанок і їжак";
        var asAnsi = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(message));
        Assert.StartsWith("Ð", asAnsi, StringComparison.Ordinal);

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await ExecAsync(connection, $"CREATE PROCEDURE {garbled} AS THROW 50015, N'{asAnsi.Replace("'", "''", StringComparison.Ordinal)}', 1;");
            await ExecAsync(connection, $"CREATE PROCEDURE {clean} AS THROW 50015, N'{message}', 1;");

            var pc = await RunCheckAsync(connection);

            var names = pc["mojibake_module_names"].Split(',', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains(garbled, names);
            Assert.DoesNotContain(clean, names);
            Assert.True(int.Parse(pc["mojibake_modules"], System.Globalization.CultureInfo.InvariantCulture) >= 1);

            // Модулі, розгорнуті з Sql/*.sql як UTF-8 (фікстура читає файли як UTF-8): жодного arc./rpt./тригера.
            Assert.DoesNotContain(names, n => n.StartsWith("arc.", StringComparison.Ordinal)
                                           || n.StartsWith("rpt.", StringComparison.Ordinal)
                                           || n.StartsWith("aud.", StringComparison.Ordinal));
        }
        finally
        {
            await ExecAsync(connection, $"DROP PROCEDURE IF EXISTS {garbled}; DROP PROCEDURE IF EXISTS {clean};");
        }
    }

    private static string CheckBlock()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
        var path = Path.Combine(root, "tools", "land", "postcheck", "PostCheck.sql");
        var text = File.ReadAllText(path, Encoding.UTF8);
        var from = text.IndexOf(Begin, StringComparison.Ordinal);
        var to = text.IndexOf(End, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, $"У PostCheck.sql немає блоку {Begin} … {End}");
        return text[(from + Begin.Length)..to];
    }

    private static async Task<Dictionary<string, string>> RunCheckAsync(SqlConnection connection)
    {
        var pc = new Dictionary<string, string>(StringComparer.Ordinal);
        void OnInfo(object? _, SqlInfoMessageEventArgs e)
        {
            foreach (var line in e.Message.Split('\n'))
            {
                var parts = line.Trim().Split('|', 3);
                if (parts.Length == 3 && parts[0] == "PC")
                {
                    pc[parts[1]] = parts[2];
                }
            }
        }

        connection.InfoMessage += OnInfo;
        try
        {
            await ExecAsync(connection, CheckBlock());
        }
        finally
        {
            connection.InfoMessage -= OnInfo;
        }

        Assert.True(pc.ContainsKey("mojibake_modules") && pc.ContainsKey("mojibake_module_names"),
            "Блок не надрукував PC|mojibake_modules і PC|mojibake_module_names");
        return pc;
    }

    private static async Task ExecAsync(SqlConnection connection, string sqlText)
    {
        await using var command = new SqlCommand(sqlText, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}
