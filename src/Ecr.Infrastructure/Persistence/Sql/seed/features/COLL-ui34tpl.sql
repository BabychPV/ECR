-- COLL:ui34tpl ── ru/kz UI-34: перелік шаблонів за макетом; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'templates.subtitle', N'ru', N'Шаблон — это структура отчёта: листы, таблицы, колонки, формулы и правила. Документы создаются из опубликованной версии и остаются на ней.'),
    (N'templates.subtitle', N'kz', N'Үлгі — есептің құрылымы: парақтар, кестелер, бағандар, формулалар мен ережелер. Құжаттар жарияланған нұсқадан жасалады және сол нұсқада қалады.'),
    (N'templates.stats', N'ru', N'Сводка по шаблонам'),
    (N'templates.stats', N'kz', N'Үлгілер бойынша жиынтық'),
    (N'templates.stat.all', N'ru', N'шаблонов'),
    (N'templates.stat.all', N'kz', N'үлгі'),
    (N'templates.stat.published', N'ru', N'с опубликованной версией'),
    (N'templates.stat.published', N'kz', N'жарияланған нұсқа'),
    (N'templates.stat.publishedHint', N'ru', N'Показать шаблоны с опубликованной версией'),
    (N'templates.stat.publishedHint', N'kz', N'Жарияланған нұсқасы бар үлгілерді көрсету'),
    (N'templates.stat.drafts', N'ru', N'черновиков в работе'),
    (N'templates.stat.drafts', N'kz', N'жұмыстағы нобай'),
    (N'templates.stat.draftsHint', N'ru', N'Показать шаблоны с открытым черновиком'),
    (N'templates.stat.draftsHint', N'kz', N'Ашық нобайы бар үлгілерді көрсету'),
    (N'templates.search', N'ru', N'Поиск'),
    (N'templates.search', N'kz', N'Іздеу'),
    (N'templates.searchPlaceholder', N'ru', N'Название или код шаблона'),
    (N'templates.searchPlaceholder', N'kz', N'Үлгі атауы немесе коды'),
    (N'templates.currentVersion', N'ru', N'Текущая версия'),
    (N'templates.currentVersion', N'kz', N'Ағымдағы нұсқа'),
    (N'templates.noCurrentVersion', N'ru', N'текущей версии нет'),
    (N'templates.noCurrentVersion', N'kz', N'ағымдағы нұсқа жоқ'),
    (N'templates.notPublished', N'ru', N'Ещё не опубликован'),
    (N'templates.notPublished', N'kz', N'Әлі жарияланбаған'),
    (N'templates.draft', N'ru', N'Черновик'),
    (N'templates.draft', N'kz', N'Нобай'),
    (N'templates.state', N'ru', N'Состояние'),
    (N'templates.state', N'kz', N'Күйі'),
    (N'templates.noMatch', N'ru', N'Нет шаблонов по этим фильтрам'),
    (N'templates.noMatch', N'kz', N'Сүзгілерге сәйкес үлгілер жоқ'),
    (N'templates.draftBy', N'ru', N'Редактирует {name}'),
    (N'templates.draftBy', N'kz', N'{name} өңдеп жатыр'),
    (N'templates.documents', N'ru', N'Документы'),
    (N'templates.documents', N'kz', N'Құжаттар'),
    (N'templates.documentsHint', N'ru', N'Документы в доступных вам проектах, которые используют этот шаблон'),
    (N'templates.documentsHint', N'kz', N'Сізге көрінетін жобалардағы осы үлгіні пайдаланатын құжаттар'),
    (N'templates.updated', N'ru', N'Обновлён'),
    (N'templates.updated', N'kz', N'Жаңартылды'),
    (N'templates.stat.documents', N'ru', N'документов на них'),
    (N'templates.stat.documents', N'kz', N'оларды пайдаланатын құжат'),
    (N'templates.stat.documentsHint', N'ru', N'Показать шаблоны, на которые опираются документы'),
    (N'templates.stat.documentsHint', N'kz', N'Құжаттар сүйенетін үлгілерді көрсету')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui34tpl ── кінець секції ──
-- COLL:ui34tpl ── UI-34: перелік шаблонів за макетом (поточна версія, чернетка, стан, смуга, фільтри); ru/kz — порцією COLL:ui34tpl у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'templates.subtitle', N'en', N'A template is the structure of a report: sheets, tables, columns, formulas and rules. Documents are created from a published version and stay on it.', 1),
    (N'templates.stats', N'en', N'Template summary', 1),
    (N'templates.stat.all', N'en', N'templates', 1),
    (N'templates.stat.published', N'en', N'with a published version', 1),
    (N'templates.stat.publishedHint', N'en', N'Show templates that have a published version', 1),
    (N'templates.stat.drafts', N'en', N'drafts in progress', 1),
    (N'templates.stat.draftsHint', N'en', N'Show templates with an open draft', 1),
    (N'templates.search', N'en', N'Search', 1),
    (N'templates.searchPlaceholder', N'en', N'Template name or code', 1),
    (N'templates.currentVersion', N'en', N'Current version', 1),
    (N'templates.noCurrentVersion', N'en', N'no current version', 1),
    (N'templates.notPublished', N'en', N'Not published yet', 1),
    (N'templates.draft', N'en', N'Draft', 1),
    (N'templates.state', N'en', N'State', 1),
    (N'templates.noMatch', N'en', N'No templates match the filters', 1),
    (N'templates.draftBy', N'en', N'{name} is editing', 1),
    (N'templates.documents', N'en', N'Documents', 1),
    (N'templates.documentsHint', N'en', N'Documents in the projects you can see that use this template', 1),
    (N'templates.updated', N'en', N'Updated', 1),
    (N'templates.stat.documents', N'en', N'documents using them', 1),
    (N'templates.stat.documentsHint', N'en', N'Show templates that documents depend on', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui34tpl ── кінець секції ──
