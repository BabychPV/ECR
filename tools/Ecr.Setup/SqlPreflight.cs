using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Ecr.Setup;

/// <summary>
/// Перевіряє, чи ДОСЯЖНИЙ цільовий SQL Server, ДО того як користувач
/// дійде до кроку 6 (Installation).
/// </summary>
/// <remarks>
/// ⛔ Q-228: реальний прогін людиною — майстер довів до кроку 6, запустив
/// deploy-ecr.ps1, і той упав на "Крок 1/7: передумови" з сирим текстом
/// sqlcmd. Ніщо в майстрі не перевіряло сервер раніше кроку 6: користувач
/// вводить SqlInstance/Database на кроці 3 (<see
/// cref="Steps.DatabaseStep"/>), а дізнається про недосяжний сервер лише
/// після ще трьох кроків і повного запуску встановлення.
///
/// ⛔ Q-232: відсутність цільової бази більше НЕ зупиняє цю перевірку —
/// директива людини (2026-09-11) дозволила `deploy-ecr.ps1
/// -CreateDatabaseIfMissing` створювати базу самому на кроці 6 (майстер
/// завжди передає цей прапорець, <see cref="DeployRunner"/>), тож
/// відсутня база на кроці 3 — це очікуваний, підтримуваний стан, а не
/// підстава зупиняти майстра. Ця перевірка й далі підтверджує лише те, що
/// сервер узагалі ДОСЯЖНИЙ під заданими обліковими даними.
/// </remarks>
internal static class SqlPreflight
{
    private const int TimeoutSeconds = 10;

    /// <summary>
    /// Синхронна перевірка (той самий контракт, що <see
    /// cref="IWizardStep"/><c>.Validate</c> — метод виклику не async).
    /// Обмежена в часі: жодного способу зависнути назавжди на недосяжному
    /// сервері немає.
    /// </summary>
    public static bool TryVerifyDatabaseExists(
        string sqlInstance, string database, bool windowsAuth, string sqlLogin, string sqlPassword,
        out string error)
    {
        var timeoutText = TimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        // Той самий запит, що deploy-ecr.ps1 виконує на кроці 1/7 —
        // навмисно: та сама перевірка, лише раніше й з людським текстом.
        var escapedDatabase = database.Replace("'", "''", StringComparison.Ordinal);
        var query = $"IF DB_ID('{escapedDatabase}') IS NULL RAISERROR('database missing', 16, 1);";

        var psi = new ProcessStartInfo("sqlcmd")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-S");
        psi.ArgumentList.Add(sqlInstance);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add(timeoutText);

        if (windowsAuth)
        {
            psi.ArgumentList.Add("-E");
        }
        else
        {
            psi.ArgumentList.Add("-U");
            psi.ArgumentList.Add(sqlLogin);
            // ⚠ Пароль — лише через змінну оточення дочірнього процесу, ніколи
            // в аргументах: той самий принцип, що вже задокументований для
            // SERVICE_PASSWORD у deploy-ecr.ps1 (видимість у списку процесів).
            psi.Environment["SQLCMDPASSWORD"] = sqlPassword;
        }

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add("master");
        psi.ArgumentList.Add("-Q");
        psi.ArgumentList.Add(query);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("sqlcmd не повернув процес.");
        }
        catch (Win32Exception)
        {
            error = "sqlcmd не знайдено. Встановіть SQL Server command-line utilities " +
                    "(той самий інструмент, якого потребує deploy-ecr.ps1) і повторіть.";
            return false;
        }

        using (process)
        {
            // Обидва потоки читаємо асинхронно ДО WaitForExit: sqlcmd, що
            // заповнив би буфер одного з них, інакше міг би зависнути
            // назавжди, чекаючи, поки хтось прочитає інший.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((TimeoutSeconds + 5) * 1000))
            {
                TryKill(process);
                error = $"SQL Server '{sqlInstance}' не відповів за {timeoutText} с. " +
                         "Перевірте назву інстансу й мережевий доступ.";
                return false;
            }

            if (process.ExitCode != 0)
            {
                var stderr = stderrTask.GetAwaiter().GetResult();
                var stdout = stdoutTask.GetAwaiter().GetResult();

                // ⛔ Q-231: "database missing" на деяких машинах опиняється в
                // STDOUT, не в STDERR — перевіряємо обидва потоки.
                //
                // ⛔ Q-232: і це більше НЕ помилка тут узагалі — директива
                // людини (2026-09-11) дозволила deploy-ecr.ps1
                // -CreateDatabaseIfMissing створити базу самому на кроці 6
                // (майстер передає цей прапорець завжди, DeployRunner.cs).
                // Відсутня база на кроці 3 — очікуваний, підтримуваний стан.
                var hasDatabaseMissing =
                    stderr.Contains("database missing", StringComparison.OrdinalIgnoreCase)
                    || stdout.Contains("database missing", StringComparison.OrdinalIgnoreCase);

                if (hasDatabaseMissing)
                {
                    error = string.Empty;
                    return true;
                }

                error = $"Не вдалося підключитися до '{sqlInstance}'. Перевірте назву інстансу, " +
                        "автентифікацію й мережевий доступ.\n\n" +
                        DiagnosticTail(process.ExitCode, stdout, stderr);
                return false;
            }

            _ = stdoutTask.GetAwaiter().GetResult();
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Складає діагностичний хвіст повідомлення: код виходу завжди, обидва
    /// потоки — якщо в них щось є.
    /// </summary>
    /// <remarks>
    /// ⛔ Реальний прогін людиною (день фіксу Q-228): помилка з'єднання
    /// показала ЛИШЕ загальний текст, без жодного діагностичного рядка —
    /// це трапляється, коли причина відмови потрапляє в STDOUT, а не в
    /// STDERR (залежить від версії/драйвера sqlcmd), а попередня версія
    /// цього класу читала для показу тільки STDERR. Код виходу тепер
    /// видно завжди, навіть якщо обидва потоки порожні — це вже само собою
    /// діагностика ("процес вийшов без жодного тексту"), а не порожнеча.
    /// </remarks>
    private static string DiagnosticTail(int exitCode, string stdout, string stderr)
    {
        var lines = new List<string> { $"Код виходу sqlcmd: {exitCode.ToString(CultureInfo.InvariantCulture)}." };

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            lines.Add(stderr.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            lines.Add(stdout.Trim());
        }

        if (lines.Count == 1)
        {
            lines.Add("sqlcmd не вивів жодного тексту помилки — можливо, процес завершився " +
                      "до підключення (антивірус/брандмауер, відсутній драйвер) чи мовчки обірвав з'єднання.");
        }

        return string.Join("\n\n", lines);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Процес уже завершився між перевіркою й Kill — нормально.
        }
    }
}
