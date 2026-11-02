-- PERMITS: types ── ECR-PRM-4091 ── ru/kz: код типу дозволу зайнятий; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-4091', N'ru', N'Код типа разрешения занят'),
    (N'err.ECR-PRM-4091', N'kz', N'Рұқсат түрінің коды бос емес'),
    (N'err.ECR-PRM-4091.code', N'ru', N'Тип разрешения с кодом «{code}» уже существует.'),
    (N'err.ECR-PRM-4091.code', N'kz', N'«{code}» кодты рұқсат түрі бұрыннан бар.'),
    (N'err.ECR-PRM-4091.registryCode', N'ru', N'Код справочника «{registryCode}», нужный типу разрешения, уже занят.'),
    (N'err.ECR-PRM-4091.registryCode', N'kz', N'Рұқсат түріне қажет «{registryCode}» анықтамалық коды бос емес.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: types ── ECR-PRM-4091 ── кінець секції ──

