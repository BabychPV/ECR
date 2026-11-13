namespace Ecr.Infrastructure.Persistence.Migrations;

/// <summary>
/// Сирий SQL міграції PT01 (B74 P1 + Land-year L0; дизайн <c>docs/build/B74-L0-PT01-migration-design.md</c>).
/// </summary>
/// <remarks>
/// ⚠ Окремим класом, а не всередині міграції: ті самі тексти кличуть тести міграції (повтор посередині збою), а саму
/// міграцію генерує <c>dotnet ef migrations add</c>. Кожен блок — <c>EXEC(N'…')</c>: ідемпотентний скрипт компілює пакет
/// цілком ДО того, як у ньому з'явиться нова колонка (та сама пастка, що в RPTV01), і всі блоки повторювані
/// (<c>NOT EXISTS</c> / <c>IS NULL</c>).
/// Порядок Up: колонка <c>ProjectTemplateVersion.TemplateId</c> NULL → <see cref="Precheck"/> → <see cref="Backfill"/> →
/// NOT NULL, новий PK і FK → <see cref="CreateTemplateVersionOf"/> → <see cref="AlterVersionOfCompat"/>.
/// Порядок Down: <see cref="DownGuard"/> → <see cref="RestoreVersionOf"/> → <see cref="DropTemplateVersionOf"/> → решта.
/// </remarks>
public static class PT01ProjectTemplatesSql
{
    /// <summary>Номер помилки: шкала проєкту містить версію іншого шаблону (бекфіл 1:1 неможливий).</summary>
    public const int ForeignTemplateTimelineError = 51074;

    /// <summary>Номер помилки Down: проєкт уже має кілька шаблонів (відкат втратив би дані).</summary>
    public const int MultiTemplateDownError = 51075;

    /// <summary>
    /// Відмова, якщо рядок шкали належить іншому шаблону, ніж чинна версія проєкту: такий проєкт уже не одношаблонний,
    /// і бекфіл 1:1 мовчки зробив би неправду.
    /// </summary>
    public static readonly string Precheck = $"""
        EXEC(N'IF EXISTS (SELECT 1
                    FROM doc.ProjectTemplateVersion AS t
                    JOIN cfg.TemplateVersion AS v  ON v.Id = t.TemplateVersionId
                    JOIN doc.Project AS p          ON p.Id = t.ProjectId
                    JOIN cfg.TemplateVersion AS pv ON pv.Id = p.TemplateVersionId
                    WHERE v.TemplateId <> pv.TemplateId)
            THROW {ForeignTemplateTimelineError}, N''PT01: doc.ProjectTemplateVersion has a row of another template than the project version; 1:1 backfill refused.'', 1;');
        """;

    /// <summary>Бекфіл 1:1 з <c>Project.TemplateVersionId</c>: шаблон проєкту, шаблон рядків шкали, шаблон документів.</summary>
    public const string Backfill = """
        EXEC(N'INSERT INTO doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId)
            SELECT p.Id, v.TemplateId, p.TemplateVersionId
            FROM doc.Project AS p
            JOIN cfg.TemplateVersion AS v ON v.Id = p.TemplateVersionId
            WHERE NOT EXISTS (SELECT 1 FROM doc.ProjectTemplate AS x WHERE x.ProjectId = p.Id AND x.TemplateId = v.TemplateId);');
        EXEC(N'UPDATE t SET TemplateId = v.TemplateId
            FROM doc.ProjectTemplateVersion AS t
            JOIN cfg.TemplateVersion AS v ON v.Id = t.TemplateVersionId
            WHERE t.TemplateId IS NULL;');
        EXEC(N'UPDATE d SET TemplateId = v.TemplateId
            FROM doc.Document AS d
            JOIN doc.Project AS p ON p.Id = d.ProjectId
            JOIN cfg.TemplateVersion AS v ON v.Id = p.TemplateVersionId
            WHERE d.TemplateId IS NULL;');
        """;

