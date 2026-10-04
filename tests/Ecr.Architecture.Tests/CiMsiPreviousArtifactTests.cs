using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// L10-10 (AUDIT-2026-10-03 §1J): <c>ci-msi-install.ps1</c> бере попередню MSI лише з прогону
/// цього репозиторію, а не за самим іменем гілки.
/// </summary>
/// <remarks>
/// ⛔ Предмет. PR з форку, чия гілка зветься <c>main</c> чи <c>dev/integration</c>, дає прогін у
/// цьому репозиторії з тим самим <c>head_branch</c>; його артефакт <c>ecr-msi</c> ставився б на
/// ранер як «попередній реліз». Предмет тесту — справжня функція
/// <c>Select-PreviousMsiArtifact</c> зі скрипта (вирізається парсером, як у
/// <see cref="DeployScriptHarness"/>).
/// Мутація (прогнано в pwsh): прибрати умову <c>head_repository_id -eq $RepositoryId</c> →
/// <c>main</c>, <c>lane</c> і <c>forkOnly</c> = 2 (новіший форк); тест червоний.
/// </remarks>
public sealed class CiMsiPreviousArtifactTests
{
    private const string Body = """
        function A($id, $repo, $branch, $created) {
            [pscustomobject]@{ expired = $false; created_at = $created
                workflow_run = [pscustomobject]@{ id = $id; head_repository_id = $repo; head_branch = $branch; head_sha = 'x' } }
        }
        $own = A 1 100 'main' '2026-10-01T00:00:00Z'
        $forkMain = A 2 999 'main' '2026-10-03T00:00:00Z'
        $ownLane = A 3 100 'lane/x' '2026-10-04T00:00:00Z'
        $forkLane = A 4 999 'lane/x' '2026-10-05T00:00:00Z'
        function Id($p) { if ($p) { $p.workflow_run.id } else { 'none' } }
        "main=$(Id (Select-PreviousMsiArtifact -Artifacts @($own, $forkMain, $ownLane, $forkLane) -RepositoryId 100 -RunId 0 -OwnBranch 'lane/x'))"
        "lane=$(Id (Select-PreviousMsiArtifact -Artifacts @($forkMain, $ownLane, $forkLane) -RepositoryId 100 -RunId 0 -OwnBranch 'lane/x'))"
        "forkOnly=$(Id (Select-PreviousMsiArtifact -Artifacts @($forkMain, $forkLane) -RepositoryId 100 -RunId 0 -OwnBranch 'lane/x'))"
        "noRepoId=$(Id (Select-PreviousMsiArtifact -Artifacts @($own) -RepositoryId '' -RunId 0 -OwnBranch 'lane/x'))"
        "sameRun=$(Id (Select-PreviousMsiArtifact -Artifacts @($own) -RepositoryId 100 -RunId 1 -OwnBranch ''))"
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Select-PreviousMsiArtifact"], Body, "ci-msi-install.ps1"));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("main", "1")]
    [InlineData("lane", "3")]
    [InlineData("forkOnly", "none")]
    [InlineData("noRepoId", "none")]
    [InlineData("sameRun", "none")]
    public void Попередня_MSI_лише_з_прогону_цього_репозиторію(string key, string expected) =>
        Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, key));
}
