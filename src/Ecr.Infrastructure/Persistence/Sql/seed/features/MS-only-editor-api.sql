-- MS:only-editor-api ── ru/kz: відмова формульного авторингу (P-1124-1, рішення людини 24.11 «має бути тільки наш новий редактор») ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.formulaEngineRetired', N'ru', N'Версии формульного движка больше нельзя создавать, копировать, публиковать или импортировать: новые версии методологий — только скрипт. Работайте в редакторе скрипта.'),
    (N'err.ECR-CALC-0422.formulaEngineRetired', N'kz', N'Формула қозғалтқышының нұсқаларын енді жасауға, көшіруге, жариялауға немесе импорттауға болмайды: әдістемелердің жаңа нұсқалары тек скрипт. Скрипт редакторында жұмыс істеңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- MS:only-editor-api ── кінець секції ──

-- MS:only-editor-api ── en: відмова формульного авторингу; ru/kz — порцією MS:only-editor-api вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.formulaEngineRetired', N'en', N'Formula-engine versions can no longer be created, copied, published or imported: new methodology versions are scripts only. Work in the script editor.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- MS:only-editor-api ── кінець секції ──