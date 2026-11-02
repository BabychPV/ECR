-- COLL:an120-snapshot-ceiling ── ru/kz: AN-120 / L1-01: рядків зрізу (після правил відбору) більше за стелю; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-RPT-0422.snapshotTooLarge', N'ru', N'В срезе проекта {projectId} за период {periodKey} после правил отбора больше {limit} строк. Срез не построен, потому что неполный срез выглядел бы полным; предыдущий срез остаётся действующим. Сузьте отчёт правилами отбора или стройте его по периодам.'),
    (N'err.ECR-RPT-0422.snapshotTooLarge', N'kz', N'{projectId} жобасының {periodKey} кезеңіндегі кесіндісінде іріктеу ережелерінен кейін {limit} жолдан көп. Кесінді құрылмады, өйткені толық емес кесінді толық болып көрінер еді; алдыңғы кесінді күшінде қалады. Есепті іріктеу ережелерімен тарылтыңыз немесе оны кезең бойынша құрыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an120-snapshot-ceiling ── кінець секції ──

-- COLL:an120-snapshot-ceiling ── AN-120 / L1-01: рядків зрізу (після правил відбору) більше за стелю — відмова, а не обрізання; ru/kz — порцією COLL:an120-snapshot-ceiling нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-RPT-0422.snapshotTooLarge', N'en', N'The snapshot of project {projectId} for period {periodKey} has more than {limit} rows after the selection rules. It was not built, because a partial snapshot would look complete; the previous snapshot stays current. Narrow the report with its selection rules or build it per period.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an120-snapshot-ceiling ── кінець секції ──
