import { expect, test } from '@playwright/test';
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
    test.slow();

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
    await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });

    const afterLogin = await focusState(page);
    expect(afterLogin.tag, 'після входу фокус загубився на body').not.toBe('body');

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

    // ⚠ Grid — веб-компонент, і його внутрішній фокус живе у shadow DOM.
    // Тому сюди заходимо фокусом на контейнер, а далі стрілками, як людина.
    await grid.focus();
    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('ArrowDown');

    // ── 6. Вставка з Excel ───────────────────────────────────────────────
    // ⛔ Це головний шлях введення даних у системі (`ФВ-4.1`), і він мусить
    // працювати з клавіатури цілком: Ctrl+V без миші.
    await page.evaluate(async () => {
      await navigator.clipboard.writeText('101\t102\n103\t104');
    });
    await page.keyboard.press('Control+v');

    // ── 7. Збереження ────────────────────────────────────────────────────
    // Ctrl+S — той самий рефлекс, що в Excel; без нього оператор шукає кнопку.
    await page.keyboard.press('Control+s');

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
    await page.keyboard.press('Enter');

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
    await page.keyboard.press('Enter');

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
    await page.getByLabel(/Password|Пароль/i).fill(Operator.password);
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
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toBeHidden({ timeout: 10_000 });

    const returned = await focusState(page);
    expect(returned.tag, 'після Escape фокус на body').not.toBe('body');
    expect(
      returned.label,
      'після Escape фокус не повернувся на кнопку, яка відкрила діалог',
    ).toBe(opener.label);
  });
});
