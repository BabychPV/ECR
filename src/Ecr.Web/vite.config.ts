import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';
import { readFileSync } from 'node:fs';
import {
  UnusedMantineComponents,
  findPrunedClassesInUse,
  mantineClassesIn,
  pruneCss,
} from './src/app/mantineCssPrune';

/**
 * Відсікає з `@mantine/core/styles.css` стилі компонентів, яких у збірці
 * немає (`src/app/mantineCssPrune.ts` — чому і як; `D-132`).
 *
 * ⚠ Лише `build`: у `vite dev` і у vitest стилі лишаються повними — там
 * бюджету немає, а зайвий компонент без стилів у розробці був би пасткою.
 *
 * ⛔ Відсікання — у `transform`, ДО збирання, а не правкою готового CSS у
 * `generateBundle`: хеш імені файла рахується від вмісту, і правка після
 * хешування дала б той самий `index-<хеш>.css` із різним вмістом у різних
 * збірках — отруєний кеш у браузері.
 *
 * ⛔ Після збирання — перевірка, що жоден JS-чанк не вживає класу
 * відсіченого компонента. Інакше перший `<Slider>` у коді мовчки малювався б
 * без стилів; так — `npm run build` червоний і каже, який рядок прибрати.
 */
function pruneUnusedMantineCss(): Plugin {
  const stylesDir = path.resolve(__dirname, 'node_modules/@mantine/core/styles');
  const owner = new Map<string, string>();

  for (const component of UnusedMantineComponents) {
    const css = readFileSync(path.join(stylesDir, `${component}.css`), 'utf8');
    for (const cls of mantineClassesIn(css)) owner.set(cls, component);
  }

  const dead = new Set(owner.keys());
  let applied = false;

  return {
    name: 'ecr:prune-unused-mantine-css',
    apply: 'build',
    enforce: 'pre',
    transform(code, id) {
      if (!/[\\/]@mantine[\\/]core[\\/]styles\.css(\?.*)?$/.test(id)) return null;

      applied = true;
      return { code: pruneCss(code, dead), map: null };
    },
    generateBundle(_options, bundle) {
      if (!applied) {
        this.error('`@mantine/core/styles.css` не пройшов через відсікання — перевір шлях імпорту.');
      }

      const code = Object.values(bundle).flatMap((item) =>
        item.type === 'chunk' ? [item.code] : [],
      );
      const inUse = findPrunedClassesInUse(code, dead);
      if (inUse.size === 0) return;

      const components = [...new Set([...inUse].map((cls) => owner.get(cls)))].join(', ');
      this.error(
        `Ужито компонент(и) Mantine, чиї стилі відсічені: ${components}. ` +
          'Прибери їх з `UnusedMantineComponents` у `src/app/mantineCssPrune.ts`.',
      );
    },
  };
}

/*
 * ⛔ Директива паралельного аудиту (2026-09-11, Wave 0 / PR-0.2): кілька
 * ліній роботи піднімають dotnet+vite одночасно, і фіксовані 5173/5080
 * означали б, що друга лінія падає на "порт зайнятий" замість того, щоб
 * просто працювати поруч. Схема: лінія k → API `508k`, Vite `517(2+k)`
 * (лінія 1 → 5081/5173, лінія 2 → 5082/5174, ...). Порт 5080/5173
 * лишається дефолтом — це оркестраторова власна, непараметризована лінія.
 */
