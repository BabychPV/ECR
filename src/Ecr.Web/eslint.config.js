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
            'У компоненті використовуйте токен: c="dimmed", color="red", var(--mantine-...).',
        },
        {
          // Функціональні записи кольору — той самий літерал іншими словами.
          selector: "Literal[value=/^(?:rgb|rgba|hsl|hsla)\\(/]",
          message: 'Колір задається лише темою (src/shared/theme/theme.ts) — ФВ-14.11.',
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
];
