-- PERMITS: bindings ── ECR-SCHM-0409 ── ru/kz: видалення прив'язки у версії з документами руйнівне; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SCHM-0409.permitBindingBreaking', N'ru', N'Удаление привязки разрешения «{bindingCode}» разрушительно: к этой версии уже привязаны документы, строки которых созданы из разрешения.'),
    (N'err.ECR-SCHM-0409.permitBindingBreaking', N'kz', N'«{bindingCode}» рұқсат байланысын жою бұзушы әрекет: осы нұсқаға құжаттар байланған, олардың жолдары рұқсаттан жасалған.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: bindings ── ECR-SCHM-0409 ── кінець секції ──

