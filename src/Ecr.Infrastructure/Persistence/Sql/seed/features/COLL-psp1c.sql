-- COLL:psp1c ── ru/kz місяця колонки (PS-P1C); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.monthColumnWithoutMonth', N'ru', N'Столбец "{columnCode}" помечен как месячный, но месяц не задан.'),
    (N'err.ECR-TMPL-0422.monthColumnWithoutMonth', N'kz', N'"{columnCode}" бағаны айлық деп белгіленген, бірақ ай көрсетілмеген.'),
    (N'err.ECR-TMPL-0422.monthOutOfRange', N'ru', N'Месяц столбца "{columnCode}" должен быть от 1 до 12, получено "{monthNumber}".'),
    (N'err.ECR-TMPL-0422.monthOutOfRange', N'kz', N'"{columnCode}" бағанының айы 1-ден 12-ге дейін болуы керек, алынған мән "{monthNumber}".'),
    (N'err.ECR-TMPL-0422.monthWithoutFlag', N'ru', N'У столбца "{columnCode}" задан месяц, но столбец не помечен как месячный.'),
    (N'err.ECR-TMPL-0422.monthWithoutFlag', N'kz', N'"{columnCode}" бағанында ай көрсетілген, бірақ баған айлық деп белгіленбеген.'),
    (N'columns.month', N'ru', N'Месяц столбца'),
    (N'columns.month', N'kz', N'Баған айы'),
    (N'columns.monthHint', N'ru', N'Календарный месяц, за который столбец хранит данные. Его используют правила периода (окно источника, вне окна разрешения); пусто — столбец не привязан к месяцу.'),
    (N'columns.monthHint', N'kz', N'Баған деректерді сақтайтын күнтізбелік ай. Оны кезең ережелері (дереккөз терезесі, рұқсат терезесінен тыс) пайдаланады; бос — баған айға байланыстырылмаған.'),
    (N'columns.monthNone', N'ru', N'Не привязан к месяцу'),
    (N'columns.monthNone', N'kz', N'Айға байланыстырылмаған'),
    (N'columns.errMonth', N'ru', N'Месяц должен быть целым числом от 1 до 12.'),
    (N'columns.errMonth', N'kz', N'Ай 1-ден 12-ге дейінгі бүтін сан болуы керек.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:psp1c ── кінець секції ──

