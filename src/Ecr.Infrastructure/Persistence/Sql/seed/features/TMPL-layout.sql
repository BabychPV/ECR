-- TMPL:layout ── ru/kz: розкладка версії шаблону (D-474); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SCHM-0409.templateLayoutBreaking', N'ru', N'Раскладку версии нельзя изменить: к этой версии шаблона уже привязаны документы. Клонируйте версию и задайте раскладку в новой.'),
    (N'err.ECR-SCHM-0409.templateLayoutBreaking', N'kz', N'Нұсқаның орналасуын өзгертуге болмайды: бұл үлгі нұсқасына құжаттар бұрыннан тіркелген. Нұсқаны көшіріп, орналасуды жаңасында орнатыңыз.'),
    (N'err.ECR-TMPL-0422.templateLayoutUnknown', N'ru', N'Неизвестная раскладка версии шаблона.'),
    (N'err.ECR-TMPL-0422.templateLayoutUnknown', N'kz', N'Үлгі нұсқасының орналасуы белгісіз.'),
    (N'err.ECR-TMPL-0422.yearlyProjectNeedsYearLayout', N'ru', N'Годовому проекту нужна версия шаблона с раскладкой «Год, месяцы в столбцах».'),
    (N'err.ECR-TMPL-0422.yearlyProjectNeedsYearLayout', N'kz', N'Жылдық жобаға «Жыл, айлар бағандарда» орналасуы бар үлгі нұсқасы қажет.'),
    (N'err.ECR-TMPL-0422.yearLayoutPeriodFormula', N'ru', N'Раскладка «Год, месяцы в столбцах»: в таблице {tableCode} формулы немесячных столбцов зависят от месяца ([Period]) — {count}: {columns}. Оставьте раскладку «Копия на каждый месяц» или перенесите расчёт в месячные столбцы.'),
    (N'err.ECR-TMPL-0422.yearLayoutPeriodFormula', N'kz', N'«Жыл, айлар бағандарда» орналасуы: {tableCode} кестесінде айлық емес бағандардың формулалары айға тәуелді ([Period]) — {count}: {columns}. «Әр айға көшірме» орналасуын қалдырыңыз немесе есептеуді айлық бағандарға көшіріңіз.'),
    (N'err.ECR-TMPL-0422.yearLayoutNonMonthBinding', N'ru', N'Раскладка «Год, месяцы в столбцах»: в таблице {tableCode} немесячные столбцы привязаны к методологиям — {count}: {columns}. Результат методологии месячный; оставьте раскладку «Копия на каждый месяц» или привяжите методологию к месячным столбцам.'),
    (N'err.ECR-TMPL-0422.yearLayoutNonMonthBinding', N'kz', N'«Жыл, айлар бағандарда» орналасуы: {tableCode} кестесінде айлық емес бағандар әдістемелерге байланған — {count}: {columns}. Әдістеме нәтижесі айлық; «Әр айға көшірме» орналасуын қалдырыңыз немесе әдістемені айлық бағандарға байлаңыз.'),
    (N'err.ECR-TMPL-0422.monthInputAliasInvalid', N'ru', N'Псевдонимы месячных входов методологий в таблице {tableCode} некорректны — {count}: {aliases}. Цель псевдонима — месячный столбец той же таблицы с тем же месяцем, имя не совпадает с кодом столбца, версия — «Год, месяцы в столбцах», таблица — «месяцы в столбцах».'),
    (N'err.ECR-TMPL-0422.monthInputAliasInvalid', N'kz', N'{tableCode} кестесіндегі әдістемелердің айлық кірістерінің бүркеншік аттары дұрыс емес — {count}: {aliases}. Бүркеншік аттың мақсаты — сол кестенің сол айдағы айлық бағаны, аты баған кодымен сәйкес келмейді, нұсқа — «Жыл, айлар бағандарда», кесте — «айлар бағандарда».'),
    (N'template.layout.label', N'ru', N'Раскладка'),
    (N'template.layout.label', N'kz', N'Орналасу'),
    (N'template.layout.perMonthCopies', N'ru', N'Копия на каждый месяц'),
    (N'template.layout.perMonthCopies', N'kz', N'Әр айға көшірме'),
    (N'template.layout.yearWithMonthColumns', N'ru', N'Год, месяцы в столбцах'),
    (N'template.layout.yearWithMonthColumns', N'kz', N'Жыл, айлар бағандарда'),
    (N'template.layout.hint', N'ru', N'Раскладка определяет, к какому периоду относится ячейка. Меняется только в черновике.'),
    (N'template.layout.hint', N'kz', N'Орналасу ұяшықтың қай кезеңге жататынын анықтайды. Тек нобайда өзгертіледі.'),
    (N'template.layout.saved', N'ru', N'Раскладка сохранена'),
    (N'template.layout.saved', N'kz', N'Орналасу сақталды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- TMPL:layout ── кінець секції ──
-- TMPL:layout ── PUT /template-versions/{id}/layout; ru/kz — порцією TMPL:layout вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SCHM-0409.templateLayoutBreaking', N'en', N'The layout cannot be changed: documents are already attached to this template version. Clone the version and set the layout there.', 1),
    (N'err.ECR-TMPL-0422.templateLayoutUnknown', N'en', N'Unknown template version layout.', 1),
    (N'err.ECR-TMPL-0422.yearlyProjectNeedsYearLayout', N'en', N'A yearly project needs a template version with the "Year, months in columns" layout.', 1),
    (N'err.ECR-TMPL-0422.yearLayoutPeriodFormula', N'en', N'"Year, months in columns" layout: in table {tableCode}, formulas of non-month columns depend on the month ([Period]) — {count}: {columns}. Keep the "A copy per month" layout or move the calculation to month columns.', 1),
    (N'err.ECR-TMPL-0422.yearLayoutNonMonthBinding', N'en', N'"Year, months in columns" layout: in table {tableCode}, non-month columns are bound to methodologies — {count}: {columns}. A methodology result is monthly; keep the "A copy per month" layout or bind the methodology to month columns.', 1),
    (N'err.ECR-TMPL-0422.monthInputAliasInvalid', N'en', N'Month input aliases of methodologies in table {tableCode} are invalid — {count}: {aliases}. An alias must point to a month column of the same table with the same month, must not equal a column code, the version must use the "Year, months in columns" layout and the table the "months in columns" layout.', 1),
    (N'template.layout.label', N'en', N'Layout', 1),
    (N'template.layout.perMonthCopies', N'en', N'A copy per month', 1),
    (N'template.layout.yearWithMonthColumns', N'en', N'Year, months in columns', 1),
    (N'template.layout.hint', N'en', N'The layout defines which period a cell belongs to. It can be changed only in a draft.', 1),
    (N'template.layout.saved', N'en', N'Layout saved', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- TMPL:layout ── кінець секції ──
