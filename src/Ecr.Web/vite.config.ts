import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  build: {
    /*
     * ⛔ Маніфест потрібен НЕ для розгортання, а для гейту бюджету (`D-132`).
     * Бюджет каже «чанк маршруту ≤ 250 КБ gzip», і щоб порахувати, скільки
     * важить маршрут, треба знати, які чанки тягне його чанк — а це є лише
     * тут. Без маніфесту перевірка вміла б зважити окремі файли і не вміла б
     * відповісти на єдине питання, яке має значення: скільки чекає людина,
     * що відкриває цю адресу.
     */
    manifest: true,
  },
  server: {
    port: 5173,
    proxy: {
      // Проксі на API, щоб cookie працювала без CORS у розробці
      '/api': { target: 'http://localhost:5080', changeOrigin: true, secure: false },

      // ⛔ `/health/*` живе ПОЗА `/api/v1` навмисно (`HealthResponse.cs`):
      // інсталятор і зовнішній моніторинг читають його за стабільною,
      // не версійованою адресою. Без цього запису Vite віддає SPA-фолбек
      // (`index.html`) замість JSON — сторінка `Health` показує загальну
      // «запит не вдався» БЕЗ жодного коду, хоча бекенд відповідає 200
      // (виявлено реальним переглядом сторінки під час аудиту).
      '/health': { target: 'http://localhost:5080', changeOrigin: true, secure: false },
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
