-- PERMITS: types ── ECR-PRM-0404 ── ru/kz: тип дозволу не знайдено; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0404', N'ru', N'Тип разрешения не найден'),
    (N'err.ECR-PRM-0404', N'kz', N'Рұқсат түрі табылмады'),
    (N'err.ECR-PRM-0404.type', N'ru', N'Типа разрешения «{code}» не существует.'),
    (N'err.ECR-PRM-0404.type', N'kz', N'«{code}» рұқсат түрі жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: types ── ECR-PRM-0404 ── кінець секції ──

