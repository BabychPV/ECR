import { appendFile, mkdir, readdir } from 'node:fs/promises';
import path from 'node:path';
import { expect, test, type Page, type TestInfo } from '@playwright/test';

/**
 * Прохід системою «як реальний користувач», від входу до адміністративних
 * екранів (`ЕТАП 7.5`, продовження `D-142`).
 *
 * ⛔ Цей файл НІЧОГО НЕ СТВЕРДЖУЄ про продукт і навмисно не падає на знахідці.
 * Він — **прилад**, а не гейт, і різниця тут принципова:
 *
 *   - гейт (`screenshots.spec.ts`, `keyboardPath.spec.ts`, `cellStates.spec.ts`)
 *     відповідає «так/ні» на одне заздалегідь відоме питання і мусить падати,
 *     інакше він нічого не доводить;
 *   - прилад проходить сценарій ДО КІНЦЯ і записує все, що побачив, бо
 *     зупинка на першій же знахідці приховала б усі наступні. Стенд коштує
 *     ~12 хвилин і одну тимчасову базу на 14 ГБ (`tools/e2e-stand.ps1`); прогін,
 *     що впав на третьому екрані з двадцяти, означає ще один такий цикл.
 *
 * ⚠ Тому кожен сценарій обгорнутий `record()`: виняток усередині стає записом
 * у звіті, а не падінням набору. Перетворити це на гейт означало б зробити
 * прохід беззмістовним — «зелено» тут не є доказом і ним не прикидається.
 *
 * ⛔ Знімки НЕ лягають у репозиторій: `fullPage` на `/admin/ui-strings` — це
 * 254 млн пікселів і 11.5 МБ одним файлом (заміряно, див. `screenshots.spec.ts`).
 * Каталог задається `ECR_WALK_SCREENS`; типовий — `artifacts/` (в `.gitignore`).
 *
 * ⚠ Знімки — саме ВІКНО, не `fullPage`. Питання проходу — «що бачить людина,
 * коли відкрила екран», і довга сторінка, склеєна в один файл, на нього не
 * відповідає (а на `/admin/ui-strings` ще й коштує 19 с і 11.5 МБ).
 *
 * Запуск:
 *   powershell -File tools/e2e-stand.ps1 -Server localhost -Database EcrWalk `
 *     -Port 5091 -Grep "WALK"
 */

/** Облікові записи стенда (`tools/e2e-stand.ps1`); існують лише в тимчасовій базі. */
const Operator = { user: 'e2e-operator', password: 'E2E-Operator-Work-2026!' };
const Admin = { user: 'e2e-admin', password: 'E2E-Admin-Work-2026!' };

/** Період і документ приходять зі стенда — зашите число ламалося б у січні. */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

const ScreenDirectory =
  process.env['ECR_WALK_SCREENS'] ?? path.resolve('../../artifacts/walkthrough');

/** Журнал спостережень — читається людиною після прогону, поруч зі знімками. */
const LogFile = path.join(ScreenDirectory, 'walkthrough.ndjson');

/**
 * Наскрізна нумерація знімків: файли мають читатися як історія, а не як набір.
 *
 * ⛔ Лічильник ВІДНОВЛЮЄТЬСЯ з каталогу, а не починається з нуля. Playwright
 * піднімає НОВИЙ процес-робітник після кожного падіння прогону, і модульний
 * стан у ньому свіжий: у першому проході це дало два файли `01-…`, два `02-…`
 * і так далі — історія читалася як дві різні історії. Диск переживає
 * перезапуск робітника, модуль — ні.
 */
let shotIndex = 0;

/**
 * Один запис журналу.
 *
 * ⚠ `unknown` замість `any` — лінт `e2e/**` забороняє `any` явно
 * (`eslint.config.js`), і тут це не формальність: у журнал лягає те, що
 * повернув браузер, тобто структура, якої TypeScript не бачив.
 */
interface LogEntry {
  readonly screen: string;
  readonly shot: string | null;
  readonly note: string;
  readonly data: unknown;
}

async function log(entry: LogEntry): Promise<void> {
  await appendFile(LogFile, `${JSON.stringify(entry)}\n`, 'utf8');
}

/**
 * Знімок вікна з наскрізним номером.
 *
 * ⚠ Перед знімком чекає на стабільний екран — це робить `settle()` на боці
 * виклику; тут лише запис файла, щоб номер видавався рівно один раз.
 */
async function shot(page: Page, name: string): Promise<string> {
  shotIndex += 1;
  const file = `${String(shotIndex).padStart(2, '0')}-${name}.png`;
  await page.screenshot({ path: path.join(ScreenDirectory, file) });

  return file;
}

/**
 * Чекає, доки екран перестане бути «в дорозі».
 *
 * ⛔ Не `networkidle`: на екранах із опитуванням фонових задач (`/admin/jobs`,
 * `ExportButton`) стан «мережа тиха» не настає ніколи, і знімок чекав би до
 * таймауту. Ознака готовності тут — заголовок сторінки плюс відсутність
 * видимого індикатора очікування.
 *
 * ⚠ Обидва очікування «м'які»: якщо заголовка немає — це САМО ПО СОБІ знахідка,
 * і зняти такий екран важливіше, ніж впасти на ньому.
 */
async function settle(page: Page, timeout = 30_000): Promise<void> {
  await page
    .getByRole('heading')
    .first()
    .waitFor({ state: 'visible', timeout })
    .catch(() => undefined);

  await page
    .locator('.mantine-Loader-root')
    .first()
    .waitFor({ state: 'hidden', timeout: 10_000 })
    .catch(() => undefined);

  // ⚠ Півсекунди після зникнення індикатора: Mantine анімує появу таблиць і
  // модалок, і знімок посеред переходу дав би «накладений текст» там, де його
  // немає — хибну знахідку, найдорожчий різновид (`CLAUDE.md`).
  await page.waitForTimeout(500);
}

/** Слухачі помилок сторінки; чіпляються один раз на сторінку. */
interface Watcher {
  readonly consoleErrors: string[];
  readonly httpErrors: string[];

  /**
   * Відповіді на `PATCH /api/v1/documents/{id}/cells` — НЕминущий слід
   * збереження комірки.
   *
   * ⛔ Усі екранні ознаки збереження минущі, і саме тому крок `cell-saved`
   * роками не мав чого перевіряти. Кнопка `Save` вимкнена, щойно `pending`
   * спорожніє (`DocumentGrid.tsx`: `disabled={pending.size === 0}`), а
   * `onPaste` кличе `save(edits)` НЕГАЙНО — тобто після успішної вставки
   * кнопка законно вимкнена. Індикатор `[data-save-status="saved"]` живе
   * близько двох секунд і встигає зникнути між кроками. Запит же в журналі
   * мережі лишається назавжди — і його наявність із кодом < 400 і є
   * відповіддю на питання «комірку справді записано?».
   */
  readonly cellPatches: Array<{ status: number; path: string }>;
}

