-- UI:dead-buttons ── ru/kz причин недоступних дій і порожнього стану полів довідника; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'reportDefs.addBlocked', N'ru', N'Сначала заполните код, название, версию и код каждого столбца.'),
    (N'reportDefs.addBlocked', N'kz', N'Алдымен кодты, атауды, нұсқаны және әр бағанның кодын толтырыңыз.'),
    (N'reportDefs.removeColumnBlocked', N'ru', N'В отчёте должен остаться хотя бы один столбец.'),
    (N'reportDefs.removeColumnBlocked', N'kz', N'Есепте кемінде бір баған болуы керек.'),
    (N'registries.rc816.selectionLocked', N'ru', N'Сначала сохраните или отмените изменения на нижних уровнях.'),
    (N'registries.rc816.selectionLocked', N'kz', N'Алдымен төменгі деңгейлердегі өзгерістерді сақтаңыз немесе болдырмаңыз.'),
    (N'methodologies.importApplyBlocked', N'ru', N'Сначала проверьте пакет: импорт доступен после проверки, которая создала бы методику.'),
    (N'methodologies.importApplyBlocked', N'kz', N'Алдымен пакетті тексеріңіз: импорт әдістемені құратын тексеруден кейін қолжетімді болады.'),
    (N'registries.noFields', N'ru', N'У этого справочника пока нет полей'),
    (N'registries.noFields', N'kz', N'Бұл анықтамалықта әзірге өрістер жоқ'),
    (N'reportDefs.newVersionBlocked', N'ru', N'Сначала введите номер новой версии.'),
    (N'reportDefs.newVersionBlocked', N'kz', N'Алдымен жаңа нұсқаның нөмірін енгізіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- UI:dead-buttons ── кінець секції ──

-- UI:dead-buttons ── причини недоступних дій і порожній стан полів довідника (клієнт) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'reportDefs.addBlocked', N'en', N'Fill in the code, the name, the version and a code for every column first.', 1),
    (N'reportDefs.removeColumnBlocked', N'en', N'A report needs at least one column.', 1),
    (N'registries.rc816.selectionLocked', N'en', N'Save or discard the changes in the levels below first.', 1),
    (N'methodologies.importApplyBlocked', N'en', N'Check the package first: importing becomes available after a check that would create the methodology.', 1),
    (N'registries.noFields', N'en', N'This registry has no fields yet', 1),
    (N'reportDefs.newVersionBlocked', N'en', N'Enter the number of the new version first.', 1)
    -- ru/kz — окремою порцією `UI:dead-buttons` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- UI:dead-buttons ── кінець секції ──
