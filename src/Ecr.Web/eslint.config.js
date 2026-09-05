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
    // У тестах допускається `any` у типізації моків: бібліотеки моків самі
    // ним оперують, і боротьба з цим дала б менш читабельні тести, а не
    // безпечніші.
    files: ['src/**/__tests__/**/*.{ts,tsx}', 'src/test/**/*.{ts,tsx}'],
    rules: { '@typescript-eslint/no-explicit-any': 'off' },
  },
];