function watch(page: Page): Watcher {
  const consoleErrors: string[] = [];
  const httpErrors: string[] = [];
  const cellPatches: Array<{ status: number; path: string }> = [];

  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text().slice(0, 300));
  });
  page.on('pageerror', (error) => {
    consoleErrors.push(`pageerror: ${error.message.slice(0, 300)}`);
  });
  page.on('response', (response) => {
    const pathname = new URL(response.url()).pathname;
    const method = response.request().method();

    if (method === 'PATCH' && pathname.endsWith('/cells')) {
      cellPatches.push({ status: response.status(), path: pathname });
    }
    if (response.status() >= 400) {
      httpErrors.push(`${String(response.status())} ${method} ${pathname}`);
    }
  });

  return { consoleErrors, httpErrors, cellPatches };
}

/**
 * Машинний огляд екрана.
 *
 * ⛔ Саме машинний, і це не зручність. `A7-33` (сторінка входу показувала
 * `login.title` замість назви системи) прожила до живого запуску тому, що
 * напис виправлявся від першого ж натискання клавіші: **очі не ловлять того,
 * що зникає від дотику** (`src/test/keyLikeText.ts`). Те саме стосується
 * кнопки без доступного імені й поля без мітки — на знімку вони виглядають
 * нормально.
 *
 * ⚠ Правила ключеподібного тексту скопійовані з `src/test/keyLikeText.ts`
 * навмисно, а не імпортовані: той модуль живе в середовищі vitest/jsdom, а цей
 * код виконується ВСЕРЕДИНІ браузера через `page.evaluate` — імпорт затягнув би
 * у сторінку модульну систему тестів.
 */
async function probe(page: Page): Promise<unknown> {
  return await page.evaluate(() => {
    const keyLike = /^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$/;
    const cyrillic = /[Ѐ-ӿ]/;

    const keys: string[] = [];
    const cyrillicText: string[] = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);

    for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      const text = (node.textContent ?? '').trim();
      const parent = node.parentElement;
      if (text.length === 0 || parent === null) continue;
      if (parent.closest('[data-allow-dotted]') !== null) continue;
      if (parent.closest('script, style') !== null) continue;

      if (text.includes('⟦') || keyLike.test(text)) {
        keys.push(`${text} << ${parent.outerHTML.slice(0, 160)}`);
      }
      if (cyrillic.test(text)) cyrillicText.push(text.slice(0, 120));
    }

    /** Чи має елемент хоч якесь доступне ім'я. */
    const named = (element: Element): boolean => {
      const aria = element.getAttribute('aria-label');
      if (aria !== null && aria.trim().length > 0) return true;
      if (element.getAttribute('aria-labelledby') !== null) return true;
      const title = element.getAttribute('title');
      if (title !== null && title.trim().length > 0) return true;

      return (element.textContent ?? '').trim().length > 0;
    };

    const namelessControls: string[] = [];
    for (const element of document.querySelectorAll('button, [role="button"], a[href]')) {
      const rect = element.getBoundingClientRect();
      if (rect.width === 0 || rect.height === 0) continue;
      if (named(element)) continue;
      if (element.querySelector('svg, img') !== null) {
        namelessControls.push(`іконка без підпису: ${element.outerHTML.slice(0, 160)}`);
      } else {
        namelessControls.push(`порожня кнопка: ${element.outerHTML.slice(0, 160)}`);
      }
    }

    const unlabelled: string[] = [];
    for (const element of document.querySelectorAll('input, select, textarea')) {
      if (element instanceof HTMLInputElement && element.type === 'hidden') continue;
      const rect = element.getBoundingClientRect();
      if (rect.width === 0 || rect.height === 0) continue;

      const id = element.getAttribute('id');
      const hasFor = id !== null && document.querySelector(`label[for="${CSS.escape(id)}"]`) !== null;
      const wrapped = element.closest('label') !== null;
      const placeholder = element.getAttribute('placeholder');

      if (named(element) || hasFor || wrapped) continue;
      if (placeholder !== null && placeholder.trim().length > 0) {
        unlabelled.push(`лише placeholder: ${element.outerHTML.slice(0, 160)}`);
      } else {
        unlabelled.push(`без мітки: ${element.outerHTML.slice(0, 160)}`);
      }
    }

    /**
     * Обрізаний текст: вміст ширший за контейнер, і контейнер його ховає БЕЗ
     * трикрапки. Трикрапка — свідомий прийом, обрізання без неї — дефект.
     *
     * ⛔ Поріг `clientWidth > 8` — не «магічне число», а лікування хибного
     * спрацювання, яке в першому проході дало ЧОТИРИ «знахідки» на КОЖНОМУ
     * екрані: `Skip to main content (1<137)` і сам заголовок `(1<86)`. Обидва —
     * елементи, навмисно сховані від ока й залишені читалці (клас
     * `sr-only`: ширина 1 px при вмісті в сотню). Такий елемент обрізаний
     * ЗАВЖДИ і за побудовою, і повідомляти про нього — це вчити читача звіту
     * гортати розділ знахідок не читаючи.
     */
    const clipped: string[] = [];
    for (const element of document.querySelectorAll('button, a, th, td, label, h1, h2, h3, span')) {
      if (element.children.length > 0) continue;
      const text = (element.textContent ?? '').trim();
      if (text.length === 0) continue;
      if (element.clientWidth <= 8) continue;

      const style = getComputedStyle(element);
      if (style.overflow === 'visible' && style.overflowX === 'visible') continue;
      if (style.textOverflow === 'ellipsis') continue;
      if (element.scrollWidth > element.clientWidth + 4) {
        clipped.push(`${text.slice(0, 80)} (${String(element.clientWidth)}<${String(element.scrollWidth)})`);
      }
    }

    const alerts: string[] = [];
    for (const element of document.querySelectorAll('[role="alert"]')) {
      alerts.push((element.textContent ?? '').trim().slice(0, 250));
    }

    const headings: string[] = [];
    for (const element of document.querySelectorAll('h1, h2, h3')) {
      headings.push((element.textContent ?? '').trim().slice(0, 120));
    }

    return {
      url: location.pathname + location.search,
      headings,
      keyLikeText: keys,
      namelessControls,
      unlabelledFields: unlabelled,
      clippedText: clipped,
      alerts,
      cyrillicOnEnglishUi: cyrillicText,
      horizontalOverflowPx:
        document.documentElement.scrollWidth - document.documentElement.clientWidth,
      spinnersVisible: document.querySelectorAll('.mantine-Loader-root').length,
      bodyCharacters: (document.body.textContent ?? '').trim().length,
    };
  });
}

