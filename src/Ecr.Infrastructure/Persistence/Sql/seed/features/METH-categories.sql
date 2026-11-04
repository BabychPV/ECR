-- METH:categories ── ru/kz: перелік категорій констант версії і похідні аргументи (план CONST-CATEGORY); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0404.category', N'ru', N'Категории «{category}» нет в перечне категорий этой версии методологии.'),
    (N'err.ECR-CALC-0404.category', N'kz', N'«{category}» санаты әдістеменің осы нұсқасының санаттар тізімінде жоқ.'),
    (N'err.ECR-CALC-0409.categoryInUse', N'ru', N'Категория «{category}» — ключ сопоставления для {count} констант: её нельзя удалить или вывести из употребления, пока эти константы не перенесены в другую категорию.'),
    (N'err.ECR-CALC-0409.categoryInUse', N'kz', N'«{category}» санаты {count} тұрақтының сәйкестендіру кілті: бұл тұрақтылар басқа санатқа ауыстырылмайынша, оны жоюға немесе қолданыстан шығаруға болмайды.'),
    (N'err.ECR-CALC-0409.categoryRename', N'ru', N'Категория «{existing}» уже есть; «{category}» отличается только регистром. Коды категорий не переименовываются: добавьте новую категорию и перенесите в неё константы.'),
    (N'err.ECR-CALC-0409.categoryRename', N'kz', N'«{existing}» санаты бар; «{category}» тек әріп регистрімен ерекшеленеді. Санат кодтары қайта аталмайды: жаңа санат қосып, тұрақтыларды соған ауыстырыңыз.'),
    (N'err.ECR-CALC-0422.categoryCodeInvalid', N'ru', N'Категория «{category}»: код — от 1 до {maxLength} символов без пробелов по краям, описание — не длиннее 400 символов.'),
    (N'err.ECR-CALC-0422.categoryCodeInvalid', N'kz', N'«{category}» санаты: код — шетінде бос орынсыз 1-ден {maxLength} таңбаға дейін, сипаттама — 400 таңбадан аспауы керек.'),
    (N'err.ECR-CALC-0422.constantCategoryUnknown', N'ru', N'Константа {constantCode}: категории «{category}» нет в перечне категорий версии. Добавьте её в перечень или выберите существующую.'),
    (N'err.ECR-CALC-0422.constantCategoryUnknown', N'kz', N'{constantCode} тұрақтысы: «{category}» санаты нұсқаның санаттар тізімінде жоқ. Оны тізімге қосыңыз немесе барын таңдаңыз.'),
    (N'err.ECR-CALC-0422.constantCategoryRetired', N'ru', N'Константа {constantCode}: категория «{category}» выведена из употребления, выбрать её нельзя.'),
    (N'err.ECR-CALC-0422.constantCategoryRetired', N'kz', N'{constantCode} тұрақтысы: «{category}» санаты қолданыстан шығарылған, оны таңдауға болмайды.'),
    (N'publish.warning.categoryRuleLiteralNoCategory', N'ru', N'Правило категории может вернуть «{category}», но в перечне категорий версии такой категории нет: ни одна константа не может быть к ней привязана.'),
    (N'publish.warning.categoryRuleLiteralNoCategory', N'kz', N'Санат ережесі «{category}» мәнін қайтаруы мүмкін, бірақ нұсқаның санаттар тізімінде мұндай санат жоқ: бірде-бір тұрақты оған байланыстырыла алмайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- METH:categories ── кінець секції ──
-- METH:categories ── en: перелік категорій констант версії і похідні аргументи (план CONST-CATEGORY, 2026-11-02); ru/kz — порція METH:categories нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0404.category', N'en', N'Category "{category}" is not in the category list of this methodology version.', 1),
    (N'err.ECR-CALC-0409.categoryInUse', N'en', N'Category "{category}" is the matching key of {count} constant(s): it cannot be removed or retired until those constants are moved to another category.', 1),
    (N'err.ECR-CALC-0409.categoryRename', N'en', N'Category "{existing}" already exists; "{category}" differs only in letter case. Category codes are not renamed: add a new category and move the constants to it.', 1),
    (N'err.ECR-CALC-0422.categoryCodeInvalid', N'en', N'Category "{category}": the code must be 1 to {maxLength} characters without leading or trailing spaces, and the description at most 400 characters.', 1),
    (N'err.ECR-CALC-0422.constantCategoryUnknown', N'en', N'Constant {constantCode}: category "{category}" is not in the category list of this version. Add it to the list or choose an existing category.', 1),
    (N'err.ECR-CALC-0422.constantCategoryRetired', N'en', N'Constant {constantCode}: category "{category}" is retired and cannot be chosen.', 1),
    (N'publish.warning.categoryRuleLiteralNoCategory', N'en', N'The category rule can return "{category}", but the category list of the version has no such category: no constant can be narrowed to it.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- METH:categories ── кінець секції ──
