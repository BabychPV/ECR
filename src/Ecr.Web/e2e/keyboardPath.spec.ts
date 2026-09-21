import { expect, test, type Page, type Response } from '@playwright/test';
import { expectFocusRing, expectFocusTrapped, expectFocusVisible, focusState } from './focus';

/**
 * Прохід оператора **без миші** від входу до виходу (`ФВ-14.16`, `D-141`).
 *
 * ⛔ У цьому файлі НЕ МОЖНА клікати. Заборона тримається лінтом
 * (`eslint.config.js`, селектори на `.click()`, `page.mouse.*`, `hover`), і
 * вона не формальність: один клік посеред проходу робить зеленим сценарій, у
 * якому клавіатурою пройти неможливо — а це рівно те, що перевірка мала б
 * спіймати.
 *
 * ⛔ Перевіряється не «сторінка відкрилася», а що ПІСЛЯ КОЖНОЇ ЗМІНИ фокуса
 * людина знає, де вона: фокус не на `body`, елемент у полі зору, а на
 * ключових зупинках — кільце фокуса видно пікселями (`focus.ts`).
 *
 * ⚠ Стенд готує `tools/e2e-stand.ps1`: чиста база, розгортання, два
 * іменовані користувачі з уже зміненими разовими паролями. Без цього кожен
 * прогін починався б із примусової зміни пароля і перевіряв би саме її.
 *
 * ⚠ Паролі тут ТЕСТОВІ й існують лише в тимчасовій базі, яку стенд же й
 * видаляє. У продуктивній системі жодного з цих записів немає.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Admin-Work-2026!' };

/** Період і документ приходять зі стенда: зашите число ламалося б у січні. */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Прохід оператора без миші (ФВ-14.16)', () => {
  // ⚠ Гейт більше не вирішує долю набору: без стенда падає весь набір
  // (`e2e/globalSetup.ts`). Він працює лише під `ECR_E2E_OPTIONAL`, коли
  // пропуск оголошений свідомо, — і змінна названа в тексті навмисно, щоб
  // рядок «немає стенда» не читався знову як норма.
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('від входу до виходу самою лише клавіатурою', async ({ page }) => {
    // ⚠ Явний бюджет замість `test.slow()` (30 с × 3 = 90 с). На спокійній
    // машині прохід іде 84 с — упритул до стелі; на завантаженій (десятки
    // процесів `dotnet` паралельних сесій) один лише `GET …/tables/730` ішов
    // 14.9 с, а `POST …/submit` не відповідав і за 30 с. Нижче кожен запис на
    // сервер чекається ЗА ВІДПОВІДДЮ (`waitForResponse`), а не за годинником,
    // тож бюджет прогону мусить ці відповіді вміщати.
    test.setTimeout(360_000);

    // ── 1. Вхід ───────────────────────────────────────────────────────────
    await page.goto('/login');

    // ⚠ Чекаємо ЗАГОЛОВОК, а не сам факт переходу. Доки публічний каталог
    // рядків не розв'язано, сторінка показує спінер і не має жодної зупинки
    // для Tab (`D-138`) — фокус лишався б на `body`, і падіння виглядало б як
    // дефект доступності там, де сторінка просто ще не відрендерилася.
    await expect(
      page.getByRole('heading').first(),
      'сторінка входу не відрендерилася',
    ).toBeVisible({ timeout: 30_000 });

    // ⛔ Сторінка входу — єдина, яку бачить КОЖЕН користувач системи. Якщо
    // прохід ламається тут, далі не має сенсу нічого.
    await expectFocusRing(page, page.getByLabel(/User name|Ім'я/i), 'поле імені');
    await page.keyboard.type(Operator.user);

    await page.keyboard.press('Tab');
    await expectFocusVisible(page, 'перехід до пароля табом');
    await page.keyboard.type(Operator.password);

    // ⛔ Enter у полі, а не пошук кнопки. Форма, яку не можна надіслати
    // Enter'ом, змушує людину без миші шукати кнопку табом щоразу — і саме
    // так було до `A7-49`: поля лежали в `<Stack>`, а не у `<form>`, і Enter
    // не робив НІЧОГО на першому ж екрані системи.
    await page.keyboard.press('Enter');

    await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });

    // ── 2. Після переходу фокус не загубився ─────────────────────────────
    // ⛔ Заголовок — САМЕ в `<main>`, а не перший-ліпший. `waitForURL` вище
    // проходить, щойно змінився шлях, а вміст маршруту ще вантажиться: `<main>`
    // порожній, і `getByRole('heading').first()` знаходив заголовок не нового
    // екрана. Фокус у цю мить законно на `body` — `PageHeader.tsx` переносить
    // його на заголовок при МОНТУВАННІ сторінки. Траса падіння: фокус читався
    // через 20 мс після зміни URL, `<main>` у знімку порожній.
    await expect(
      page.getByRole('main').getByRole('heading').first(),
      'сторінка після входу не відрендерилася',
    ).toBeVisible({ timeout: 30_000 });

    // ⚠ `expect.poll`, а не одне читання: фокус переносить `useEffect` після
    // коміту рендера, тобто щонайменше на кадр пізніше за появу заголовка.
    // Дефект — фокус, що НЕ приходить узагалі, — і далі червоний.
    await expect
      .poll(async () => (await focusState(page)).tag, {
        message: 'після входу фокус загубився на body',
        timeout: 10_000,
      })
      .not.toBe('body');

    // ── 3. Період ────────────────────────────────────────────────────────
    await expectFocusRing(page, page.getByLabel(/Period|Період/i).first(), 'поле періоду');
    await page.keyboard.press('Control+a');
    await page.keyboard.type(PeriodKey);

    // ── 4. Документ ──────────────────────────────────────────────────────
    // Посилання на документ — звичайний `<a>`, і Enter на ньому має
    // спрацювати так само, як клік.
    const link = page.locator(`a[href^="/documents/${DocumentId}"]`).first();
    await expect(link, 'документа немає в переліку за цей період').toBeVisible({ timeout: 30_000 });

    await expectFocusRing(page, link, 'посилання на документ');
    await page.keyboard.press('Enter');

    await page.waitForURL(`**/documents/${DocumentId}**`, { timeout: 30_000 });

    // ── 5. Таблиця ───────────────────────────────────────────────────────
    const grid = page.locator('revo-grid').first();
    await expect(grid, "сітка не з'явилася — далі йти нема куди").toBeVisible({ timeout: 30_000 });

    // ⛔ Видима сітка — ще не сітка з даними. Елемент `revo-grid` є одразу, а
    // рядки з'являються після `GET …/tables/{id}`, який під навантаженням ішов
    // 14.9 с. Вставка в сітку без рядків не породжувала жодного `PATCH`, і
    // прохід мовчки йшов подавати незмінений аркуш.
    await expect(
      grid.locator('revogr-data .rgCell').first(),
      'рядки сітки не завантажилися',
    ).toBeVisible({ timeout: 60_000 });

    // ⚠ Grid — веб-компонент, і його внутрішній фокус живе у shadow DOM.
    // Тому сюди заходимо фокусом на контейнер, а далі стрілками, як людина.
    await grid.focus();
    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('ArrowDown');

    // ⚠ І назад тими самими стрілками. Це не «скасування» попередніх двох:
    // після аудиту §10.1 якір вставки — ВИДІЛЕНА комірка, а не жорсткий кут
    // таблиці, тож без цього прохід вставляв би в комірку, редагованість якої
    // залежить від складу шаблону, і сам собі створював би плаваючу відмову.
    // Навігація лишається перевіреною (чотири натискання, обидві осі), а якір
    // — тим самим, що й до фіксу.
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('ArrowUp');

    // ── 6. Вставка з Excel ───────────────────────────────────────────────
    // ⛔ Це головний шлях введення даних у системі (`ФВ-4.1`), і він мусить
    // працювати з клавіатури цілком: Ctrl+V без миші.
    await page.evaluate(async () => {
      await navigator.clipboard.writeText('101\t102\n103\t104');
    });

    // ⚠ Слухач ставиться ДО натискання: відповідь може прийти раніше, ніж
    // рядок після `press` почне її чекати.
    const saved = waitForWrite(page, 'PATCH', 'cells');
    await page.keyboard.press('Control+v');

    // ── 7. Збереження ────────────────────────────────────────────────────
    // Ctrl+S — той самий рефлекс, що в Excel; без нього оператор шукає кнопку.
    await page.keyboard.press('Control+s');

    // ⛔ Подання — ПІСЛЯ підтвердженого збереження. Раніше `Submit` натискався,
    // поки `PATCH` ще летів (траса: `PATCH` ішов 4.4 с, `submit` відправлено
    // на 0.7 с раніше за його відповідь) — два записи того самого аркуша
    // навперегін.
    expect((await saved).status(), 'сервер не прийняв вставку').toBe(200);

    // ── 8. Дії робочого процесу ──────────────────────────────────────────
    // ⛔ До аудиту (`A7-39`) тут не було нічого, крім «Подати»: затвердити
    // документ через інтерфейс було неможливо.
    const validate = page.getByRole('button', { name: /Validate|Перевір/i }).first();
    if ((await validate.count()) > 0) {
      await expectFocusRing(page, validate, 'кнопка перевірки');
      await page.keyboard.press('Enter');
    }

    // ── 9. Подання ───────────────────────────────────────────────────────
    // ⛔ Q-259 (аудит): до цього рядка прохід перевіряв «вставити, зберегти,
    // за наявності — перевірити» і одразу переходив до виходу — жодна з двох
    // кнопок робочого процесу (`SheetActions.tsx`), заради яких компонент і
    // зʼявився (`A7-39`), не отримувала жодного натискання з клавіатури.
    // Тести API й Application (`Ecr.Scenarios.Tests`,
    // `Ecr.Application.Tests/Workflow`) доводять, що ендпоінти
    // `submit`/`approve` працюють; жоден тест не доводив, що до них веде
    // КНОПКА в браузері й що екран показує результат.
    const submit = page.getByRole('button', { name: /Submit|Подати/i }).first();
    await expect(submit, 'у шапці немає кнопки подання').toBeVisible({ timeout: 10_000 });

    await expectFocusRing(page, submit, 'кнопка подання');
    const submitted = waitForWrite(page, 'POST', 'submit');
    await page.keyboard.press('Enter');

    // ⛔ Спершу ВІДПОВІДЬ сервера, потім бейдж. Подання синхронне
    // (`SubmitSheetHandler`: свіжа валідація аркуша й зріз УСІХ його таблиць
    // в одній транзакції) — фонової задачі воно не чекає, але під
    // навантаженням іде десятки секунд. Бейдж за 15 с від натискання падав
    // «лишився Draft» при запиті, що ще летів; закритий контекст обривав його,
    // транзакція відкочувалась, і наступний прогін бачив той самий Draft —
    // тобто траса виглядала як «сервер проковтнув подання».
    expect((await submitted).status(), 'сервер не прийняв подання').toBe(204);

    // ⚠ Стан читається з бейджа активної вкладки (`DocumentPage.tsx`,
    // `document.sheetStates`), а не з тосту: тост каже, що запит пройшов,
    // бейдж — що інтерфейс показує РЕЗУЛЬТАТ. Значення в бейджі — сирий
    // рядок стану сервера (`SheetState` з `transitions.ts`), не переклад,
    // тому очікуємо саме `Submitted`, а не рядок каталогу.
    const activeTab = page.getByRole('tab', { selected: true });
    await expect(activeTab, 'аркуш не перейшов у Submitted').toContainText('Submitted', {
      timeout: 15_000,
    });

    // ── 10. Затвердження ─────────────────────────────────────────────────
    // ⛔ Другий обліковий запис із правом затвердження тут НЕ ЗНАДОБИВСЯ:
    // `tools/e2e-stand.ps1` видає гранти НА ПРОЄКТ (`GrantLevel`,
    // `EditRules.Effective`), і `e2e-admin` (єдиний обліковий запис цього
    // проходу) уже має `Manage` — а `Manage` (5) ⩾ `Approve` (4), тож той
    // самий грант, який щойно дозволив подання (`Submit`, 3), дозволяє й
    // затвердження. Заводити другий вхід означало б перевіряти сценарій, який
    // стенд не видає. Якби `e2e-admin` мав лише `Write`/`Submit`, довелося б
    // або додати роль у `e2e-stand.ps1` (поза цією карткою), або зупинитися
    // й повідомити — жодне з двох тут не знадобилося.
    const approve = page.getByRole('button', { name: /Approve|Затвердити/i }).first();
    await expect(approve, 'у шапці немає кнопки затвердження').toBeVisible({ timeout: 10_000 });

    await expectFocusRing(page, approve, 'кнопка затвердження');
    const approved = waitForWrite(page, 'POST', 'approve');
    await page.keyboard.press('Enter');
    expect((await approved).status(), 'сервер не прийняв затвердження').toBe(204);

    await expect(activeTab, 'аркуш не перейшов у Approved').toContainText('Approved', {
      timeout: 15_000,
    });

    // ── 11. Вихід ────────────────────────────────────────────────────────
    // ⛔ Вихід — теж частина проходу. До аудиту (`A7-35`) елемента виходу не
    // існувало взагалі: увійти було можна, вийти — ні.
    const menu = page.getByRole('button', { name: /e2e-admin/i }).first();
    await expect(menu, 'у шапці немає меню користувача').toBeVisible({ timeout: 30_000 });

    await expectFocusRing(page, menu, 'меню користувача');
    await page.keyboard.press('Enter');

    const logout = page.getByRole('menuitem', { name: /Sign out|Вийти/i }).first();
    await expect(logout, 'у меню немає виходу').toBeVisible({ timeout: 10_000 });

    await logout.focus();
    await expectFocusVisible(page, 'вихід');
    await page.keyboard.press('Enter');

    await page.waitForURL('**/login**', { timeout: 30_000 });
  });

  test('модальний діалог тримає фокус і Escape повертає його', async ({ page }) => {
    await page.goto('/login');
    await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });

    await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
    // ⛔ Q-260 додав `visibilityToggleButtonProps={{ 'aria-label': 'Toggle
    // password visibility' }}` до `PasswordInput` — доступна кнопка, але її
    // aria-label МІСТИТЬ підрядок «password», тож `getByLabel(/Password|
    // Пароль/i)` тепер збігається і з полем, і з кнопкою (strict-mode
    // violation). Роль `textbox` є лише в полі — кнопка лишається `button`.
    await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
    await page.keyboard.press('Enter');
    await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });

    await page.goto('/admin/periods');

    const create = page.getByRole('button', { name: /New project|Новий проєкт/i }).first();
    await expect(create, 'немає кнопки створення проєкту').toBeVisible({ timeout: 30_000 });

    await create.focus();
    const opener = await focusState(page);
    await page.keyboard.press('Enter');

    await expect(page.getByRole('dialog')).toBeVisible({ timeout: 10_000 });
    await expectFocusTrapped(page, '[role="dialog"]');

    // ⛔ Escape закриває діалог І ПОВЕРТАЄ фокус тому, хто його відкрив.
    // Інакше людина без миші опиняється на початку сторінки і йде табом
    // заново — щоразу, коли передумала.
    //
    // ⚠ Де саме стоїть фокус після 12 Tab, залежить від ЧАСУ: поля назви
    // (`LocalizedInput`, по одному на мову) з'являються лише після
    // `GET /api/v1/languages`, і в трасі падіння ця відповідь прийшла посеред
    // обходу — між 2-м і 3-м Tab. Кількість зупинок змінилася з 7 на 10, і
    // 12-й Tab лягав на поле поясу — `Select searchable`, який на фокусі сам
    // розкриває список. Escape у розкритому комбобоксі закриває СПИСОК, а не
    // діалог (шар за шаром, WAI-ARIA combobox; Mantine позначає таке поле
    // `data-mantine-stop-propagation`) — це правильна поведінка, а не дефект.
    // Тому шар списку знімається окремим Escape і перевіряється окремо: діалог
    // мусить лишитися відкритим, а список — закритися.
    //
    // ⚠ Ознака розкритого списку — `aria-controls` на сфокусованому полі, що
    // вказує на наявний `listbox`. `aria-expanded` Mantine на `Select` НЕ
    // ставить (`withExpandedAttribute: false` у `Combobox.Target`), а
    // `aria-controls` — лише поки список відкритий.
    const openListbox = async (): Promise<boolean> =>
      page.evaluate(() => {
        const id = document.activeElement?.getAttribute('aria-controls');
        return id !== null && id !== undefined && document.getElementById(id) !== null;
      });

    if (await openListbox()) {
      await page.keyboard.press('Escape');
      await expect
        .poll(openListbox, { message: 'Escape не закрив розкритий список у діалозі', timeout: 5_000 })
        .toBe(false);
      await expect(
        page.getByRole('dialog'),
        'Escape у розкритому списку закрив увесь діалог, а не лише список',
      ).toBeVisible();
    }

    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toBeHidden({ timeout: 10_000 });

    const returned = await focusState(page);
    expect(returned.tag, 'після Escape фокус на body').not.toBe('body');
    expect(
      returned.label,
      'після Escape фокус не повернувся на кнопку, яка відкрила діалог',
    ).toBe(opener.label);
  });

  /**
   * Повертає аркуш у `Draft` — базову лінію стенда.
   *
   * ⛔ Без цього хука файл одноразовий, і це доведено, а не припущено: окремий
   * стенд, `-Grep "клавіатурою"`, два прогони поспіль на ОДНІЙ базі, без
   * жодного іншого spec-файла — перший зелений, другий червоний. Кроки 9 і 10
   * доводять аркуш до `Approved`, а на затвердженому аркуші немає кнопки
   * подання, тож наступний прогін падає на кроці 9 («у шапці немає кнопки
   * подання») — за десять кроків від причини.
   *
   * ⚠ Досі за цим файлом прибирав СУСІД: `security.spec.ts` повертає аркуш у
   * роботу в `makeSheetEditable`, бо інакше не може перевірити власне
   * твердження. Це працює, доки ніхто не змінить порядок і не запустить
   * підмножину набору через `--grep`, — тобто рівно доти, доки про залежність
   * пам'ятають. Файл, який прибирає за собою сам, такої пам'яті не потребує.
   *
   * ⛔ Клавіатурою, як і весь файл (`ФВ-14.16`, лінт на `.click()` у
   * `eslint.config.js`). Прибирання — не привід заводити в цьому файлі мишу:
   * саме так заборони й розмиваються.
   *
   * ⚠ Повернення в роботу — законна дія з правом `Document.Reopen`
   * (`ReopenDocumentHandler` → `Draft`, `ФВ-5.20a`); `e2e-admin` його має
   * (`tools/e2e-stand.ps1`, роль `E2EAdmin`).
   */
  test.afterAll(async ({ browser }, testInfo) => {
    // ⚠ Той самий гейт, що й у `test.skip` вище: без стенда відновлювати
    // нічого, а `test.skip` на хуки не поширюється.
    if (PeriodKey === '' || DocumentId === '') return;

    // ⚠ Хук має власний бюджет (умовчання — 30 с), а вхід, документ і
    // повернення в роботу під навантаженням у нього не вміщаються.
    testInfo.setTimeout(180_000);

    const baseURL = testInfo.project.use.baseURL;
    if (baseURL === undefined) {
      throw new Error('у конфігурації немає baseURL — відновлювати базову лінію нема де');
    }

    const context = await browser.newContext({ baseURL });
    const page = await context.newPage();

    try {
      await page.goto('/login');
      await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
      await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
      await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
      await page.keyboard.press('Enter');
      await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });

      // ⚠ Стан аркуша приходить із `GET /documents/{id}`, а вкладки — з
      // `GET …/tables`; вкладка без стану показує `Draft` за умовчанням. Під
      // навантаженням перший ішов 1.8 с проти 1.5 с другого, тож стан без
      // цього очікування читався до відповіді — і хук тихо лишав аркуш
      // `Approved` наступному прогону.
      const documentLoaded = page.waitForResponse(
        (response) =>
          response.request().method() === 'GET' &&
          new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}`,
        { timeout: 90_000 },
      );
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
      await documentLoaded;

      // ⚠ Стан читається з бейджа активної вкладки — сирий рядок стану
      // сервера (`SheetState` з `transitions.ts`), не переклад.
      const activeTab = page.getByRole('tab', { selected: true });
      await expect(activeTab, 'у документа немає жодного аркуша').toBeVisible({ timeout: 30_000 });

      // ⚠ Ідемпотентно: прогін міг не дійти до затвердження — тоді повертати
      // нема чого.
      const state = (await activeTab.textContent()) ?? '';
      if (!/Submitted|Approved/.test(state)) return;

      const reopen = page.getByRole('button', { name: /Return for edits|Повернути/i }).first();
      await expect(reopen, 'у шапці немає кнопки повернення в роботу').toBeVisible({
        timeout: 10_000,
      });
      await reopen.focus();
      await page.keyboard.press('Enter');

      // ⚠ Причина обов'язкова в домені (`ECR-DOC-0422`), і кнопка
      // підтвердження вимкнена, доки поле порожнє (`ReasonModal.tsx`).
      const dialog = page.getByRole('dialog');
      await expect(dialog, 'діалог причини не відкрився').toBeVisible({ timeout: 10_000 });
      await dialog
        .getByRole('textbox', { name: /Reason|Причина/i })
        .fill('e2e: повернення базової лінії стенда');

      const confirm = dialog.getByRole('button', { name: /Return for edits|Повернути/i });
      await confirm.focus();
      const reopened = waitForWrite(page, 'POST', 'reopen');
      await page.keyboard.press('Enter');
      expect((await reopened).status(), 'сервер не прийняв повернення в роботу').toBe(204);

      await expect(activeTab, 'аркуш не повернувся в Draft').toContainText('Draft', {
        timeout: 15_000,
      });
    } finally {
      await context.close();
    }
  });
});

/**
 * Відповідь на запис документа (`PATCH …/cells`, `POST …/submit` тощо).
 *
 * ⚠ Викликати ДО дії, що породжує запит: інакше відповідь може прийти
 * раніше, ніж її почнуть чекати.
 *
 * ⚠ 90 с — не «з запасом на всяк випадок». Це стеля для ОДНОГО запису на
 * завантаженій машині: у трасах `GET …/tables/730` ішов 14.9 с, а подання не
 * відповіло й за 30 с. Відповідь, що не прийшла за 90 с, — уже не повільність,
 * а зависання, і падіння назве сам запит, а не бейдж за три кроки від нього.
 */
function waitForWrite(page: Page, method: string, action: string): Promise<Response> {
  const path = `/api/v1/documents/${DocumentId}/${action}`;

  return page.waitForResponse(
    (response) => response.request().method() === method && new URL(response.url()).pathname === path,
    { timeout: 90_000 },
  );
}
