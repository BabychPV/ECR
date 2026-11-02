-- PERMITS: sheetguard ── ECR-PRM-4102 ── ru/kz: правку аркуша тримає прив'язка (PM-09b); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-4102', N'ru', N'Эту правку листа удерживает привязка разрешения'),
    (N'err.ECR-PRM-4102', N'kz', N'Парақтың бұл өзгерісін рұқсат байланысы ұстап тұр'),
    (N'err.ECR-PRM-4102.rowMode', N'ru', N'Таблица «{tableCode}» принимает строки разрешения через привязку «{bindingCode}»: фиксированные строки их не принимают. Сначала удалите привязку.'),
    (N'err.ECR-PRM-4102.rowMode', N'kz', N'«{tableCode}» кестесі «{bindingCode}» байланысы арқылы рұқсат жолдарын қабылдайды: тұрақты жолдар оларды қабылдамайды. Алдымен байланысты жойыңыз.'),
    (N'err.ECR-PRM-4102.table', N'ru', N'Таблица «{tableCode}» используется привязкой разрешения «{bindingCode}»: сначала удалите привязку.'),
    (N'err.ECR-PRM-4102.table', N'kz', N'«{tableCode}» кестесін «{bindingCode}» рұқсат байланысы пайдаланады: алдымен байланысты жойыңыз.'),
    (N'err.ECR-PRM-4102.column', N'ru', N'Колонка «{columnCode}» сопоставлена привязкой разрешения «{bindingCode}»: сначала измените или удалите привязку.'),
    (N'err.ECR-PRM-4102.column', N'kz', N'«{columnCode}» бағанын «{bindingCode}» рұқсат байланысы сәйкестендірген: алдымен байланысты өзгертіңіз немесе жойыңыз.'),
    (N'err.ECR-PRM-4102.maxRows', N'ru', N'Таблица «{tableCode}» принимает строки разрешения через привязку «{bindingCode}»: лимит строк нельзя снижать (было {previousMax}, запрошено {newMax}). Сначала удалите привязку.'),
    (N'err.ECR-PRM-4102.maxRows', N'kz', N'«{tableCode}» кестесі «{bindingCode}» байланысы арқылы рұқсат жолдарын қабылдайды: жол шегін төмендетуге болмайды (болған {previousMax}, сұралған {newMax}). Алдымен байланысты жойыңыз.'),
    (N'err.ECR-PRM-4102.maxRowsUnlimited', N'ru', N'Таблица «{tableCode}» принимает строки разрешения через привязку «{bindingCode}»: лимита строк у таблицы не было, поставить его ({newMax}) нельзя. Сначала удалите привязку.'),
    (N'err.ECR-PRM-4102.maxRowsUnlimited', N'kz', N'«{tableCode}» кестесі «{bindingCode}» байланысы арқылы рұқсат жолдарын қабылдайды: кестеде жол шегі болмаған, оны ({newMax}) орнатуға болмайды. Алдымен байланысты жойыңыз.'),
    (N'err.ECR-PRM-4102.sheet', N'ru', N'Лист «{sheetCode}» содержит таблицу «{tableCode}», привязанную к разрешению «{bindingCode}»: сначала удалите привязку.'),
    (N'err.ECR-PRM-4102.sheet', N'kz', N'«{sheetCode}» парағында «{bindingCode}» рұқсат байланысы бекітілген «{tableCode}» кестесі бар: алдымен байланысты жойыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: sheetguard ── ECR-PRM-4102 ── кінець секції ──

-- PERMITS: sheetguard ── ECR-PRM-4102 ── правку аркуша тримає прив'язка (PM-09b); en ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-PRM-4102', N'en', N'A permit binding holds this sheet change', 1),
    (N'err.ECR-PRM-4102.rowMode', N'en', N'Table "{tableCode}" takes permit rows through binding "{bindingCode}": fixed rows cannot accept them. Delete the binding first.', 1),
    (N'err.ECR-PRM-4102.table', N'en', N'Table "{tableCode}" is used by permit binding "{bindingCode}": delete the binding first.', 1),
    (N'err.ECR-PRM-4102.column', N'en', N'Column "{columnCode}" is mapped by permit binding "{bindingCode}": change or delete the binding first.', 1),
    (N'err.ECR-PRM-4102.maxRows', N'en', N'Table "{tableCode}" takes permit rows through binding "{bindingCode}": the row limit cannot be lowered (was {previousMax}, requested {newMax}). Delete the binding first.', 1),
    (N'err.ECR-PRM-4102.maxRowsUnlimited', N'en', N'Table "{tableCode}" takes permit rows through binding "{bindingCode}": the table had no row limit, so a limit ({newMax}) cannot be set. Delete the binding first.', 1),
    (N'err.ECR-PRM-4102.sheet', N'en', N'Sheet "{sheetCode}" holds table "{tableCode}" bound to permit binding "{bindingCode}": delete the binding first.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: sheetguard ── кінець секції ──