/** Відкрити маршрут, дочекатися, зняти, оглянути, записати. */
async function visit(
  page: Page,
  watcher: Watcher,
  route: string,
  name: string,
  note = '',
): Promise<void> {
  watcher.consoleErrors.length = 0;
  watcher.httpErrors.length = 0;

  await page.goto(route).catch(() => undefined);
  await settle(page);

  const file = await shot(page, name);
  const seen = await probe(page);

  await log({
    screen: name,
    shot: file,
    note: note === '' ? `маршрут ${route}` : note,
    data: { route, seen, console: [...watcher.consoleErrors], http: [...watcher.httpErrors] },
  });
}

/**
 * Текст лівої верхньої комірки сітки.
 *
 * ⛔ Без цього рядка «скасування» перевірялося б тим, що КНОПКА `Undo` була
 * доступна, — тобто нічим. Undo або повертає в комірку попереднє число, або не
 * працює, і різниця видна лише в самій комірці.
 *
 * ⚠ `revo-grid` — веб-компонент, і його комірки живуть у shadow DOM: звичайний
 * `querySelector` по документу їх не бачить, тому спуск у `shadowRoot` тут
 * навмисний, а не перестраховка.
 */
async function firstCellText(page: Page): Promise<string> {
  return await page.evaluate(() => {
    const grid = document.querySelector('revo-grid');
    if (grid === null) return 'сітки на сторінці немає';

    const root: ParentNode = grid.shadowRoot ?? grid;
    const cell =
      root.querySelector('[data-rgrow="0"][data-rgcol="0"]') ?? root.querySelector('.rgCell');

    return cell === null ? 'комірку не знайдено' : (cell.textContent ?? '').trim();
  });
}

/** Вхід через справжню форму — той самий шлях, що й у людини. */
async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });

  await page.getByLabel(/User name|Ім'я/i).fill(user);
  // ⚠ `getByRole('textbox')`, а не `getByLabel`: `aria-label` кнопки-тумблера
  // видимості містить «password», і мітковий локатор резолвиться у два
  // елементи (`Q-260`; той самий рядок у трьох сусідніх spec-файлах).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');

  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
  await settle(page);
}

/** Тема ставиться ДО входу, у сховище: після входу її міняє меню користувача. */
async function useScheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  await page.goto('/login');
  await page.evaluate((value: string) => {
    localStorage.setItem('mantine-color-scheme-value', value);
  }, scheme);
}

/**
 * Виконує крок сценарію і НЕ дає йому завалити прохід.
 *
 * ⛔ Пояснення — у шапці файла: зупинка на першій знахідці приховала б усі
 * наступні, а другий прогін коштує стенда. Виняток стає записом.
 *
 * ⚠ `hard` — список кроків, чия невдача МУСИТЬ дійти до прогону, а не лише до
 * журналу. Прохід усе одно йде до кінця (запис у список, не `throw`), але
 * сценарій перевіряє список наприкінці й падає. Це не робить файл гейтом:
 * гейт відповідає «так/ні» про продукт, а тут перевіряється, що САМ КРОК
 * щось перевірив. Крок, який нічого не перевірив і записав «успішно», — не
 * спостереження, а неправда в журналі, і саме така неправда вивела
 * розслідування на десять хвилин і чотири кроки вбік.
 */
async function record(name: string, step: () => Promise<void>, hard?: string[]): Promise<void> {
  const started = Date.now();

  try {
    await step();
  } catch (failure) {
    const message = failure instanceof Error ? failure.message : String(failure);
    await log({ screen: name, shot: null, note: 'КРОК ОБІРВАВСЯ', data: message.slice(0, 600) });
    hard?.push(`${name}: ${message.slice(0, 300)}`);
  }

  // ⚠ Тривалість кожного кроку в журналі — не статистика. У першому проході
  // сценарій оператора вибрав увесь свій бюджет і обірвався перед `undo` та
  // `validate`, і з журналу НЕ БУЛО ВИДНО, який саме крок його з'їв: довелося
  // здогадуватися по коду. Один рядок тут відповідає на це прямо.
  await log({
    screen: name,
    shot: null,
    note: 'тривалість кроку',
    data: { seconds: Math.round((Date.now() - started) / 100) / 10 },
  });
}

