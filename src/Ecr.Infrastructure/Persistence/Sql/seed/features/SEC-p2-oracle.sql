-- SEC:p2-oracle ── ru/kz відмови без номера невидимого проєкту (S18); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-AUTH-0403.columnUsedInHiddenProjects', N'ru', N'Столбец используют проекты, к которым у вас нет доступа уровня Manage.'),
    (N'err.ECR-AUTH-0403.columnUsedInHiddenProjects', N'kz', N'Бағанды сізде Manage деңгейіндегі қолжетімділік жоқ жобалар пайдаланады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- SEC:p2-oracle ── кінець секції ──

