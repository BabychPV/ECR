-- PERMITS: types ── ECR-PRM-4094 ── ru/kz: тип дозволу використовується; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-4094', N'ru', N'Тип разрешения используется'),
    (N'err.ECR-PRM-4094', N'kz', N'Рұқсат түрі қолданыста'),
    (N'err.ECR-PRM-4094.published', N'ru', N'Тип разрешения «{code}» опубликован и не удаляется.'),
    (N'err.ECR-PRM-4094.published', N'kz', N'«{code}» рұқсат түрі жарияланған және жойылмайды.'),
    (N'err.ECR-PRM-4094.hasPermits', N'ru', N'По типу разрешения «{code}» уже есть разрешения ({count}): он не удаляется.'),
    (N'err.ECR-PRM-4094.hasPermits', N'kz', N'«{code}» рұқсат түрі бойынша рұқсаттар бар ({count}): ол жойылмайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: types ── ECR-PRM-4094 ── кінець секції ──