test.describe('WALK: прохід системою від А до Я', () => {
  // ⚠ Той самий гейт, що й у сусідів: без стенда перевіряти нічого. Весь набір
  // і так падає в `globalSetup.ts` — це рядок для `ECR_E2E_OPTIONAL`.
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.',
  );

  test.beforeAll(async () => {
    await mkdir(ScreenDirectory, { recursive: true });

    // ⛔ Журнал НЕ обнуляється тут. У першому проході `writeFile(LogFile, '')`
    // стояв саме в цьому місці — і другий робітник (піднятий після падіння
    // прогону, див. `shotIndex`) стер записи двох уже пройдених сценаріїв.
    // Чистити журнал — справа того, хто запускає прогін, а не сценарію:
    // сценарій не знає, чи він перший.
    const existing = await readdir(ScreenDirectory).catch(() => [] as string[]);
    shotIndex = existing.filter((name) => name.endsWith('.png')).length;
  });

  test.afterEach(async ({}, testInfo: TestInfo) => {
    await log({
      screen: testInfo.title,
      shot: null,
      note: `сценарій завершено: ${testInfo.status ?? 'unknown'}`,
      data: { durationMs: testInfo.duration },
    });
  });

  /**
   * Повертає аркуш у `Draft` — базову лінію стенда.
   *
   * ⛔ Без цього хука ПОВТОРНИЙ прогін на тому самому стенді не повторює
   * перший, і це не теорія: WALK 5 доводить аркуш до `Approved` і на цьому
   * файл — останній за алфавітом — закінчується. Наступний повний прогін
   * починає `keyboardPath.spec.ts`, який на кроці 9 вимагає кнопку `Submit`
   * («у шапці немає кнопки подання») — а на затвердженому аркуші її немає.
   * Тобто набір був одноразовим: другий прогін падав би там, де перший
   * проходив, і причина лежала б за півгодини й за чотири файли назад.
   *
   * ⚠ Це не суперечить тому, що файл — ПРИЛАД, а не гейт (шапка файла).
   * Прибирання за собою — не твердження про продукт: воно нічого не доводить
   * і нічого не приховує, воно лише не ламає наступного. Тому хук і НЕ
   * загорнутий у `record()`: його відмова мусить бути видною, бо мовчазна
   * відмова прибирання — рівно той дефект, який цей хук і лікує.
   *
   * ⚠ Повернення в роботу — законна дія з правом `Document.Reopen`
   * (`ReopenDocumentHandler` → `Draft`, `ФВ-5.20a`), той самий діалог із
   * причиною, що й у людини, а не запит повз інтерфейс.
   */
  test.afterAll(async ({ browser }, testInfo) => {
    // ⚠ Той самий гейт, що й у `test.skip` вище: без стенда відновлювати
    // нічого, а `test.skip` на хуки не поширюється.
    if (PeriodKey === '' || DocumentId === '') return;

    // ⚠ `browser`, а не `page`: фікстура `page` — рівня прогону, і в `afterAll`
    // її вже немає. `baseURL` беремо з проєкту, щоб адреса стенда не
    // роздвоїлася з `playwright.config.ts`.
    const baseURL = testInfo.project.use.baseURL;
    if (baseURL === undefined) {
      throw new Error('у конфігурації немає baseURL — відновлювати базову лінію нема де');
    }

    const context = await browser.newContext({ baseURL });
    const page = await context.newPage();

    try {
      await signIn(page, Admin.user, Admin.password);
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      await settle(page);

      // ⚠ Стан читається з бейджа активної вкладки (`DocumentPage.tsx`,
      // `document.sheetStates`) — сирий рядок стану сервера, не переклад.
      const activeTab = page.getByRole('tab', { selected: true });
      await expect(activeTab, 'у документа немає жодного аркуша').toBeVisible({ timeout: 30_000 });

      // ⚠ Ідемпотентно: аркуш уже в `Draft` (наприклад, WALK 5 не дійшов до
      // затвердження) — повертати нема чого.
      const state = (await activeTab.textContent()) ?? '';
      if (!/Submitted|Approved/.test(state)) return;

      const reopen = page.getByRole('button', { name: /Return for edits|Повернути/i }).first();
      await expect(reopen, 'у шапці немає кнопки повернення в роботу').toBeVisible({
        timeout: 10_000,
      });
      await reopen.click();

      // ⚠ Причина обов'язкова в домені (`ECR-DOC-0422`), і кнопка підтвердження
      // вимкнена, доки поле порожнє (`ReasonModal.tsx`).
      const dialog = page.getByRole('dialog');
      await expect(dialog, 'діалог причини не відкрився').toBeVisible({ timeout: 10_000 });
      await dialog
        .getByRole('textbox', { name: /Reason|Причина/i })
        .fill('e2e: повернення базової лінії стенда');
      await dialog.getByRole('button', { name: /Return for edits|Повернути/i }).click();

      await expect(activeTab, 'аркуш не повернувся в Draft').toContainText('Draft', {
        timeout: 15_000,
      });
    } finally {
      await context.close();
    }
  });

  test('WALK 1 · вхід: форма, хибний пароль, зміна пароля, обидві теми', async ({ page }) => {
    test.setTimeout(180_000);
    const watcher = watch(page);

    await record('login-light', async () => {
      await useScheme(page, 'light');
      await page.goto('/login');
      await settle(page);

      const file = await shot(page, 'login-light');
      await log({ screen: 'login-light', shot: file, note: 'форма входу, світла тема', data: await probe(page) });
    });

    await record('login-wrong-password', async () => {
      await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
      await page.getByRole('textbox', { name: /Password|Пароль/i }).fill('definitely-wrong-2026');
      await page.keyboard.press('Enter');

      // ⚠ Чекаємо саме ПОВІДОМЛЕННЯ, а не час: `role="alert"` — те, що
      // `ErrorAlert` ставить на кожну помилку застосунку.
      await page.getByRole('alert').first().waitFor({ state: 'visible', timeout: 15_000 }).catch(() => undefined);
      await page.waitForTimeout(500);

      const file = await shot(page, 'login-wrong-password');
      await log({
        screen: 'login-wrong-password',
        shot: file,
        note: 'хибний пароль: що саме показано користувачеві',
        data: { seen: await probe(page), http: [...watcher.httpErrors] },
      });
    });

    await record('login-dark', async () => {
      await useScheme(page, 'dark');
      await page.goto('/login');
      await settle(page);

      const file = await shot(page, 'login-dark');
      await log({ screen: 'login-dark', shot: file, note: 'форма входу, темна тема', data: await probe(page) });
    });

    await record('change-password', async () => {
      await useScheme(page, 'light');
      await signIn(page, Operator.user, Operator.password);
      await visit(page, watcher, '/change-password', 'change-password', 'екран зміни пароля');
    });
  });

  test('WALK 2 · оператор: перелік, документ, введення, збереження, undo, перевірка', async ({
    page,
  }) => {
    // ⛔ 10 хвилин, а не 5. У першому проході цей сценарій вибрав рівно свої
    // 5:00 і обірвався ПЕРЕД `undo` та `validate` — тобто перед двома кроками,
    // заради яких він і написаний. Бюджет підняли разом із тим, що його з'їло
    // (явні `timeout` у розвідувальних викликах нижче): саме по собі число
    // нічого не лікує.
    test.setTimeout(600_000);
    const watcher = watch(page);

    /*
     * ⛔ Кроки, які мусять ДОВОДИТИ, а не лише спостерігати. Решта сценарію
     * лишається приладом; ці три відповідають на питання «чи введення даних
     * узагалі працює», і мовчазне «успішно» від них коштувало розслідування
     * на десять хвилин: `cell-saved` і `cell-undo` проходили на ВИМКНЕНИХ
     * кнопках, а справжня відмова спливала аж на `validate`.
     *
     * ⚠ Список перевіряється в кінці сценарію, а не на місці: прохід має
     * дійти до кінця й зняти всі екрани, інакше наступне розслідування знову
     * почнеться з «а що було далі — невідомо».
     */
    const hard: string[] = [];

    await record('operator-sign-in', async () => {
      await useScheme(page, 'light');
      await signIn(page, Operator.user, Operator.password);
    });

    await record('documents-empty', async () => {
      // ⚠ Порожній перелік береться НЕ вимкненим стендом, а періодом, якого в
      // календарі немає: перевіряється саме порожній стан продукту.
      await visit(page, watcher, '/?periodKey=190001', 'documents-empty', 'перелік за неіснуючий період');
    });

    await record('documents-list', async () => {
      await visit(page, watcher, `/?periodKey=${PeriodKey}`, 'documents-list', 'перелік документів за період стенда');
    });

    await record('document-open', async () => {
      const link = page.locator(`a[href^="/documents/${DocumentId}"]`).first();
      await expect(link, 'документа немає в переліку').toBeVisible({ timeout: 30_000 });
      await link.click();
      await page.waitForURL(`**/documents/${DocumentId}**`, { timeout: 30_000 });
      await settle(page);

      const file = await shot(page, 'document-open');
      await log({
        screen: 'document-open',
        shot: file,
        note: 'екран документа одразу після відкриття',
        data: { seen: await probe(page), http: [...watcher.httpErrors] },
      });
    });

    await record('document-grid', async () => {
      const grid = page.locator('revo-grid').first();
      await expect(grid, 'сітка не зʼявилася').toBeVisible({ timeout: 60_000 });
      await page.waitForTimeout(1_000);

      const file = await shot(page, 'document-grid');
      await log({
        screen: 'document-grid',
        shot: file,
        note: 'сітка документа',
        data: {
          seen: await probe(page),
          gridCount: await page.locator('revo-grid').count(),
          toolbar: await page.getByRole('button').allInnerTexts(),
        },
      });
    });

    await record('cell-input', async () => {
      // ⚠ Введення — ВСТАВКОЮ, а не набором у комірку: вставка з Excel — це
      // головний шлях уведення даних у системі (`ФВ-4.1`), і саме його
      // проходить людина. Набір по одній комірці перевіряв би рідкісний шлях.
      const grid = page.locator('revo-grid').first();
      const before = await firstCellText(page);

      await grid.focus();
      await page.keyboard.press('ArrowRight');
      await page.keyboard.press('ArrowLeft');

      await page.evaluate(async () => {
        await navigator.clipboard.writeText('4242');
      });
      await page.keyboard.press('Control+v');
      await page.waitForTimeout(1_500);

      const after = await firstCellText(page);

      /*
       * ⛔ Модалку відмови ТРЕБА ПОМІТИТИ І ЗГАСИТИ, інакше вона лишається
       * відкритою до кінця сценарію і псує КОЖЕН наступний крок: її оверлей
       * перехоплює вказівник, і `validate.click()` чекав 579.8 с саме тому.
       * Знімок і огляд робляться ще при відкритій модалці — вона і є головною
       * знахідкою кроку, — і лише потім `Escape`.
       *
       * ⚠ Гасимо саме ТУТ, а не в `record()` для всіх кроків підряд: WALK 3
       * навмисно переносить відкритий діалог попереднього перегляду імпорту з
       * кроку `import` у крок `import-apply`, і сліпий `Escape` між кроками
       * зламав би єдину перевірку застосування імпорту.
       */
      const rejectedDialog = await page.getByText('Some cells were not saved').count();

      const file = await shot(page, 'cell-input');
      await log({
        screen: 'cell-input',
        shot: file,
        note: `після вставки 4242: комірка була «${before}», стала «${after}»`,
        data: {
          seen: await probe(page),
          rejectedDialog,
          cellPatches: [...watcher.cellPatches],
          // ⚠ Явний короткий `timeout` у КОЖНОМУ розвідувальному виклику.
          // `innerText()` без нього чекає 30 с, `getAttribute()` — теж, і
          // `.catch(...)` цього не скорочує: він ловить відмову ПІСЛЯ
          // очікування. Саме такі «нешкідливі» рядки з'їли бюджет у першому
          // проході — питання «а чи є така кнопка» коштувало пів хвилини.
          saveButton: await page
            .getByRole('button', { name: /^Save/i })
            .first()
            .innerText({ timeout: 3_000 })
            .catch(() => 'кнопки Save немає'),
          http: [...watcher.httpErrors],
        },
      });

      if (rejectedDialog > 0) await page.keyboard.press('Escape');
    });

    await record('cell-saved', async () => {
      const save = page.getByRole('button', { name: /^Save/i }).first();
      const enabled = await save.isEnabled({ timeout: 3_000 }).catch(() => false);
      if (enabled) await save.click();

      // ⚠ Індикатор збереження — `[data-save-status]` (`DocumentGrid.tsx`), а не
      // тост: тост каже, що запит пішов, індикатор — що сервер відповів.
      await page
        .locator('[data-save-status]')
        .first()
        .waitFor({ state: 'visible', timeout: 8_000 })
        .catch(() => undefined);
      await page.waitForTimeout(1_000);

      const file = await shot(page, 'cell-saved');
      const accepted = watcher.cellPatches.filter((entry) => entry.status < 400);

      await log({
        screen: 'cell-saved',
        shot: file,
        note: 'після збереження',
        data: {
          seen: await probe(page),
          saveWasEnabled: enabled,
          cellPatches: [...watcher.cellPatches],
          status: await page
            .locator('[data-save-status]')
            .first()
            .getAttribute('data-save-status', { timeout: 3_000 })
            .catch(() => null),
          http: [...watcher.httpErrors],
        },
      });

      /*
       * ⛔ Цей крок роками звітував «після збереження» з `saveWasEnabled:
       * false` — тобто не натиснув нічого й нічого не перевірив. Журнал читав
       * це як норму, і справжня відмова («документ лише для читання, вставку
       * відхилено цілим пакетом») спливала аж на `validate`, за десять хвилин
       * і за два кроки далі.
       *
       * ⛔ Перевіряється НЕ стан кнопки. Вимкнена `Save` після успішної
       * вставки — це норма продукту, а не знахідка: `onPaste` кличе
       * `save(edits)` негайно, `pending` порожніє, і кнопка законно гасне
       * (`DocumentGrid.tsx`: `disabled={pending.size === 0}`). Індикатор
       * `[data-save-status]` теж не годиться — він живе ~2 с і встигає
       * зникнути. Єдиний неминущий доказ — сам запит: відхилена вставка НЕ
       * надсилає нічого (`onPaste` виходить на `plan.rejected.length > 0`).
       */
      expect(
        accepted.length,
        'крок «збережено» нічого не зберіг: жодного успішного PATCH …/cells за ' +
          'весь сценарій. Вимкнена кнопка Save сама по собі не є знахідкою ' +
          '(вставку зберігає onPaste, і pending порожніє), але відсутність ' +
          'ЗАПИТУ означає, що вставку відхилено ще на клієнті — дивись крок ' +
          'cell-input, поле rejectedDialog і модалку «Some cells were not saved».',
      ).toBeGreaterThan(0);
    }, hard);

    await record('cell-undo', async () => {
      const undo = page.getByRole('button', { name: /^Undo$/i }).first();
      const enabled = await undo.isEnabled({ timeout: 3_000 }).catch(() => false);
      if (enabled) await undo.click();
      await page.waitForTimeout(1_500);

      const file = await shot(page, 'cell-undo');
      await log({
        screen: 'cell-undo',
        shot: file,
        note: 'після скасування (Undo)',
        data: {
          seen: await probe(page),
          undoWasEnabled: enabled,
          redoEnabled: await page
            .getByRole('button', { name: /^Redo$/i })
            .first()
            .isEnabled({ timeout: 3_000 })
            .catch(() => false),
          firstCell: await firstCellText(page),
          http: [...watcher.httpErrors],
        },
      });

      /*
       * ⛔ Тут стан кнопки — ЗАКОННИЙ доказ, на відміну від `Save` вище, і
       * різниця не в стилі. `Undo` доступна рівно тоді, коли в історії є крок
       * (`disabled={!history.current.canUndo}`), а `DocumentGrid.onPaste`
       * робить `history.current.push(...)` на КОЖНІЙ прийнятій вставці — і
       * робить це незалежно від автозбереження. Тому вимкнена `Undo` в цьому
       * місці означає рівно одне: вставки не було, і скасовувати нічого.
       * Крок, який на це відповідав «після скасування (Undo)», брехав.
       */
      expect(
        enabled,
        'кнопка Undo вимкнена: історія порожня, отже прийнятої вставки не було — ' +
          'крок «скасування» не перевірив нічого. Undo не залежить від ' +
          'автозбереження (history.push у DocumentGrid.onPaste), тож причина ' +
          'попереду, у cell-input.',
      ).toBe(true);
    }, hard);

    await record('validate', async () => {
      const validate = page.getByRole('button', { name: /^Validate$/i }).first();
      const present = (await validate.count()) > 0;
      if (present) await validate.click();
      await page.waitForTimeout(4_000);

      const file = await shot(page, 'validate');
      await log({
        screen: 'validate',
        shot: file,
        note: 'після натискання «Validate»',
        data: { seen: await probe(page), buttonPresent: present, http: [...watcher.httpErrors] },
      });
    });

    await record('operator-document-bottom', async () => {
      // ⛔ Найважливіший знімок цього сценарію, і в першому проході його НЕ
      // БУЛО. Машинний огляд знайшов на цьому екрані `role="alert"` з текстом
      // про брак права `Calculation.View`, але у ВІКНІ його не видно: панель
      // розрахунків (`CalculationResultsPanel`) лежить під сіткою, нижче згину.
      // Знімок горішньої частини екрана довів би протилежне тому, що є
      // насправді, — тому прокручуємо до кінця сторінки й знімаємо те, до чого
      // оператор дійде прокруткою.
      await page.keyboard.press('End');
      await page.mouse.wheel(0, 40_000);
      await page.waitForTimeout(2_000);

      const file = await shot(page, 'operator-document-bottom');
      await log({
        screen: 'operator-document-bottom',
        shot: file,
        note: 'низ екрана документа під оператором',
        data: { seen: await probe(page), http: [...watcher.httpErrors] },
      });
    });

    /*
     * ⛔ Єдине місце, де цей сценарій падає, — і воно в кінці навмисно: усі
     * знімки зняті, увесь журнал записаний, і лише тоді прогін каже, що кроки
     * введення даних нічого не довели. Без цього рядка список `hard`
     * лишався б черговим записом у файлі, який читають після того, як
     * розслідування вже пішло не туди.
     */
    expect(
      hard,
      'кроки, які мусять доводити, а не лише спостерігати, нічого не довели',
    ).toEqual([]);
  });

  test('WALK 3 · оператор: експорт у .xlsx та імпорт', async ({ page }) => {
    test.setTimeout(240_000);
    const watcher = watch(page);

    await record('export-import-open', async () => {
      await useScheme(page, 'light');
      await signIn(page, Operator.user, Operator.password);
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      await settle(page);
    });

    await record('export', async () => {
      const exportButton = page.getByRole('button', { name: /Export/i }).first();
      const present = (await exportButton.count()) > 0;

      if (present) await exportButton.click();
      // ⚠ Експорт — ФОНОВА задача (`ExportButton.tsx`, опитування `/jobs/{id}`),
      // тож чекаємо не на зміну екрана, а на появу посилання «готово».
      await page.waitForTimeout(8_000);

      const file = await shot(page, 'export');
      await log({
        screen: 'export',
        shot: file,
        note: 'після натискання «Export to Excel»',
        data: {
          seen: await probe(page),
          buttonPresent: present,
          buttonText: await exportButton.innerText({ timeout: 3_000 }).catch(() => null),
          readyLink: await page
            .locator(`a[href^="/api/v1/documents/${DocumentId}/export/"]`)
            .count(),
          http: [...watcher.httpErrors],
        },
      });
    });

    await record('export-finished', async () => {
      // Другий погляд пізніше: чи довела задача експорт до кінця, чи так і
      // лишилася в «будується».
      await page.waitForTimeout(15_000);

      const file = await shot(page, 'export-finished');
      await log({
        screen: 'export-finished',
        shot: file,
        note: 'експорт через ~23 с після запуску',
        data: {
          seen: await probe(page),
          buttonText: await page.getByRole('button', { name: /Export/i }).first().innerText({ timeout: 3_000 }).catch(() => null),
          http: [...watcher.httpErrors],
        },
      });
    });

    await record('import', async () => {
      /*
       * ⛔ Натиснути «Import from Excel» і зняти екран — НЕ перевірка імпорту,
       * і перший прохід це довів: `dialogOpened: 0`, бо кнопка лише клацає
       * прихований `input[type=file]` (`ImportPanel.tsx`), а діалог операційної
       * системи Playwright не бачить і бачити не має. Крок «пройшов» і не
       * перевірив нічого.
       *
       * ⚠ Тому файл беремо ТОЙ, ЩО ЙОГО ЩОЙНО ВІДДАВ ЕКСПОРТ, і подаємо
       * `setInputFiles` — це замикає шлях «вивантажив → правив в Excel →
       * повернув», заради якого обидві кнопки й існують. Вигаданий .xlsx
       * перевіряв би розбір чужого файла, а не цей шлях.
       */
      // ⚠ Посилання шукається за АДРЕСОЮ, а не за роллю з підписом: `getByRole
      // ('link')` на цьому екрані першими віддає пункти навбару («Documents»,
      // «My groups»), і прохід завантажив би сторінку замість книги.
      const ready = page.locator(`a[href^="/api/v1/documents/${DocumentId}/export/"]`).first();
      const linkVisible = await ready.isVisible({ timeout: 5_000 }).catch(() => false);

      if (!linkVisible) {
        await log({
          screen: 'import',
          shot: null,
          note: 'експорт не дав посилання на файл — імпортувати нічого',
          data: { seen: await probe(page) },
        });

        return;
      }

      const [download] = await Promise.all([
        page.waitForEvent('download', { timeout: 30_000 }),
        ready.click(),
      ]);

      const saved = path.join(ScreenDirectory, 'exported.xlsx');
      await download.saveAs(saved);

      /*
       * ⛔ Книгу повертаємо в аркуш, який ТИМ ЧАСОМ ЗМІНИВСЯ, — інакше імпорт
       * не перевіряється до кінця. Другий прохід це показав: файл, вивантажений
       * і одразу повернений, дає «0 CHANGE(S) · The file matches the sheet:
       * there is nothing to apply», кнопка `Apply` законно вимкнена, і гілка
       * «застосувати» лишається непройденою — прохід уперся у ВЛАСНУ рівність,
       * а не в продукт.
       *
       * ⚠ Правимо комірку тим самим шляхом, що й людина (вставка в сітку), а не
       * запитом до API: інакше зміна прийшла б повз той самий екран, і розбіжність
       * між файлом і аркушем була б наслідком дії, якої користувач не робив.
       */
      const grid = page.locator('revo-grid').first();
      const beforeEdit = await firstCellText(page);
      await grid.focus({ timeout: 10_000 }).catch(() => undefined);
      await page.evaluate(async () => {
        await navigator.clipboard.writeText('777');
      });
      await page.keyboard.press('Control+v');
      await page.waitForTimeout(2_500);
      const afterEdit = await firstCellText(page);

      await page.locator('input[type="file"]').first().setInputFiles(saved);
      await page.getByRole('dialog').waitFor({ state: 'visible', timeout: 30_000 }).catch(() => undefined);
      await page.waitForTimeout(1_000);

      const file = await shot(page, 'import-preview');
      await log({
        screen: 'import-preview',
        shot: file,
        note: `попередній перегляд: книгу вивантажено, комірку змінено «${beforeEdit}» → «${afterEdit}», книгу повернено`,
        data: {
          seen: await probe(page),
          downloadedAs: download.suggestedFilename(),
          cellBeforeEdit: beforeEdit,
          cellAfterEdit: afterEdit,
          dialogOpened: await page.getByRole('dialog').count(),
          dialogText: await page
            .getByRole('dialog')
            .first()
            .innerText({ timeout: 3_000 })
            .catch(() => 'діалог не відкрився'),
          http: [...watcher.httpErrors],
        },
      });
    });

    await record('import-apply', async () => {
      const apply = page.getByRole('button', { name: /^Apply$/i }).first();
      const present = await apply.isVisible({ timeout: 3_000 }).catch(() => false);

      /*
       * ⛔ Стан кнопки перевіряється ДО натискання. У другому проході тут стояв
       * голий `apply.click()`, кнопка була законно вимкнена («нічого
       * застосовувати»), і Playwright чекав, доки вона ввімкнеться, — рівно
       * 240 секунд, тобто ввесь бюджет прогону. Сценарій упав на СВОЄМУ дефекті
       * й забрав із собою два наступні кроки; продукт при цьому поводився
       * правильно.
       */
      const enabled = present && (await apply.isEnabled({ timeout: 3_000 }).catch(() => false));
      if (enabled) await apply.click();
      await page.waitForTimeout(4_000);

      const file = await shot(page, 'import-applied');
      await log({
        screen: 'import-applied',
        shot: file,
        note: enabled ? 'після застосування імпорту' : 'кнопка «Apply» вимкнена — застосовувати нічого',
        data: {
          seen: await probe(page),
          applyPresent: present,
          applyEnabled: enabled,
          cellAfterApply: await firstCellText(page),
          dialogStillOpen: await page.getByRole('dialog').count(),
          http: [...watcher.httpErrors],
        },
      });
    });
  });

  test('WALK 4 · адміністратор: адміністративні екрани', async ({ page }) => {
    test.setTimeout(600_000);
    const watcher = watch(page);

    await record('admin-sign-in', async () => {
      await useScheme(page, 'light');
      await signIn(page, Admin.user, Admin.password);
    });

    const screens: ReadonlyArray<readonly [string, string]> = [
      ['/admin/templates', 'admin-templates'],
      ['/admin/registries', 'admin-registries'],
      ['/admin/methodologies', 'admin-methodologies'],
      ['/admin/expressions', 'admin-expressions'],
      ['/admin/units', 'admin-units'],
      ['/admin/security', 'admin-security'],
      ['/admin/periods', 'admin-periods'],
      ['/admin/sources', 'admin-sources'],
      ['/admin/mapping', 'admin-mapping'],
      ['/admin/jobs', 'admin-jobs'],
      ['/admin/snapshots', 'admin-snapshots'],
      ['/admin/audit', 'admin-audit'],
      ['/admin/consistency', 'admin-consistency'],
      ['/admin/health', 'admin-health'],
      ['/admin/ui-strings', 'admin-ui-strings'],
      ['/my-groups', 'my-groups'],
    ];

    for (const entry of screens) {
      const route = entry[0];
      const name = entry[1];
      await record(name, async () => {
        await visit(page, watcher, route, name);
      });
    }

    // Глибокий маршрут: шаблон → версія. Саме тут живуть аркуші, таблиці,
    // колонки, формули й правила валідації, і саме тут breadcrumbs мають
    // чотири рівні (`routes.ts`, `admin-template-version-relations`).
    await record('admin-template-version', async () => {
      await page.goto('/admin/templates');
      await settle(page);

      const link = page.locator('a[href^="/admin/templates/"]').first();
      if ((await link.count()) === 0) {
        await log({
          screen: 'admin-template-version',
          shot: null,
          note: 'у переліку шаблонів немає жодного посилання вглиб — далі йти нема куди',
          data: await probe(page),
        });

        return;
      }

      await link.click();
      await page.waitForTimeout(2_000);
      await settle(page);

      const file = await shot(page, 'admin-template-version');
      await log({
        screen: 'admin-template-version',
        shot: file,
        note: 'версія шаблону: аркуші, таблиці, колонки, формули, правила',
        data: { seen: await probe(page), url: page.url(), http: [...watcher.httpErrors] },
      });
    });
  });

  test('WALK 5 · робочий процес: подання, затвердження, документ лише для читання', async ({
    page,
  }) => {
    test.setTimeout(300_000);
    const watcher = watch(page);

    await record('workflow-open', async () => {
      await useScheme(page, 'light');
      await signIn(page, Admin.user, Admin.password);
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      await settle(page);
      await page.locator('revo-grid').first().waitFor({ state: 'visible', timeout: 60_000 }).catch(() => undefined);

      const file = await shot(page, 'workflow-draft');
      await log({
        screen: 'workflow-draft',
        shot: file,
        note: 'документ під адміністратором до подання',
        data: { seen: await probe(page), buttons: await page.getByRole('button').allInnerTexts() },
      });
    });

    await record('workflow-submit', async () => {
      const submit = page.getByRole('button', { name: /^Submit$/i }).first();
      const present = (await submit.count()) > 0;
      if (present) await submit.click();
      await page.waitForTimeout(4_000);

      const file = await shot(page, 'workflow-submitted');
      await log({
        screen: 'workflow-submitted',
        shot: file,
        note: 'після подання аркуша',
        data: {
          seen: await probe(page),
          buttonPresent: present,
          activeTab: await page.getByRole('tab', { selected: true }).innerText({ timeout: 5_000 }).catch(() => null),
          http: [...watcher.httpErrors],
        },
      });
    });

    await record('workflow-approve', async () => {
      const approve = page.getByRole('button', { name: /^Approve$/i }).first();
      const present = (await approve.count()) > 0;
      if (present) await approve.click();
      await page.waitForTimeout(4_000);

      const file = await shot(page, 'workflow-approved');
      await log({
        screen: 'workflow-approved',
        shot: file,
        note: 'після затвердження аркуша',
        data: {
          seen: await probe(page),
          buttonPresent: present,
          activeTab: await page.getByRole('tab', { selected: true }).innerText({ timeout: 5_000 }).catch(() => null),
          http: [...watcher.httpErrors],
        },
      });
    });

    await record('approved-read-only', async () => {
      // ⛔ Ключове питання стану `Approved`: чи ПОКАЗУЄ екран, що писати не
      // можна, — чи лише мовчки відхиляє спробу. Дивимось на склад панелі
      // інструментів сітки, а не на тост.
      await page.reload();
      await settle(page);
      await page.locator('revo-grid').first().waitFor({ state: 'visible', timeout: 60_000 }).catch(() => undefined);
      await page.waitForTimeout(1_000);

      const file = await shot(page, 'approved-read-only');
      await log({
        screen: 'approved-read-only',
        shot: file,
        note: 'затверджений документ: що лишилося доступним',
        data: {
          seen: await probe(page),
          buttons: await page.getByRole('button').allInnerTexts(),
          importPresent: await page.getByRole('button', { name: /Import/i }).count(),
          addRowPresent: await page.getByRole('button', { name: /Add row/i }).count(),
        },
      });
    });

    await record('approved-paste-rejected', async () => {
      // Спроба ввести значення у затверджений аркуш — тим самим шляхом, що й
      // раніше. Цікавить не відмова сервера, а те, що побачить людина.
      const grid = page.locator('revo-grid').first();
      await grid.focus({ timeout: 10_000 }).catch(() => undefined);
      await page.evaluate(async () => {
        await navigator.clipboard.writeText('9999');
      });
      await page.keyboard.press('Control+v');
      await page.waitForTimeout(3_000);

      const file = await shot(page, 'approved-paste-rejected');
      await log({
        screen: 'approved-paste-rejected',
        shot: file,
        note: 'вставка у затверджений аркуш: чи пояснено відмову',
        data: { seen: await probe(page), http: [...watcher.httpErrors] },
      });
    });
  });

  test('WALK 6 · межові стани: заборонена дія, неіснуючі адреси, довгий текст', async ({ page }) => {
    test.setTimeout(300_000);
    const watcher = watch(page);

    await record('forbidden-sign-in', async () => {
      await useScheme(page, 'light');
      await signIn(page, Operator.user, Operator.password);
    });

    await record('operator-navbar', async () => {
      await page.goto('/');
      await settle(page);

      const file = await shot(page, 'operator-navbar');
      await log({
        screen: 'operator-navbar',
        shot: file,
        note: 'що бачить оператор у навігації (порівняння з адміністратором)',
        data: {
          seen: await probe(page),
          navLinks: await page.locator('nav a, .mantine-AppShell-navbar a').allInnerTexts().catch(() => []),
        },
      });
    });

    // ⛔ Оператор на адміністративному маршруті: пункт меню туди не веде, але
    // адресу можна набрати руками або отримати посиланням від колеги. Порожній
    // екран без пояснення тут — знахідка, відмова з поясненням — норма.
    for (const entry of [
      ['/admin/security', 'forbidden-security'],
      ['/admin/health', 'forbidden-health'],
      ['/admin/ui-strings', 'forbidden-ui-strings'],
    ] as ReadonlyArray<readonly [string, string]>) {
      await record(entry[1], async () => {
        await visit(page, watcher, entry[0], entry[1], `оператор відкриває ${entry[0]} напряму`);
      });
    }

    await record('missing-route', async () => {
      await visit(page, watcher, '/no-such-page-2026', 'missing-route', 'неіснуюча адреса застосунку');
    });

    await record('missing-document', async () => {
      await visit(page, watcher, '/documents/999999999', 'missing-document', 'документ, якого немає');
    });

    await record('bad-period', async () => {
      await visit(
        page,
        watcher,
        `/documents/${DocumentId}?periodKey=190001`,
        'bad-period',
        'документ за період, якого немає в календарі',
      );
    });

    await record('long-text', async () => {
      // ⚠ `/admin/ui-strings` — найдовша сторінка застосунку (1314 рядків
      // однією таблицею без сторінок, заміряно в `screenshots.spec.ts`). Тут
      // цікавить не довжина сама по собі, а що видно у ВІКНІ: чи є пошук, чи є
      // сторінки, чи зрозуміло, скільки всього рядків.
      await signIn(page, Admin.user, Admin.password);
      await visit(page, watcher, '/admin/ui-strings', 'long-text-top', 'початок найдовшої сторінки');

      await page.mouse.wheel(0, 20_000);
      await page.waitForTimeout(1_500);

      const file = await shot(page, 'long-text-scrolled');
      await log({
        screen: 'long-text-scrolled',
        shot: file,
        note: 'та сама сторінка після прокрутки на 20 000 px',
        data: {
          seen: await probe(page),
          documentHeight: await page.evaluate(() => document.documentElement.scrollHeight),
          rows: await page.locator('tbody tr').count(),
        },
      });
    });

    await record('dark-key-screens', async () => {
      // Темна тема на ключових екранах: вхід уже знято в сценарії 1, тут —
      // перелік, документ і два адміністративні екрани.
      await useScheme(page, 'dark');
      await signIn(page, Admin.user, Admin.password);

      await visit(page, watcher, `/?periodKey=${PeriodKey}`, 'dark-documents', 'перелік документів, темна тема');
      await visit(
        page,
        watcher,
        `/documents/${DocumentId}?periodKey=${PeriodKey}`,
        'dark-document',
        'документ, темна тема',
      );
      await visit(page, watcher, '/admin/health', 'dark-health', 'стан системи, темна тема');
      await visit(page, watcher, '/admin/consistency', 'dark-consistency', 'знахідки узгодженості, темна тема');
      await visit(page, watcher, '/admin/jobs', 'dark-jobs', 'фонові задачі, темна тема');
    });

    await record('sign-out', async () => {
      await useScheme(page, 'light');
      await signIn(page, Admin.user, Admin.password);

      const menu = page.getByRole('button', { name: /e2e-admin/i }).first();
      const present = (await menu.count()) > 0;
      if (present) await menu.click();
      await page.waitForTimeout(1_000);

      const file = await shot(page, 'user-menu');
      await log({
        screen: 'user-menu',
        shot: file,
        note: 'меню користувача',
        data: {
          seen: await probe(page),
          menuPresent: present,
          items: await page.getByRole('menuitem').allInnerTexts().catch(() => []),
        },
      });
    });
  });
});
