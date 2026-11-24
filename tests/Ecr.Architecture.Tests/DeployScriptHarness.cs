using System.Diagnostics;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Виконує СПРАВЖНІ функції <c>tools/deploy-ecr.ps1</c>: вирізає їх парсером PowerShell (AST)
/// і запускає в <c>pwsh</c> (Linux-раннер CI) або <c>powershell.exe</c> (Windows без pwsh).
/// </summary>
/// <remarks>
/// ⛔ Предмет — функції скрипта, не їх копія (той самий підхід, що й у
/// <see cref="DeployWorkerModeTests"/>). Реєстру, msiexec, SQL і сховища сертифікатів тут немає.
/// <para>
/// ⚠ Тіло сценарію й весь вивід — лише ASCII: Windows PowerShell 5.1 читає файл без BOM у кодовій
/// сторінці ANSI, а вивід консолі — теж; кирилиця дала б хибний збій, не пов'язаний із логікою.
/// Рядки з кирилицею у функціях скрипта перевіряються за ASCII-ознаками (коди, лічильники,
/// збіг за латинським фрагментом), а не дослівно.
/// </para>
/// </remarks>
internal static class DeployScriptHarness
{
    private const string Prologue = """
        param([string] $Deploy)
        $ErrorActionPreference = 'Stop'
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Deploy, [ref] $tokens, [ref] $errors)
        if ($errors.Count) { throw "parse: $($errors[0].Message)" }
        """;

    // Без обгортки-функції: крапкове підключення всередині функції лишило б визначення в її власній області.
    private const string ImportTemplate = """
        $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq '__NAME__' }, $true)
        if (-not $fn) { throw "no function __NAME__" }
        . ([scriptblock]::Create($fn.Extent.Text))
        """;

