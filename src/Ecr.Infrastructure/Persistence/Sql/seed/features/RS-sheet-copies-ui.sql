-- RS:sheet-copies-ui ── ru/kz: копії аркушів у документі (дії над смугою вкладок) і прапорець «можна копіювати» в редакторі шаблону; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.sheetCopies.group', N'ru', N'Копии листа'),
    (N'documents.sheetCopies.group', N'kz', N'Парақ көшірмелері'),
    (N'documents.sheetCopies.add', N'ru', N'Добавить копию'),
    (N'documents.sheetCopies.add', N'kz', N'Көшірме қосу'),
    (N'documents.sheetCopies.rename', N'ru', N'Переименовать'),
    (N'documents.sheetCopies.rename', N'kz', N'Атауын өзгерту'),
    (N'documents.sheetCopies.renameTitle', N'ru', N'Переименовать листы'),
    (N'documents.sheetCopies.renameTitle', N'kz', N'Парақтардың атауын өзгерту'),
    (N'documents.sheetCopies.nameLabel', N'ru', N'Название'),
    (N'documents.sheetCopies.nameLabel', N'kz', N'Атауы'),
    (N'documents.sheetCopies.delete', N'ru', N'Удалить копию'),
    (N'documents.sheetCopies.delete', N'kz', N'Көшірмені жою'),
    (N'documents.sheetCopies.deleteTitle', N'ru', N'Удалить копию «{name}»?'),
    (N'documents.sheetCopies.deleteTitle', N'kz', N'«{name}» көшірмесін жою керек пе?'),
    (N'documents.sheetCopies.deleteText', N'ru', N'Копия и введённые в неё значения будут удалены из документа. Оригинал и другие копии не затрагиваются.'),
    (N'documents.sheetCopies.deleteText', N'kz', N'Көшірме және оған енгізілген мәндер құжаттан жойылады. Түпнұсқа мен басқа көшірмелер өзгермейді.'),
    (N'sheets.copyable', N'ru', N'Можно копировать в документе'),
    (N'sheets.copyable', N'kz', N'Құжатта көшіруге болады'),
    (N'sheets.copyableHint', N'ru', N'В документе этот лист можно скопировать: у копий те же таблицы, значения у каждой свои.'),
    (N'sheets.copyableHint', N'kz', N'Құжатта бұл парақты көшіруге болады: көшірмелерде кестелер бірдей, мәндері әрқайсысында бөлек.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- RS:sheet-copies-ui ── кінець секції ──

-- RS:sheet-copies-ui ── копії аркушів у документі і прапорець «можна копіювати»; ru/kz — порцією RS:sheet-copies-ui у блоці I18N вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.sheetCopies.group', N'en', N'Sheet copies', 1),
    (N'documents.sheetCopies.add', N'en', N'Add copy', 1),
    (N'documents.sheetCopies.rename', N'en', N'Rename', 1),
    (N'documents.sheetCopies.renameTitle', N'en', N'Rename sheets', 1),
    (N'documents.sheetCopies.nameLabel', N'en', N'Name', 1),
    (N'documents.sheetCopies.delete', N'en', N'Delete copy', 1),
    (N'documents.sheetCopies.deleteTitle', N'en', N'Delete copy “{name}”?', 1),
    (N'documents.sheetCopies.deleteText', N'en', N'The copy and the values entered in it will be removed from the document. The original and the other copies are not affected.', 1),
    (N'sheets.copyable', N'en', N'Can be copied in the document', 1),
    (N'sheets.copyableHint', N'en', N'The sheet can be copied inside a document: copies have the same tables, each with its own values.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- RS:sheet-copies-ui ── кінець секції ──
