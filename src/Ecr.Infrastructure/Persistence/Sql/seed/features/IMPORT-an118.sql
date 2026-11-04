-- IMPORT:an118 ── ru/kz R1-02: попередження про книгу без версій рядків і відбитків комірок; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'import.warningsTitle', N'ru', N'Проверьте перед применением'),
    (N'import.warningsTitle', N'kz', N'Қолданар алдында тексеріңіз'),
    (N'import.outdatedWorkbook', N'ru', N'Эта книга выгружена более ранней версией системы. Значения, которые вы в ней не меняли, всё равно могут заменить более новые значения, внесённые другими пользователями или интеграциями после выгрузки, и будут записаны как ваши изменения. Проверьте список изменений или выгрузите документ заново и повторите изменения в новой книге.'),
    (N'import.outdatedWorkbook', N'kz', N'Бұл кітап жүйенің бұрынғы нұсқасымен экспортталған. Сіз онда өзгертпеген мәндер экспорттан кейін басқа пайдаланушылар немесе интеграциялар енгізген жаңарақ мәндерді бәрібір ауыстыруы мүмкін және сіздің өзгерістеріңіз ретінде жазылады. Өзгерістер тізімін тексеріңіз немесе құжатты қайта экспорттап, өзгерістерді жаңа кітапта қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- IMPORT:an118 ── кінець секції ──

-- IMPORT:an118 ── R1-02: попередження перегляду імпорту про книгу, вивантажену до версій рядків і відбитків комірок; ru/kz — порцією IMPORT:an118 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'import.warningsTitle', N'en', N'Check before applying', 1),
    (N'import.outdatedWorkbook', N'en', N'This workbook was exported by an earlier version of the system. Values you did not change in it can still replace newer values entered by others or by integrations after the export, and they will be recorded as your changes. Check the list of changes, or export the document again and repeat your changes in the new workbook.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- IMPORT:an118 ── кінець секції ──