    /// <summary>Виконує <paramref name="body"/> після імпорту <paramref name="functions"/>; повертає рядки <c>ключ=значення</c>.</summary>
    /// <param name="functions">Імена функцій зі скрипта розгортання.</param>
    /// <param name="body">ASCII-сценарій, що друкує рядки <c>ключ=значення</c>.</param>
    /// <param name="script">Скрипт у <c>tools/</c>, з якого вирізаються функції.</param>
    public static Dictionary<string, string> Run(string[] functions, string body, string script = "deploy-ecr.ps1")
    {
        var dir = Path.Combine(Path.GetTempPath(), "ecr-deploy-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var imports = string.Join(Environment.NewLine, functions.Select(f => ImportTemplate.Replace("__NAME__", f, StringComparison.Ordinal)));
            var harness = Path.Combine(dir, "harness.ps1");
            File.WriteAllText(harness, Prologue + Environment.NewLine + imports + Environment.NewLine + body);

            var start = new ProcessStartInfo(FindPowerShell())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness,
                         "-Deploy", Path.Combine(SourceTree.Root, "tools", script) })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell не завершився за 2 хв");
            }

            Assert.True(process.ExitCode == 0, $"PowerShell: код {process.ExitCode}\n{stderr.Result}\n{stdout.Result}");

            return stdout.Result
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Contains('=', StringComparison.Ordinal))
                .Select(l => l.Split('=', 2))
                .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Значення ключа з виводу; падає з усім виводом, якщо ключа немає.</summary>
    /// <param name="results">Результат <see cref="Run"/>.</param>
    /// <param name="key">Ключ.</param>
    public static string Value(IReadOnlyDictionary<string, string> results, string key)
    {
        Assert.True(results.TryGetValue(key, out var value), $"ключа {key} немає у виводі:\n{string.Join('\n', results)}");
        return value!;
    }

    // Лише Linux (раннер CI). pwsh інколи гине SIGSEGV (код 139) на старті: 2026-10-30 push-CI 41024966306 - 9 тестів
    // LandStaleValuesTests поспіль із порожнім виводом, lane/integ-rpt-stable 40977687661 - код 139. Обгортка:
    // (1) не більше двох pwsh одночасно на всі класи (xUnit ганяє класи паралельно);
    // (2) TERM=dumb і вимкнена телеметрія - жодної взаємодії з терміналом/мережею на старті;
    // (3) ОДИН повтор запуску ЛИШЕ за коду 139 (пауза 1 с), причина - у stderr. Інші коди й провали перевірок не повторюються.
    // ✎ 2026-11-23. Картина збою: з якоїсь миті КОЖЕН новий pwsh у job гине 139 до кінця job, повтор теж
    // (45136077424 core - усі запуски після 08:23:47; 45112460489 Evidence - після 04:39:35; 39584163913 - навіть
    // наступний крок workflow). Отже стан спільний і тривкий. Єдиний спільний змінюваний стан pwsh у job -
    // `~/.cache/powershell` (StartupProfileData-NonInteractive: кожен pwsh читає на старті й переписує на виході).
    // (4) Тому кожен запуск отримує ВЛАСНИЙ XDG_CACHE_HOME - спільного файлу більше немає. ⚠ Відтворити 139 через
    // цей файл НЕ вдалося (проба 45152360896: 1200 паралельних стартів зі спільним кешем, 16 прогонів pwsh-класів,
    // 7 видів псування файлу - нуль 139), тож (4) - усунення єдиного відомого спільного стану, а не доведена причина.
    // (5) Тому на першому 139 у тестовому хості обгортка друкує діагностику (пам'ять, диск, процеси, змінні DOTNET_/LD_, і
    // які зміни оточення рятують старт) - наступний випадок назве причину. Повтор (3) - лише страховка.
    private const string LauncherScript = """
        #!/bin/sh
        # Тести інколи звужують PATH: утиліти обгортки (mktemp, cat, rm, flock) шукаємо й у системних каталогах.
        ORIG_PATH="$PATH"
        SYS_PATH="$PATH:/usr/bin:/bin"
        PATH="$SYS_PATH"
        export TERM=dumb POWERSHELL_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
        lockdir="${TMPDIR:-/tmp}"
        if command -v flock >/dev/null 2>&1; then
          while :; do
            for i in 1 2; do
              exec 9>"$lockdir/ecr-pwsh-slot-$i.lock"
              if flock -n 9; then break 2; fi
            done
            sleep 0.2
          done
        fi
        d=$(mktemp -d)
        trap 'rm -rf "$d"' EXIT
        attempt=1
        note=""
        while :; do
          # Власний кеш PowerShell (профіль JIT, ModuleAnalysisCache) на кожну спробу - див. (4) вище.
          mkdir -p "$d/cache$attempt"
          # pwsh бачить ЛИШЕ початковий PATH: тести навмисно звужують його (напр. "git недоступний").
          PATH="$ORIG_PATH" XDG_CACHE_HOME="$d/cache$attempt" "$ECR_REAL_PWSH" -NoLogo "$@" >"$d/out" 2>"$d/err"
          code=$?
          if [ "$code" -eq 139 ] && [ "$attempt" -lt 2 ]; then
            note="ecr-pwsh-launcher: pwsh exit 139 (SIGSEGV on start), launch retried once"
            attempt=2
            sleep 1
            continue
          fi
          break
        done
        cat "$d/out"
        cat "$d/err" >&2
        [ -n "$note" ] && echo "$note; second attempt exit $code" >&2
        [ "$code" -ge 128 ] && [ -z "$note" ] && echo "ecr-pwsh-launcher: pwsh exit $code" >&2
        # (5) Діагностика - один раз на тестовий хост (mkdir атомарний), решта падінь посилаються на неї.
        if [ "$code" -eq 139 ]; then
          if mkdir "$lockdir/ecr-pwsh-139-diag-${GITHUB_RUN_ID:-0}-$PPID" 2>/dev/null; then
            {
              echo "ecr-pwsh-diag: $(date -u +%FT%TZ) $(uname -r) image=${ImageOS:-?}/${ImageVersion:-?}"
              echo "ecr-pwsh-diag: mem $(free -m 2>/dev/null | awk 'NR==2{print "used="$3"MB avail="$7"MB"} NR==3{print "swap="$3"MB"}' | tr '\n' ' ')"
              echo "ecr-pwsh-diag: commit $(grep -E '^(CommitLimit|Committed_AS)' /proc/meminfo 2>/dev/null | tr -s ' ' | tr '\n' ' ')"
              echo "ecr-pwsh-diag: disk tmp=$(df -Pm "$lockdir" 2>/dev/null | awk 'NR==2{print $4}')MB home=$(df -Pm "$HOME" 2>/dev/null | awk 'NR==2{print $4}')MB procs=$(ps -e --no-headers 2>/dev/null | wc -l) pwsh=$(pgrep -c pwsh 2>/dev/null)"
              echo "ecr-pwsh-diag: env $(env | grep -E '^(DOTNET_|COMPlus_|LD_|PSModulePath=|POWERSHELL_|HOME=|XDG_)' | cut -c1-100 | tr '\n' ' ')"
              echo "ecr-pwsh-diag: cache $(ls -la "$HOME/.cache/powershell" 2>&1 | tail -n +2 | awk '{print $5":"$9}' | tr '\n' ' ')"
              for v in "PLAIN=1" "HOME=$d/home" "DOTNET_EnableWriteXorExecute=0" "DOTNET_TieredCompilation=0" "DOTNET_GCgen0size=0x4000000"; do
                mkdir -p "$d/home" "$d/cache-diag"
                env "$v" XDG_CACHE_HOME="$d/cache-diag" "$ECR_REAL_PWSH" -NoProfile -NonInteractive -Command 'exit 0' >/dev/null 2>&1
                echo "ecr-pwsh-diag: start with $v -> exit $?"
              done
            } >&2
          else
            echo "ecr-pwsh-diag: see the first 139 of this test host" >&2
          fi
        fi
        exit $code
        """;

    private static readonly Lazy<string> LinuxLauncher = new(() =>
    {
        var real = FindOnPath("pwsh");
        var dir = Path.Combine(Path.GetTempPath(), "ecr-pwsh-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "pwsh-launch.sh");
        File.WriteAllText(file, LauncherScript.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Дочірній процес успадковує змінну від тестового хоста; значення стале на весь прогін.
        Environment.SetEnvironmentVariable("ECR_REAL_PWSH", real);
        return file;
    });

    private static string FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // ⛔ Не пропуск: без PowerShell сторож мовчав би саме там, де його чекають.
        throw new InvalidOperationException("Немає pwsh у PATH (на Windows — і powershell.exe): тест не може виконати deploy-ecr.ps1.");
    }

    /// <summary><c>pwsh</c> з PATH; на Windows без нього — вбудований <c>powershell.exe</c>. На Linux - обгортка запуску.</summary>
    internal static string FindPowerShell()
    {
        if (OperatingSystem.IsLinux())
        {
            return LinuxLauncher.Value;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe", "powershell.exe" } : ["pwsh"];
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var name in names)
        {
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // ⛔ Не пропуск: без PowerShell сторож мовчав би саме там, де його чекають.
        throw new InvalidOperationException("Немає pwsh у PATH (на Windows — і powershell.exe): тест не може виконати deploy-ecr.ps1.");
    }
}
