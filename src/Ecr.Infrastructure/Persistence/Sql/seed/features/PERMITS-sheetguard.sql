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

