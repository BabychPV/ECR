-- MIMP ── ru/kz імпорту пакета методологій; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.methodologyImportBlocked', N'ru', N'Пакет не импортирован: найдено блокеров — {count}. Ничего не записано; см. отчёт.'),
    (N'err.ECR-CALC-0422.methodologyImportBlocked', N'kz', N'Пакет импортталмады: бөгеттер табылды — {count}. Ештеңе жазылмады; есепті қараңыз.'),
    (N'err.ECR-CALC-0409.methodologyImportConflict', N'ru', N'Пакет не импортирован: версий, которые уже есть с другим содержимым, — {count}. Ничего не записано; дайте изменённым версиям новый номер.'),
    (N'err.ECR-CALC-0409.methodologyImportConflict', N'kz', N'Пакет импортталмады: басқа мазмұнмен бұрыннан бар нұсқалар — {count}. Ештеңе жазылмады; өзгерген нұсқаларға жаңа нөмір беріңіз.'),
    (N'err.ECR-CALC-0422.methodologyImportTimeZone', N'ru', N'Неизвестный часовой пояс {timeZone}: нужен идентификатор IANA, например Asia/Atyrau.'),
    (N'err.ECR-CALC-0422.methodologyImportTimeZone', N'kz', N'Белгісіз уақыт белдеуі {timeZone}: IANA идентификаторы қажет, мысалы Asia/Atyrau.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- MIMP ── кінець секції ──

-- MIMP: імпорт пакета методологій з AF (`ImportMethodologyPackageHandler`, `MethodologyPackageImport.tsx`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.methodologyImportBlocked', N'en', N'The package was not imported: blockers found — {count}. Nothing was written; see the report.', 1),
    (N'err.ECR-CALC-0409.methodologyImportConflict', N'en', N'The package was not imported: versions that already exist with different content — {count}. Nothing was written; give changed versions a new number.', 1),
    (N'err.ECR-CALC-0422.methodologyImportTimeZone', N'en', N'Unknown time zone {timeZone}: an IANA identifier is required, e.g. Asia/Atyrau.', 1)
    -- ru/kz — окремою порцією `MIMP` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- MIMP: кінець секції ──