const vitePort = Number(process.env.ECR_VITE_PORT ?? 5173);
const apiUrl = process.env.ECR_API_URL ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react(), pruneUnusedMantineCss()],
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
    port: vitePort,
    proxy: {
      // Проксі на API, щоб cookie працювала без CORS у розробці
      '/api': { target: apiUrl, changeOrigin: true, secure: false },

      // ⛔ `/health/*` живе ПОЗА `/api/v1` навмисно (`HealthResponse.cs`):
      // інсталятор і зовнішній моніторинг читають його за стабільною,
      // не версійованою адресою. Без цього запису Vite віддає SPA-фолбек
      // (`index.html`) замість JSON — сторінка `Health` показує загальну
      // «запит не вдався» БЕЗ жодного коду, хоча бекенд відповідає 200
      // (виявлено реальним переглядом сторінки під час аудиту).
      '/health': { target: apiUrl, changeOrigin: true, secure: false },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],

    // ⚠ Межа тесту вища за межу очікувань `asyncUtilTimeout` (5 с, `src/test/setup.ts`):
    // інакше тест із одним повільним `findBy` упирався б у власну межу раніше.
    testTimeout: 15_000,

    /*
     * ⛔ `vmThreads` замість дефолтного `forks` — зміна ЗАМІРЯНА, не за
     * порадою з документації. Машина: Windows, Node 24.19.0, 156 файлів /
     * 793 тести. Три прогони підряд на кожному варіанті, час — стінний,
     * від запуску `vitest run` до виходу:
     *
     *   forks (було)  : 157 с, 136 с, 130 с — зелено
     *   vmThreads     :  28 с,  27 с,  32 с — зелено
     *
     * Тобто ~4.8×. Джерело виграшу видно у власному звіті vitest: на
     * `forks` «environment 27–31 %», і рядок `jsdom was created 156 times ·
     * 398–456 с сумарно». `vmThreads` не піднімає воркер на файл — він дає
     * файлу власний контекст `node:vm` у вже прогрітому потоці, тож і
     * створення jsdom, і розбір модулів коштують у рази менше
     * (`environment` падає до 11 %).
     *
     * ⛔ Ізоляція НЕ вимкнена, і це навмисно. `isolate: false` міряли
     * окремо, обидва варіанти:
     *   • `isolate: false` на чинному пулі — 825 с і 826 с (у 6 разів
     *     ПОВІЛЬНІШЕ), 50 і 54 впалих файли з 156, плюс 6 і 9
     *     неперехоплених помилок, і набір файлів щоразу інший. Тут уже є
     *     задокументована історія спільного стану (`src/test/setup.ts`),
     *     і вимкнення ізоляції її просто випускає назовні.
     *   • `vmThreads` + `isolate: false` — 25 с, зелено; але це в межах
     *     розкиду `vmThreads` із ізоляцією (27–32 с). Нуль виграшу за
     *     повністю спільний глобальний стан між 156 файлами — не той
     *     обмін, тому НЕ беремо.
     *
     * ⚠ Чого ця зміна НЕ обіцяє:
     *   • ⛔ Це НЕ прискорення CI на 4.8×. Замір зроблено на Windows/Node
     *     24; гейт `client` — ubuntu/Node 22, і там не перевірено нічого.
     *   • ⛔ Це НЕ стосується гейтів `a11y`: у них власний конфіг
     *     (`vitest.a11y.config.ts`), який ця зміна не торкає, а їхня ціна —
     *     `axe`, а не підняття середовища.
     *   • ⚠ `vmThreads` тримає контексти `node:vm`, і vitest прямо
     *     попереджає про витік пам'яті на великих наборах. На 156 файлах
     *     тут витоку не спостерігалося; якщо набір виросте і прогін почне
     *     падати з `heap limit`, лікується `poolOptions.vmThreads.memoryLimit`
     *     або поверненням на `forks` — не «додаванням пам'яті».
     *
     * ⚠ Чим перевірено, що зелене — справді зелене, а не тихо ослаблене:
     *   1. Мутаційна проба на `src/shared/i18n/localized.ts` (зламано так,
     *      щоб функція завжди повертала порожній рядок): `forks` — 7
     *      файлів / 9 тестів впало, `vmThreads` — РІВНО ті самі 7 / 9.
     *      Тобто новий пул ловить злам так само, а не пропускає його.
     *   2. Друга мутація, точкова: `useRouteTransitionFocus.ts` перестав
     *      питати `matchMedia` — під `vmThreads` файл почервонів (4 з 9).
     *   3. `--sequence.shuffle` під `vmThreads` виявив 2 залежності від
     *      порядку — ОБИДВІ відтворюються і на `forks` (`ExpressionEditor.
     *      staleSeedValue`, `ChangePasswordPage.pageHeader`), тобто вони
     *      давні й до пулу не мають стосунку. Новий пул не додав жодної.
     */
    pool: 'vmThreads',

    /*
     * ⛔ `process.hrtime` НІКОЛИ не підміняється фальшивим таймером.
     *
     * Vitest відміряє власний дедлайн тесту/хука (`TaskDeadline`) через
     * `performance.now()` з `node:perf_hooks`. У Node 18 цей
     * `performance.now()` реалізований поверх `process.hrtime()`
     * (`node:internal/perf/utils`), а `@sinonjs/fake-timers` підміняє
     * `process.hrtime` у складі набору за замовчуванням (з нього виключені
     * лише `nextTick` і `queueMicrotask`). Отже `vi.useFakeTimers()`
     * перехоплював годинник, яким сам раннер міряє, скільки триває тест.
     *
     * Наслідки були рівно два, і обидва читалися як «тест підвисає»:
     *   • `vi.advanceTimersByTime(5000)` просував і фальшивий годинник
     *     раннера — той бачив, що тест «витратив» 5000 мс, і валив його з
     *     `Test timed out in 5000ms`, хоча тест виконався за мілісекунди;
     *   • `vi.useRealTimers()` в `afterEach` повертав `hrtime` до справжнього
     *     часу роботи процесу, і дедлайн хука, заведений ще за фальшивого
     *     (майже нульового) відліку, миттєво вважався простроченим —
     *     `Hook timed out in 10000ms` за 1 секунду роботи файлу.
     *
     * ⚠ Другий наслідок, окрім власного падіння, з'їдав ще й прибирання:
     * коли `afterEach` падає, авто-`cleanup` від Testing Library не
     * відпрацьовує, DOM попереднього тесту лишається змонтованим, і
     * НАСТУПНИЙ тест бачить чужу розмітку. Саме звідси брався
     * `expected <a …>…</a> to be null` у `AppLayout.prefetch.test.tsx` —
     * не помилка прав доступу, а залишок попереднього тесту.
     *
     * ⚠ Що ПЕРЕВІРЕНО заміром, а що виведено. Перевірено на цій машині
     * (Node 18.17.1): `performance.now()` справді йде через
     * `process.hrtime` — стек показує
     * `Performance.now (node:internal/perf/utils:14:22)` прямо над
     * підміненим `hrtime`; і підміна саме `hrtime` — ЄДИНЕ, що ламало ці
     * файли (виключення `Date`, `performance`, `setImmediate`,
     * `requestAnimationFrame`, `requestIdleCallback` по одному не давало
     * нічого, виключення `hrtime` робило зеленим).
     *
     * ⛔ НЕ перевірено заміром: що саме робить Node 22 — його на цій машині
     * не встановлено. Факт лише той, що в CI (Node 22) ці файли зелені;
     * «бо там `performance.now()` уже не спирається на `hrtime`» —
     * найімовірніше пояснення, а не вимірювання. Для виправлення це
     * несуттєво: підміняти `hrtime` не потрібно на ЖОДНІЙ версії.
     *
     * ⚠ Головне: це НЕ «повільна машина». Тест валився від того, скільки
     * фальшивого часу він сам просував, а не від завантаження заліза —
     * підняття таймаутів не допомогло б тут ніколи.
     *
     * ⛔ Правильне місце — саме конфіг, а не окремі файли: жоден рядок коду
     * застосунку не читає `process.hrtime`, тож підміна цього таймера не
     * може дати тестам нічого, крім перехопленого годинника раннера. Вимога
     * «не піднімати таймаути» виконана буквально: жодне число таймауту тут
     * не змінене — прибрано причину, через яку раннер хибно міряв час.
     *
     * ⚠ `queueMicrotask` і `nextTick` перелічені тут НЕ про запас. Vitest
     * виключає їх сам — але РІВНО доти, доки `toFake`/`toNotFake` не задані
     * взагалі; щойно з'являється власний список, дефолт зникає цілком, і
     * `@sinonjs/fake-timers` починає підміняти ще й їх. Підмінений
     * `queueMicrotask` ламає React 19 (його планувальник ставить туди
     * продовження роботи, і `advanceTimersByTimeAsync` крутить їх без
     * кінця): `AppLayout.prefetch.test.tsx` у такому стані не падав, а з'їдав
     * пам'ять до `heap limit Allocation failed` за ~110 с. Тобто список має
     * бути РОЗШИРЕННЯМ дефолту vitest, а не заміною.
     */
    fakeTimers: { toNotFake: ['hrtime', 'queueMicrotask', 'nextTick'] },

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
