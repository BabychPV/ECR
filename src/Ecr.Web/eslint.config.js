import tsParser from '@typescript-eslint/parser';
import tsPlugin from '@typescript-eslint/eslint-plugin';

/**
 * Правила лінтера.
 *
 * ⚠ Конфіг був відсутній, а скрипт `npm run lint` існував: команда падала з
 * «Invalid option --ext», тобто перевірка ніколи не виконувалася. Це той самий
 * клас дефектів, що й на сервері, — робота, якої ніхто не робить, без жодної
 * ознаки збою.
 *
 * ⛔ Головне правило тут — заборона `any` у продуктивному коді (`05i`,
 * наскрізна вимога 5). `any` не робить код гнучкішим: він вимикає перевірку
 * рівно там, де типи API згенеровані з OpenAPI і саме тому чогось варті.
 */
export default [
  {
    ignores: ['dist/**', 'node_modules/**', 'src/api/schema.d.ts'],
  },
  {
    files: ['src/**/*.{ts,tsx}'],
    languageOptions: {
      parser: tsParser,
      parserOptions: {
        ecmaVersion: 2023,
        sourceType: 'module',
        ecmaFeatures: { jsx: true },
      },
    },
    plugins: { '@typescript-eslint': tsPlugin },
    rules: {
      ...tsPlugin.configs.recommended.rules,

      // `any` у продуктивному коді заборонений наскрізною вимогою пакета.
      '@typescript-eslint/no-explicit-any': 'error',

      // Невикористаний параметр із підкресленням — свідомий: сигнатуру диктує
      // чужий інтерфейс, і прибрати параметр не можна.
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],

      // Порожній блок ловить `catch {}`, у якому забули пояснити, чому мовчимо.
      'no-empty': ['error', { allowEmptyCatch: false }],
      eqeqeq: ['error', 'always', { null: 'ignore' }],
    },
  },
  {
    /*
     * ЕТАП 7, модуль 7.2: літералів кольору й відступу в компонентах немає
     * (`ФВ-14.11`, `D-126`).
     *
     * ⛔ Правило, а не домовленість. П'ятнадцять областей писалися в різний
     * час; без єдиного джерела вони розходяться на п'ять відтінків сірого, і
     * привести їх назад коштує дорожче, ніж написати заново. Домовленість це
     * не втримає: вона діє рівно доти, доки про неї пам'ятають.
     *
     * ⚠ Виняток один — `src/shared/theme/**`: саме там значенням і місце.
     */
    files: ['src/**/*.{ts,tsx}'],
    ignores: ['src/shared/theme/**', 'src/**/__tests__/**', 'src/api/**'],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          // Будь-який шістнадцятковий колір у коді компонента.
          selector: "Literal[value=/^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/]",
          message:
            'Колір задається лише темою (src/shared/theme/theme.ts) — ФВ-14.11. ' +
            'У компоненті використовуйте токен: c="dimmed", color="statusError", var(--mantine-...).',
        },
        {
          // Функціональні записи кольору — той самий літерал іншими словами.
          selector: "Literal[value=/^(?:rgb|rgba|hsl|hsla)\\(/]",
          message: 'Колір задається лише темою (src/shared/theme/theme.ts) — ФВ-14.11.',
        },
        {
          /*
           * W4.2: голе ім'я кольору Mantine, що несе СТАТУС (помилка чи
           * попередження), а не декоративний відтінок.
           *
           * ⛔ Заборона на `#hex` вище не ловила цей канал: `color="red"` не
           * містить жодного символу `#`, тож ~40 місць у `features/pages`
           * доносили сенс «помилка»/«попередження» рядком, який жоден тест
           * контрасту не перевіряв. З'ясувалось гірше: `theme.primaryShade
           * = { light: 6, dark: 5 }` застосовується Mantine до КОЖНОГО
           * кольору (не лише `primaryColor`), а стандартна шкала `red`/
           * `orange` на індексі 5 світла — голий `color="red"` у темній
           * темі рендерив білий текст на світлому червоному тлі, контраст
           * ≈2.2–2.8:1, нижче AA (`W4.2`, PR #88).
           *
           * ⚠ Заборонені лише `red`/`orange` — саме ті два імені, для яких
           * уже існують перевірені контрастом токени (`statusError`/
           * `statusWarning` у theme.ts). Інші кольори Mantine (`green`,
           * `gray`, `blue`, `yellow`...) тут навмисно НЕ заборонені: для
           * них ще немає токена, і заборона без заміни лише зупинила б
           * білд без жодної користі.
           */
          selector: "Literal[value=/^(?:red|orange)$/]",
          message:
            'Голе ім\'я кольору Mantine "red"/"orange" несе статус (помилка/попередження) без ' +
            'перевірки контрасту — W4.2. Використовуйте color="statusError" / color="statusWarning" ' +
            '(src/shared/theme/theme.ts).',
        },
        {
          /*
           * Числовий відступ у пропі Mantine: `gap={4}`, `mt={12}`.
           *
           * ⚠ Шкала кратна 4 (`ФВ-14.12`), і саме тому числа заборонені
           * навіть «правильні»: `gap={4}` і `gap="xs"` дають однаковий
           * піксель сьогодні, але перше не переживе зміни шкали, а друге
           * переживе. Проміжні значення (13, 14) шкала не має навмисно.
           */
          selector:
            'JSXAttribute[name.name=/^(?:m|mt|mb|ml|mr|mx|my|p|pt|pb|pl|pr|px|py|gap)$/] > JSXExpressionContainer > Literal[raw=/^[0-9]+$/]',
          message:
            'Відступ береться зі шкали теми — ФВ-14.12: gap="xs" | "sm" | "md" | "lg" | "xl".',
        },
        {
          /*
           * D-137: форму відповіді сервера описує ЛИШЕ згенерований
           * `schema.d.ts`.
           *
           * ⛔ `A7-34`, `A7-35` і `A7-36` — не три помилки, а три прояви
           * одного: клієнт мав власні рукописні типи відповідей, і ніщо не
           * звіряло їх із тим, що сервер справді віддає. Розбіжності не видно
           * в жодному з двох файлів окремо — лише МІЖ ними.
           *
           * ⚠ Псевдонім (`type XDto = Schemas['XDto']`) дозволений: він не
           * оголошує форми, а коротко називає вже оголошену. Заборонені саме
           * ІНТЕРФЕЙСИ і псевдоніми з літералом об'єкта.
           */
          selector:
            "TSInterfaceDeclaration[id.name=/(Response|Dto|Payload)$/], TSTypeAliasDeclaration[id.name=/(Response|Dto|Payload)$/] > TSTypeLiteral",
          message:
            'Форму відповіді сервера описує лише згенерований schema.d.ts — D-137. ' +
            'Використайте псевдонім: type XDto = Schemas["XDto"].',
        },
        {
          /*
           * D-137, друга половина: форма НЕ описується і на місці виклику.
           *
           * ⛔ Правило вище ловить лише ІМЕНОВАНІ оголошення, і через цю
           * дірку в клієнт потрапило чотири рукописні форми виду
           * `apiFetch<{ projectId: number }>(…)`. Вони не менш небезпечні за
           * іменовані — просто коротші: помилка в назві поля дає `undefined`
           * там, де компілятор обіцяв число, і жоден тип цього не помітить.
           *
           * ⚠ Причина, чому так писали, була справжня: чотирнадцять дій
           * сервера повертали анонімний об'єкт, у схемі на його місці
           * лишалося порожнє тіло, і псевдоніма просто не існувало (`A7-44`).
           * Тепер відповіді іменовані, і обхідний шлях більше не потрібен.
           */
          selector:
            'CallExpression[callee.name=/^(apiFetch|apiFetchIfChanged|apiEnqueue)$/] > TSTypeParameterInstantiation > TSTypeLiteral',
          message:
            'Форму відповіді не описують на місці виклику — D-137. ' +
            'Сервер має повертати іменований запис; використайте псевдонім зі schema.d.ts.',
        },
        {
          /*
           * ФВ-14.30: у полів і кнопок немає ФІКСОВАНОЇ ширини.
           *
           * ⚠ Рядки приходять із сервера трьома мовами (`D-95`), і казахська
           * й російська на 20–40 % довші за англійську. `w={260}`, підібране
           * під англійський підпис, обріже казахський — а перевірити це може
           * лише той, хто відкриє систему казахською, тобто ніхто до UAT.
           *
           * ⚠ `miw` дозволений: мінімальна ширина не заважає рости.
           */
          selector:
            "JSXOpeningElement[name.name=/^(TextInput|NumberInput|PasswordInput|Textarea|Select|MultiSelect|Autocomplete|Button|Badge)$/] > JSXAttribute[name.name='w']",
          message:
            'Фіксована ширина поля або кнопки — ФВ-14.30: використовуйте miw (мінімальну), інакше довший переклад обріжеться.',
        },
        {
          /*
           * ФВ-14.20: у поля має бути ПІДПИС, а не placeholder.
           *
           * ⛔ Правило написане тому, що `axe` цього не ловить: за його
           * правилом `label` непорожній `placeholder` вважається достатнім
           * ім'ям, і поле без підпису проходить перевірку доступності.
           * Перевірено — прибраний `label` не завалив жодного маршруту.
           *
           * ⚠ А вимога саме про підпис: placeholder ЗНИКАЄ при введенні, і
           * користувач, який відвернувся на секунду, більше не знає, що він
           * заповнює. У формі на двадцять полів це не дрібниця.
           */
          selector:
            "JSXOpeningElement[name.name=/^(TextInput|NumberInput|PasswordInput|Textarea|Select|MultiSelect|Autocomplete|DateInput|DatePickerInput|Checkbox|Switch|Radio)$/]:not(:has(JSXAttribute[name.name='label'])):not(:has(JSXAttribute[name.name='aria-label'])):not(:has(JSXAttribute[name.name='aria-labelledby']))",
          message:
            'Поле без підпису — ФВ-14.20: додайте label (placeholder не рахується: він зникає при введенні).',
        },
        {
          /*
           * Відступ, колір або шрифт усередині `style={{…}}`.
           *
           * ⚠ Заборонені саме ці властивості, а не сам `style`: `height:
           * '70vh'` для сітки і `cursor: 'pointer'` для рядка таблиці токенами
           * не задаються і задаватися не мають.
           */
          selector:
            "JSXAttribute[name.name='style'] Property[key.name=/^(?:color|background|backgroundColor|borderColor|margin|marginTop|marginBottom|marginLeft|marginRight|padding|paddingTop|paddingBottom|paddingLeft|paddingRight|fontSize|fontFamily)$/]",
          message:
            'Колір, відступ і шрифт у style заборонені — ФВ-14.11: використовуйте пропи Mantine або токени теми.',
        },
        {
          /*
           * D15-09: нативне поле дати заборонене.
           *
           * ⛔ `<input type="date">` малює браузер, і формат він бере з
           * налаштувань ОС, а не з локалі ПРОДУКТУ. Тобто той самий документ
           * тією самою мовою показує `03.09.2026` одному користувачеві і
           * `9/3/2026` іншому — а різниця між третім вереснем і дев'ятим
           * березнем у звітності про викиди не косметична.
           *
           * ⛔ Заборонено не лише голий `<input>`. Перша редакція правила
           * дивилася саме на нього — і не спіймала ЖОДНОГО з восьми наявних
           * місць: усі вони `<TextInput type="date">`, тобто обгортка Mantine,
           * яка під собою малює той самий нативний `<input type="date">`.
           * Правило, зелене на коді, де порушення є, гірше за відсутнє: воно
           * створює враження перевірки.
           *
           * ⚠ `DateInput`/`DatePickerInput` не заборонені: там формат задає
           * код. Заборонений рівно канал «нативне поле», а не дати взагалі.
           *
           * ⚠ Ті самі граблі в `datetime-local` і `time`: годинний формат
           * (12/24) так само з ОС.
           */
          selector:
            "JSXOpeningElement[name.name=/^(?:input|TextInput|NumberInput|Input)$/] > JSXAttribute[name.name='type'] > Literal[value=/^(?:date|datetime-local|time|month|week)$/]",
          message:
            'Нативне поле дати/часу бере формат з ОС, а не з локалі продукту — D15-09. ' +
            'Використовуйте DateInput/DatePickerInput (@mantine/dates) або shared/format.',
        },
        {
          /*
           * D15-09, друга половина: `toLocaleString()` без локалі.
           *
           * ⛔ Це та сама вада, що й вище, але непомітніша: виклик БЕЗ
           * аргументів мовчки бере локаль браузера. Продукт тримає три мови
           * (`D-95`), і мова інтерфейсу не зобов'язана збігатися з мовою
           * браузера — користувач із англійським Chrome, що працює казахською,
           * отримає англійський роздільник тисяч посеред казахської сторінки.
           *
           * ⚠ `undefined` першим аргументом — те саме, що й без аргументів;
           * це найчастіший спосіб «передати лише опції», тому він заборонений
           * окремим селектором, а не сподіванням на уважність.
           */
          selector:
            "CallExpression[callee.property.name=/^toLocale(?:String|DateString|TimeString)$/][arguments.length=0]",
          message:
            'toLocaleString() без локалі бере локаль БРАУЗЕРА, а не продукту — D15-09. ' +
            'Передайте локаль продукту явно або використовуйте shared/format.',
        },
        {
          selector:
            "CallExpression[callee.property.name=/^toLocale(?:String|DateString|TimeString)$/] > Identifier.arguments:first-child[name='undefined']",
          message:
            'undefined першим аргументом toLocaleString — це локаль БРАУЗЕРА, а не продукту — D15-09. ' +
            'Передайте локаль продукту явно або використовуйте shared/format.',
        },
        {
          /*
           * D15-09, третій канал: час, складений РУКАМИ.
           *
           * ⛔ Дві заборони вище закривали нативне поле й `toLocale*()` без
           * локалі — і пропускали найтихіший спосіб: скласти `ГГ:ХХ` самому з
           * `getHours()`/`getMinutes()`. Саме так і сталося в
           * `useCellPatch.clockLabel`, причому з коментарем, який пояснював,
           * ЧОМУ це нібито єдиний вихід: «явної локалі тут узяти ніде, `kz` не
           * є тегом BCP-47». Локаль береться в `shared/format/formatLocale()`
           * (`kz` → `kk`), і наслідком тієї неправди був 24-годинний запис на
           * англійському екрані, де кожна інша позначка часу — 12-годинна.
           *
           * ⚠ Заборонені саме `getHours`/`getMinutes`/`getSeconds` — складники
           * ЧАСУ ДОБИ. `getFullYear`/`getMonth`/`getDate` дозволені НАВМИСНО:
           * з них будують МАШИННІ формати (`AuditPage.isoDaysAgo` складає
           * `YYYY-MM-DD` саме так і свідомо не через `toISOString()`, бо той
           * зсуває дату на добу в UTC). Заборона на них зробила б із правила
           * шум, який вимикають цілком.
           *
           * ⚠ Тести поза межею цього блоку (`ignores` вище) — і це потрібно:
           * `conflictDetails.test.tsx` складає ручний `ГГ:ХХ` НАВМИСНО, щоб
           * довести, що `formatTime` дає ІНШИЙ запис.
           */
          selector: "CallExpression[callee.property.name=/^get(?:Hours|Minutes|Seconds)$/]",
          message:
            'Час доби не складається руками — D15-09: getHours()/getMinutes() дають запис, ' +
            'який не залежить від мови продукту. Використовуйте formatTime/formatDateTime із shared/format.',
        },
      ],
    },
  },
  {
    // У тестах допускається `any` у типізації моків: бібліотеки моків самі
    // ним оперують, і боротьба з цим дала б менш читабельні тести, а не
    // безпечніші.
    files: ['src/**/__tests__/**/*.{ts,tsx}', 'src/test/**/*.{ts,tsx}'],
    rules: { '@typescript-eslint/no-explicit-any': 'off' },
  },
  {
    files: ['e2e/**/*.ts'],
    languageOptions: {
      parser: tsParser,
      parserOptions: { ecmaVersion: 2023, sourceType: 'module' },
    },
    plugins: { '@typescript-eslint': tsPlugin },
    rules: {
      '@typescript-eslint/no-explicit-any': 'error',
    },
  },
  {
    /*
     * ⛔ У проході без миші МИШІ НЕМАЄ (`ФВ-14.16`, `D-141`).
     *
     * Один клік посеред сценарію робить зеленим прохід, у якому
     * клавіатурою пройти неможливо — тобто саме те, що ця перевірка мала
     * б спіймати. Заборона тримається лінтом, а не домовленістю: рецензент
     * не помітить `.click()` серед сотні рядків, а лінт помітить завжди.
     *
     * ⚠ Правило вузьке навмисно — лише цей файл. У решті прогонів клік
     * законний: `cellStates.spec.ts` міряє пікселі й до способу
     * навігації байдужий.
     */
    files: ['e2e/keyboardPath.spec.ts'],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          selector: "CallExpression[callee.property.name='click']",
          message:
            'У проході без миші клік заборонений — ФВ-14.16: користуйтеся focus() і keyboard.press().',
        },
        {
          selector: "MemberExpression[object.property.name='mouse']",
          message:
            'Миша в проході без миші — ФВ-14.16: сценарій має проходитися самою клавіатурою.',
        },
        {
          selector: "CallExpression[callee.property.name=/^(dblclick|hover|tap|dragTo)$/]",
          message:
            'Указівні дії заборонені в проході без миші — ФВ-14.16.',
        },
      ],
    },
  },
];