    /// <summary>
    /// <c>doc.fn_TemplateVersionOf(@ProjectId, @TemplateId, @PeriodKey)</c>: шкала пари (проєкт, шаблон) — найбільший
    /// початок ≤ періоду, раніше першого рядка — перший рядок; без шкали — чинна версія шаблону в проєкті, далі (до P6)
    /// <c>Project.TemplateVersionId</c>, якщо вона цього шаблону.
    /// </summary>
    public const string CreateTemplateVersionOf = """
        EXEC(N'CREATE OR ALTER FUNCTION doc.fn_TemplateVersionOf (@ProjectId int, @TemplateId int, @PeriodKey int)
        RETURNS int
        AS
        BEGIN
            RETURN COALESCE(
                (SELECT TOP (1) t.TemplateVersionId FROM doc.ProjectTemplateVersion t
                 WHERE t.ProjectId = @ProjectId AND t.TemplateId = @TemplateId AND t.FromPeriodKey <= @PeriodKey
                 ORDER BY t.FromPeriodKey DESC),
                (SELECT TOP (1) t.TemplateVersionId FROM doc.ProjectTemplateVersion t
                 WHERE t.ProjectId = @ProjectId AND t.TemplateId = @TemplateId ORDER BY t.FromPeriodKey ASC),
                (SELECT pt.CurrentTemplateVersionId FROM doc.ProjectTemplate pt
                 WHERE pt.ProjectId = @ProjectId AND pt.TemplateId = @TemplateId),
                (SELECT p.TemplateVersionId FROM doc.Project p JOIN cfg.TemplateVersion v ON v.Id = p.TemplateVersionId
                 WHERE p.Id = @ProjectId AND v.TemplateId = @TemplateId));
        END');
        """;

    /// <summary>
    /// Шлях сумісності: <c>doc.fn_VersionOf(@ProjectId, @PeriodKey)</c> зберігає підпис (~15 викликів
    /// <c>EcrDbContext.VersionOf</c>/SQL) і відповідає за ОСНОВНИЙ шаблон проєкту — шаблон <c>Project.TemplateVersionId</c>.
    /// На одношаблонному проєкті результат той самий, що в RPTV01 (доказ — паритетний тест PT01).
    /// </summary>
    public const string AlterVersionOfCompat = """
        EXEC(N'ALTER FUNCTION doc.fn_VersionOf (@ProjectId int, @PeriodKey int)
        RETURNS int
        AS
        BEGIN
            RETURN doc.fn_TemplateVersionOf(
                @ProjectId,
                (SELECT v.TemplateId FROM doc.Project p JOIN cfg.TemplateVersion v ON v.Id = p.TemplateVersionId
                 WHERE p.Id = @ProjectId),
                @PeriodKey);
        END');
        """;

    /// <summary>Down відмовляє, якщо проєкт уже має кілька шаблонів (рядки ProjectTemplate чи шкали) — інакше втрата даних.</summary>
    public static readonly string DownGuard = $"""
        EXEC(N'IF EXISTS (SELECT 1 FROM doc.ProjectTemplate GROUP BY ProjectId HAVING COUNT(*) > 1)
               OR EXISTS (SELECT 1 FROM doc.ProjectTemplateVersion GROUP BY ProjectId HAVING COUNT(DISTINCT TemplateId) > 1)
            THROW {MultiTemplateDownError}, N''PT01 Down refused: a project has more than one template; rollback would lose data.'', 1;');
        """;

    /// <summary>Тіло <c>doc.fn_VersionOf</c> з RPTV01 — повертається у Down.</summary>
    public const string RestoreVersionOf = """
        EXEC(N'ALTER FUNCTION doc.fn_VersionOf (@ProjectId int, @PeriodKey int)
        RETURNS int
        AS
        BEGIN
            RETURN COALESCE(
                (SELECT TOP (1) t.TemplateVersionId FROM doc.ProjectTemplateVersion t
                 WHERE t.ProjectId = @ProjectId AND t.FromPeriodKey <= @PeriodKey ORDER BY t.FromPeriodKey DESC),
                (SELECT TOP (1) t.TemplateVersionId FROM doc.ProjectTemplateVersion t
                 WHERE t.ProjectId = @ProjectId ORDER BY t.FromPeriodKey ASC),
                (SELECT p.TemplateVersionId FROM doc.Project p WHERE p.Id = @ProjectId));
        END');
        """;

    /// <summary>Прибирає нову функцію (Down, після <see cref="RestoreVersionOf"/>).</summary>
    public const string DropTemplateVersionOf = "DROP FUNCTION IF EXISTS doc.fn_TemplateVersionOf;";
}
