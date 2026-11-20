using Ecr.MethodologyScriptMigration.Loading;

namespace Ecr.MethodologyScriptMigration.Cli;

/// <summary>Файл скрипта у теці виводу: <c>&lt;Код&gt;/&lt;Версія&gt;.ecrs</c>.</summary>
/// <param name="FullPath">Повний шлях.</param>
/// <param name="CodeName">Ім'я теки (код методології, безпечне для файлової системи).</param>
/// <param name="VersionName">Ім'я файла без розширення (версія, безпечна для файлової системи).</param>
internal sealed record ScriptFile(string FullPath, string CodeName, string VersionName)
{
    /// <summary>Відносна назва для повідомлень.</summary>
    public string RelativeName => CodeName + "/" + VersionName + ScriptFiles.Extension;
}

/// <summary>Імена файлів скриптів і зіставлення їх із версіями бази.</summary>
internal static class ScriptFiles
{
    /// <summary>Розширення файла скрипта.</summary>
    public const string Extension = ".ecrs";

    /// <summary>Ім'я, безпечне для файлової системи: недозволені символи → <c>_</c>.</summary>
    /// <param name="name">Код чи версія.</param>
    public static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Create(name.Length, name, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = Array.IndexOf(invalid, source[i]) >= 0 ? '_' : source[i];
            }
        });
    }

    /// <summary>Відносна назва <c>Код/Версія.ecrs</c> (через <c>/</c>).</summary>
    public static string RelativeName(string code, string version) => Safe(code) + "/" + Safe(version) + Extension;

    /// <summary>Повний шлях файла в теці.</summary>
    public static string PathOf(string directory, string code, string version)
        => Path.Combine(directory, Safe(code), Safe(version) + Extension);

    /// <summary>Усі файли <c>*.ecrs</c> другого рівня теки, впорядковані.</summary>
    public static IReadOnlyList<ScriptFile> Enumerate(string directory)
    {
        var result = new List<ScriptFile>();
        foreach (var codeDirectory in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
        {
            foreach (var file in Directory.EnumerateFiles(codeDirectory, "*" + Extension).OrderBy(f => f, StringComparer.Ordinal))
            {
                result.Add(new ScriptFile(
                    file, Path.GetFileName(codeDirectory), Path.GetFileNameWithoutExtension(file)));
            }
        }

        return result;
    }

    /// <summary>Версія бази, що відповідає файлу; <c>null</c> — немає або назва неоднозначна.</summary>
    public static VersionRef? Find(IReadOnlyList<VersionRef> versions, ScriptFile file)
    {
        var matches = versions
            .Where(v => string.Equals(Safe(v.MethodologyCode), file.CodeName, StringComparison.Ordinal)
                        && string.Equals(Safe(v.Version), file.VersionName, StringComparison.Ordinal))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
