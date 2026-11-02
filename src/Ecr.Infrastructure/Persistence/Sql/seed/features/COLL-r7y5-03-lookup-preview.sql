-- COLL:r7y5-03-lookup-preview ── ru/kz: R7-Y5 / Y5-03: перегляд імпорту відхиляє видалений, вимкнений чи нечинний запис довідника (C7) однією коміркою, а не 4223 на всю книгу; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CELL-4223.importDeletedEntry', N'ru', N'Запись справочника с этим кодом удалена, выбрать её нельзя.'),
    (N'err.ECR-CELL-4223.importDeletedEntry', N'kz', N'Осы коды бар анықтамалық жазбасы жойылған, оны таңдауға болмайды.'),
    (N'err.ECR-CELL-4223.importInactiveEntry', N'ru', N'Запись справочника с этим кодом отключена, выбрать её нельзя.'),
    (N'err.ECR-CELL-4223.importInactiveEntry', N'kz', N'Осы коды бар анықтамалық жазбасы өшірілген, оны таңдауға болмайды.'),
    (N'err.ECR-CELL-4223.importEntryNotValidOnDate', N'ru', N'Запись справочника с этим кодом недействительна на последний день периода, выбрать её нельзя.'),
    (N'err.ECR-CELL-4223.importEntryNotValidOnDate', N'kz', N'Осы коды бар анықтамалық жазбасы кезеңнің соңғы күнінде жарамсыз, оны таңдауға болмайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r7y5-03-lookup-preview ── кінець секції ──

-- COLL:r7y5-03-lookup-preview ── R7-Y5 / Y5-03: перегляд імпорту відхиляє видалений, вимкнений чи нечинний запис довідника (C7) однією коміркою, а не 4223 на всю книгу; ru/kz — порцією COLL:r7y5-03-lookup-preview нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CELL-4223.importDeletedEntry', N'en', N'The registry entry with this code has been deleted and cannot be chosen.', 1),
    (N'err.ECR-CELL-4223.importInactiveEntry', N'en', N'The registry entry with this code is switched off and cannot be chosen.', 1),
    (N'err.ECR-CELL-4223.importEntryNotValidOnDate', N'en', N'The registry entry with this code is not valid on the last day of the period and cannot be chosen.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r7y5-03-lookup-preview ── кінець секції ──
