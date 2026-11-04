-- COLL:ui-nae-node-types ── ru/kz: діалог вузла структури НВ за типом вузла (рівнем) і рівень поля в конструкторі довідника; власна порція; en — нижче в #UiEn ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.nae.form.nodeType', N'ru', N'Тип узла'),
    (N'permits.nae.form.nodeType', N'kz', N'Түйін түрі'),
    (N'permits.nae.form.nodeTypeHint', N'ru', N'Предлагаются только типы, допустимые под этим родителем.'),
    (N'permits.nae.form.nodeTypeHint', N'kz', N'Осы аталық астында рұқсат етілген түрлер ғана ұсынылады.'),
    (N'permits.nae.form.nameHint', N'ru', N'Название на английском обязательно; русское и казахское — по желанию.'),
    (N'permits.nae.form.nameHint', N'kz', N'Ағылшынша атау міндетті; орысша және қазақша — қалауыңызша.'),
    (N'permits.nae.form.naeCodeHint', N'ru', N'Как в разрешении, например 36_pod; сохранится как 0036_pod. Уникален в пределах региона и On/Offshore.'),
    (N'permits.nae.form.naeCodeHint', N'kz', N'Рұқсаттағыдай, мысалы 36_pod; 0036_pod болып сақталады. Аймақ пен On/Offshore шегінде бірегей.'),
    (N'permits.nae.form.naeCodeTaken', N'ru', N'Номер NAE {naeCode} уже занят узлом {holder} в {region}/{area}.'),
    (N'permits.nae.form.naeCodeTaken', N'kz', N'NAE нөмірі {naeCode} {region}/{area} ішінде {holder} түйінінде қолданылады.'),
    (N'permits.nae.form.naeCodeTakenOpen', N'ru', N'Открыть этот узел'),
    (N'permits.nae.form.naeCodeTakenOpen', N'kz', N'Бұл түйінді ашу'),
    (N'permits.nae.form.derived', N'ru', N'Заполняется системой по родителям или загрузке из AF; не редактируется.'),
    (N'permits.nae.form.derived', N'kz', N'Жүйе аталықтар бойынша немесе AF жүктеуінен толтырады; өзгертілмейді.'),
    (N'permits.nae.form.derivedCreate', N'ru', N'Заполнится системой при сохранении по родителям; не редактируется.'),
    (N'permits.nae.form.derivedCreate', N'kz', N'Сақтау кезінде жүйе аталықтар бойынша толтырады; өзгертілмейді.'),
    (N'registries.fieldForm.level', N'ru', N'Тип узла (уровень)'),
    (N'registries.fieldForm.level', N'kz', N'Түйін түрі (деңгей)'),
    (N'registries.fieldForm.levelHint', N'ru', N'Тип узла, к которому относится поле: форма узла показывает его только для этого типа.'),
    (N'registries.fieldForm.levelHint', N'kz', N'Өріс қатысты түйін түрі: түйін формасы оны тек осы түрге көрсетеді.'),
    (N'registries.fieldForm.levelAll', N'ru', N'Все уровни'),
    (N'registries.fieldForm.levelAll', N'kz', N'Барлық деңгейлер'),
    (N'registries.fieldLevelFor', N'ru', N'Уровень поля {field}'),
    (N'registries.fieldLevelFor', N'kz', N'{field} өрісінің деңгейі')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-nae-node-types ── кінець секції ──
-- COLL:ui-nae-node-types ── en: діалог вузла структури НВ за типом вузла і рівень поля в конструкторі довідника; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.nae.form.nodeType', N'en', N'Node type', 1),
    (N'permits.nae.form.nodeTypeHint', N'en', N'Only the types allowed under this parent are offered.', 1),
    (N'permits.nae.form.nameHint', N'en', N'The English name is required; Russian and Kazakh are optional.', 1),
    (N'permits.nae.form.naeCodeHint', N'en', N'As in the permit, for example 36_pod; saved as 0036_pod. Unique within the region and on/offshore.', 1),
    (N'permits.nae.form.naeCodeTaken', N'en', N'NAE number {naeCode} is already used by {holder} in {region}/{area}.', 1),
    (N'permits.nae.form.naeCodeTakenOpen', N'en', N'Open that node', 1),
    (N'permits.nae.form.derived', N'en', N'Filled in by the system from the parents or the AF load; not editable.', 1),
    (N'permits.nae.form.derivedCreate', N'en', N'Filled in by the system on save, from the parents; not editable.', 1),
    (N'registries.fieldForm.level', N'en', N'Node type (level)', 1),
    (N'registries.fieldForm.levelHint', N'en', N'The node type this field belongs to: the node form shows it only for that type.', 1),
    (N'registries.fieldForm.levelAll', N'en', N'All levels', 1),
    (N'registries.fieldLevelFor', N'en', N'Level of field {field}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-nae-node-types ── кінець секції ──
-- COLL:ui-nae-node-types: змінений текст наявного ключа (людина 03.11: назва англійською обов'язкова на кожному вузлі дерева НВ). Епілог оновлює,
-- лише поки в базі стоїть САМЕ старе значення.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'permits.nae.form.nameRequired', N'en', N'Enter a name.', N'Enter the English name.'),
    (N'permits.nae.form.nameRequired', N'ru', N'Введите название.', N'Введите название на английском.'),
    (N'permits.nae.form.nameRequired', N'kz', N'Атауын енгізіңіз.', N'Атауын ағылшынша енгізіңіз.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- COLL:ui-nae-node-types ── кінець секції ──
-- COLL:ui-nae-node-types ── прибрані ключі: район тепер виводить сервер (поле лише для читання), підказка «залиште порожнім» більше ніде не читається ──
INSERT INTO #UiRemoved ([Key], Lang, OldVal)
SELECT v.[Key], v.Lang, v.OldVal
  FROM (VALUES
    (N'permits.nae.form.areaHint', N'en', N'Leave empty when there is no area: it is saved as “-”.'),
    (N'permits.nae.form.areaHint', N'ru', N'Оставьте пустым, если района нет: сохранится как «-».'),
    (N'permits.nae.form.areaHint', N'kz', N'Аудан жоқ болса, бос қалдырыңыз: «-» болып сақталады.')
       ) AS v ([Key], Lang, OldVal)
OPTION (RECOMPILE);
GO
-- COLL:ui-nae-node-types ── кінець секції ──
