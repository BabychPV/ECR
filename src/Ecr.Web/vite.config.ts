import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  server: {
    port: 5173,
    proxy: {
      // Проксі на API, щоб cookie працювала без CORS у розробці
      '/api': { target: 'http://localhost:5080', changeOrigin: true, secure: false },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],

    /*
     * ⛔ Перевірка доступності виключена зі звичайного прогону і має власний
     * конфіг (`vitest.a11y.config.ts`). Вона не необов'язкова — вона блокує
     * CI, — але `axe` у jsdom обробляє одну сторінку близько 35 секунд, і
     * додавати сім хвилин до кожного `npm test` під час роботи означало б, що
     * тести перестануть запускати.
     */
    /*
     * ⛔ `e2e/**` теж виключено. Vitest збирає файли за шаблоном
     * `*.spec.ts` і підхоплював специфікації Playwright — той падав із
     * «Playwright Test did not expect test() to be called here», і весь
     * набір ставав червоним.
     *
     * ⚠ Знайдено аудитом, а не прогоном: `npm test` я запускав ДО того, як
     * з'явився каталог `e2e/`, і зелений результат був правдою рівно доти.
     */
    exclude: [
      '**/node_modules/**',
      '**/dist/**',
      'e2e/**',
      'src/**/*.a11y.test.{ts,tsx}',
    ],
  },
});
