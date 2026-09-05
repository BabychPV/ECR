import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import path from 'node:path';

/**
 * Окремий прогін перевірки доступності (`ФВ-14.16`, `D-127`).
 *
 * ⛔ Окремий НЕ тому, що перевірка необов'язкова — вона блокує CI, — а тому,
 * що вона повільна: `axe` у jsdom обробляє одну сторінку Mantine близько
 * 35 секунд, тобто сім хвилин на дванадцять маршрутів. Повільна перевірка
 * всередині `npm test` робить повільним КОЖЕН прогін під час роботи, а те, що
 * заважає щохвилини, зрештою вимикають.
 *
 * ⚠ Причина повільності — не наші правила: звуження переліку і
 * `resultTypes: ['violations']` не змінили нічого. `axe` викликає
 * `getComputedStyle` на кожному вузлі, а в jsdom це найдорожча операція, яка
 * до того ж перечитує весь набір CSS-змінних теми.
 *
 * ⛔ Конфіг самостійний, а не `mergeConfig(base, …)`: `mergeConfig` ЗЛИВАЄ
 * масиви, тож `exclude` базового конфігу — а він виключає саме `*.a11y.*` —
 * додався б сюди і викинув би єдині файли, заради яких цей конфіг існує.
 * Перевірено: «No test files found».
 *
 * Запуск: `npm run test:a11y`; входить у `tools/verify-all.ps1`.
 */
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.a11y.test.{ts,tsx}'],
    exclude: ['**/node_modules/**', '**/dist/**'],

    // Одна сторінка ~35 с; запас на повільнішу машину.
    testTimeout: 120_000,
    hookTimeout: 120_000,
  },
});
