-- UI:dead-buttons ── ru/kz причин недоступних дій і порожнього стану полів довідника; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.rc816.selectionLocked', N'ru', N'Сначала сохраните или отмените изменения на нижних уровнях.'),
    (N'registries.rc816.selectionLocked', N'kz', N'Алдымен төменгі деңгейлердегі өзгерістерді сақтаңыз немесе болдырмаңыз.'),
    (N'registries.noFields', N'ru', N'У этого справочника пока нет полей'),
    (N'registries.noFields', N'kz', N'Бұл анықтамалықта әзірге өрістер жоқ')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- UI:dead-buttons ── кінець секції ──

-- UI:dead-buttons ── причини недоступних дій і порожній стан полів довідника (клієнт) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.rc816.selectionLocked', N'en', N'Save or discard the changes in the levels below first.', 1),
    (N'registries.noFields', N'en', N'This registry has no fields yet', 1)
    -- ru/kz — окремою порцією `UI:dead-buttons` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- UI:dead-buttons ── кінець секції ──
