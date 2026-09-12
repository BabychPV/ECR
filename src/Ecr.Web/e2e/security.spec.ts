import { expect, test, type Page } from '@playwright/test';

/**
 * Гранти на `/admin/security` дійсно керують доступом іншої сесії (Q-258).
 *
 * ⛔ До цього файла єдине покриття `/admin/security` (`screenshots.spec.ts`)
 * лише перевіряло, що екран не впав. Форму видачі/відкликання гранта не
 * натискав жоден прогін, і те, що клік у ній справді доходить до тих самих
 * шляхів, що й `AccessDecisionTests`/`ResourceGrantTests`
 * (`Ecr.Infrastructure.Tests`, `Ecr.Application.Tests`) — а не лише малює
 * рядок у таблиці, — залишалося недоведеним.
 *
 * ⚠ Гранта, ЯКОЇ ЩЕ НЕМАЄ, у стенді не знайти: `tools/e2e-stand.ps1` сам видає
 * `E2EOperator` запис `Project/1/Write` (крок «ресурсні гранти на проєкт») —
 * без нього стенд і власну перевірку («перелік проєктів порожній: грант не
 * діє») не пройшов би. Тому сценарій СПЕРШУ знижує цей запис через форму до
 * `Read` (щоб отримати чесний старт «писати не можна»), тоді видає `Write`
 * назад — і лише тоді «видав право» справді означає «щойно натиснутою
 * формою», а не «воно й так там лежало від стенда».
 *
 * ⛔ Базовий рівень — `Read`, НЕ повна відсутність гранта. `GetDocumentHandler`
 * (`Ecr.Application/Documents/DocumentQueryHandlers.cs`) віддає документ як
 * `null` (→ 404 → сторінка без жодного заголовка), коли
 * `profile.LevelFor(Project, projectId) < GrantLevel.Read` — тобто нульовий
 * грант ховає документ ЦІЛКОМ, і `gotoDocument` ніколи не побачив би сітку,
 * якою цей сценарій і доводить ефект. Межа, яку перевіряє Q-258, —
 * САМЕ поріг `Write` у `EditRules.CanEdit`, а не сам факт існування рядка
 * гранта, тож `Read` — правильний «немає права редагувати» стан: документ
 * видимий, вставка — ні.
 *
 * ⛔ Доказ ефекту — НЕ вигляд таблиці гранту (той самий екран, що редагує), а
 * ІНША сесія: оператор намагається вставити значення в комірку документа.
 * `DocumentGrid.onPaste` (`features/grid/DocumentGrid.tsx`) читає
 * `cellPermissions` зрізу, який рахує `EditRules.CanEdit`
 * (`Ecr.Application/Security/EditRules.cs`) за порогом `GrantLevel.Write`
 * (`Effective(profile, context) >= GrantLevel.Write`) — тобто саме тим
 * рішенням, яке міняє PUT `/roles/{id}/grants`. Рівня `Write` немає — вставка
 * відхиляється ЦІЛИМ пакетом (`ФВ-4.1`, "Some cells were not saved") із
 * причиною `deny.NoGrant` (вона й для «зовсім немає гранта», і для
 * «є, але нижче порогу» — `EditRules.CanEdit` не розрізняє); є — комірка
 * зберігається, і панель показує `data-save-status="saved"`
 * (`useCellPatch.ts`).
 *
 * ⚠ Паролі тут ТЕСТОВІ й існують лише в тимчасовій базі, яку стенд же й
 * видаляє (`tools/e2e-stand.ps1`).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Admin-Work-2026!' };
const Operator = { user: 'e2e-operator', password: 'E2E-Operator-Work-2026!' };

/** Роль оператора, заведена стендом (`tools/e2e-stand.ps1`, крок «роль оператора»). */
const OperatorRole = 'E2EOperator';

/** Проєкт і документ приходять зі стенда: зашите число ламалося б у січні. */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Гранти на /admin/security діють на сесію оператора (Q-258)', () => {
  // ⚠ Гейт більше не вирішує долю набору: без стенда падає весь набір
  // (`e2e/globalSetup.ts`). Він працює лише під `ECR_E2E_OPTIONAL`, коли
  // пропуск оголошений свідомо, — і змінна названа в тексті навмисно, щоб
  // рядок «немає стенда» не читався знову як норма.
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('видача і відкликання гранта через форму позначаються на іншій сесії', async ({ page }) => {
    // Шість входів поспіль (адмін↔оператор) на реальному SQL Server — довше за
    // типовий прогін.
    test.setTimeout(180_000);

    // ── 1. Чесний старт: адмін знижує готовий грант стенда до Read ─────────
    // (не прибирає геть — GetDocumentHandler ховає документ ЦІЛКОМ нижче
    // Read, а сценарій має довести саме поріг Write, не видимість документа).
    await signIn(page, Admin.user, Admin.password);
    await openGrantsFor(page, OperatorRole);
    await removeAllGrantRows(page);
    await addGrant(page, { resourceId: 1, level: 'Read' });
    await saveGrants(page);
    await signOut(page, Admin.user);

    // ── 2. Read, не Write — вставка відхиляється саме з цієї причини ───────
    await signIn(page, Operator.user, Operator.password);
    await gotoDocument(page);
    await expectPasteDenied(page);
    await signOut(page, Operator.user);

    // ── 3. Адмін підвищує Project/1 до Write через форму ────────────────────
    await signIn(page, Admin.user, Admin.password);
    await openGrantsFor(page, OperatorRole);
    await removeAllGrantRows(page);
    await addGrant(page, { resourceId: 1, level: 'Write' });
    await saveGrants(page);
    await signOut(page, Admin.user);

    // ── 4. Оператор бачить наслідок: вставка тепер зберігається ────────────
    await signIn(page, Operator.user, Operator.password);
    await gotoDocument(page);
    await expectPasteAccepted(page);
    await signOut(page, Operator.user);

    // ── 5. Адмін знижує той самий грант назад до Read ───────────────────────
    await signIn(page, Admin.user, Admin.password);
    await openGrantsFor(page, OperatorRole);
    await removeAllGrantRows(page);
    await addGrant(page, { resourceId: 1, level: 'Read' });
    await saveGrants(page);
    await signOut(page, Admin.user);

    // ── 6. Право зникло знову, і причина — та сама, не мовчазний збій ──────
    await signIn(page, Operator.user, Operator.password);
    await gotoDocument(page);
    await expectPasteDenied(page);
  });
});

/** Вхід без миші — той самий шлях, що й у справжнього користувача. */
async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });

  await page.getByLabel(/User name|Ім'я/i).fill(user);

  // ⚠ Q-260 (змержено вже ПІСЛЯ написання цього файла) додав кнопці-тумблеру
  // видимості пароля `aria-label="Toggle password visibility"`
  // (`LoginPage.tsx`, `passwordToggleProps`) — і `getByLabel(/Password|Пароль/i)`
  // відтоді резолвиться у ДВА елементи: саме поле і цю кнопку (регулярка без
  // прив'язки до країв ловить підрядок "password" і в її назві теж). Роль
  // розрізняє їх однозначно: поле вводу — `textbox`, кнопка — `button`.
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/**
 * Вихід через меню користувача.
 *
 * ⛔ `UserMenu.tsx` виходить через `window.location.assign('/login')` —
 * ПОВНЕ перезавантаження, не перехід роутером: «cookie сеансу гасить сервер,
 * але в пам'яті клієнта лишається кеш `react-query` з профілем і даними
 * попереднього користувача». Саме тому наступний вхід тут ЧЕСНИЙ: кеш зрізу
 * таблиці не переживає цей вихід, і `expectPasteAccepted`/`expectPasteDenied`
 * бачать те, що справді прийшло з сервера ПІСЛЯ зміни гранта, а не залишок
 * від попередньої сесії в тому самому вкладці.
 */
async function signOut(page: Page, userName: string): Promise<void> {
  const menu = page.getByRole('button', { name: new RegExp(userName, 'i') }).first();
  await expect(menu, 'у шапці немає меню користувача').toBeVisible({ timeout: 30_000 });
  await menu.click();

  const logout = page.getByRole('menuitem', { name: /Sign out|Вийти/i }).first();
  await expect(logout, 'у меню немає виходу').toBeVisible({ timeout: 10_000 });
  await logout.click();

  await page.waitForURL('**/login**', { timeout: 30_000 });
}

/** Відкриває вкладку «Grants» і обирає роль у випадаючому списку. */
async function openGrantsFor(page: Page, roleCode: string): Promise<void> {
  // ⚠ Вкладка — в адресі (`useUrlState('tab')`, `SecurityPage.tsx`): пряме
  // відкриття URL — той самий шлях, що й клік у `SegmentedControl`, лише без
  // зайвого проміжного кліку, який тут нічого не доводить.
  await page.goto('/admin/security?tab=grants');

  // ⚠ `getByLabel` тут неоднозначний: відкритий `Select` Mantine малює
  // `listbox`, який теж має `aria-labelledby` на той самий підпис, — і
  // `getByLabel` бачить ДВА елементи (поле і сам список) одразу, щойно
  // список відкрився. `getByRole('textbox', …)` бачить лише поле.
  const roleSelect = page.getByRole('textbox', { name: 'Role', exact: true });
  await expect(roleSelect, 'немає селектора ролі на вкладці Grants').toBeVisible({
    timeout: 30_000,
  });
  await roleSelect.click();
  await page.getByRole('option', { name: roleCode, exact: true }).click();

  // ⚠ Панель гранта завантажилась: або є рядок «Remove», або порожній стан
  // «This role has no grants». Обидва — ознака того, що запит на гранти
  // конкретної ролі вже відповів, і далі можна чіпати рядки, не наздоганяючи
  // спінер.
  const settled = page
    .getByRole('button', { name: 'Remove' })
    .first()
    .or(page.getByText('This role has no grants'));
  await expect(settled, 'панель грантів не завантажилась').toBeVisible({ timeout: 30_000 });
}

/** Видаляє всі рядки гранта з чернетки (клієнтський `draft`, ще не збережено). */
async function removeAllGrantRows(page: Page): Promise<void> {
  const remove = page.getByRole('button', { name: 'Remove' });

  // ⚠ Цикл, а не фіксована кількість: стенд заводить один запис
  // (`Project/1/Write`), але друге відкликання в сценарії (крок 5) застає вже
  // РІВНО той, що додав сам сценарій на кроці 3 — кількість рядків різна, а
  // умова зупинки та сама.
  while ((await remove.count()) > 0) {
    await remove.first().click();
  }

  await expect(page.getByText('This role has no grants'), 'рядки лишилися').toBeVisible({
    timeout: 10_000,
  });
}

/** Додає новий рядок гранта і заповнює його через форму (`GrantsPanel.tsx`). */
async function addGrant(
  page: Page,
  grant: { resourceId: number; level: 'Read' | 'Write' | 'Submit' | 'Approve' | 'Manage' },
): Promise<void> {
  await page.getByRole('button', { name: 'Add grant' }).click();

  // ⚠ Новий рядок — завжди перший (чернетка щойно спорожніла в
  // `removeAllGrantRows`), тому індекс у aria-мітках — `1`. Kind лишається
  // дефолтним `Project` (`GrantsPanel.tsx`: новий рядок заводиться саме з
  // ним) — вибирати нема чого, документ і стенд узгоджені саме на цьому виді
  // ресурсу.
  const resourceIdField = page.getByLabel('Resource id 1');
  await expect(resourceIdField, 'немає поля Resource id щойно доданого рядка').toBeVisible({
    timeout: 10_000,
  });
  await resourceIdField.fill(String(grant.resourceId));

  // ⚠ Той самий нюанс, що й у ролі вище: `getByLabel` став би неоднозначним
  // щойно список відкриється (`aria-label` тут стоїть прямо на полі, а не на
  // окремому `<label>`, але відкритий `listbox` усе одно підхоплює той самий
  // текст як власну доступну назву).
  await page.getByRole('textbox', { name: 'Level 1', exact: true }).click();
  await page.getByRole('option', { name: grant.level, exact: true }).click();
}

/** Зберігає чернетку гранта і чекає підтвердження сервера. */
async function saveGrants(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Save' }).click();

  // ⚠ Текст саме цей (`grants.saved`, `09-seed.sql`): підтверджує, що PUT
  // `/roles/{id}/grants` дійшов і сервер відповів, а не що кнопка просто
  // клікнулася.
  await expect(
    page.getByText('Access updated; affected sessions revalidate immediately.'),
    'сервер не підтвердив збереження гранта',
  ).toBeVisible({ timeout: 15_000 });
}

/** Відкриває документ стенда на потрібному періоді. */
async function gotoDocument(page: Page): Promise<void> {
  await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
  await expect(page.getByRole('heading').first(), 'документ не відрендерився').toBeVisible({
    timeout: 30_000,
  });

  const grid = page.locator('revo-grid').first();
  await expect(grid, 'сітка документа не з\'явилася').toBeVisible({ timeout: 30_000 });
}

/**
 * Вставляє значення в першу комірку grid.
 *
 * ⛔ Позиція вставки — завжди {rowIndex: 0, columnIndex: 0}
 * (`DocumentGrid.tsx`, `onPaste`): планувальник читає її буквально з
 * константи виклику `planPaste`, а не з поточного виділення. Фокус на сітці
 * тут потрібен лише для того, щоб подія `paste` взагалі дійшла до обробника
 * React, — не для позиціювання.
 */
async function pasteFirstCell(page: Page, value: string): Promise<void> {
  const grid = page.locator('revo-grid').first();

  // ⚠ `revo-grid` — веб-компонент без власного `tabindex`/`focus()`, що
  // делегує фокус усередину: `grid.focus()` (Playwright: `element.focus()`)
  // залишає справжній `document.activeElement` там, де він був ДО цього —
  // перевірено окремо (`activeElement after focus()` показував заголовок
  // сторінки, `<h3>`, а не сітку — фокус туди переносить а11y-механізм
  // переходу маршруту, `RouteAnnouncer`/`AppLayout`). Тому подія `paste` від
  // `Control+v` ішла на заголовок і НІКОЛИ не діставалася обробника React
  // (`onPaste` на `<Stack>`, `DocumentGrid.tsx`) — ані відхилення, ані
  // збереження не траплялося: клавіші просто нікуди не вели. Справжній КЛІК
  // — той самий шлях, яким людина заходить у клітинку, і саме він реально
  // переносить фокус у shadow DOM грида (RevoGrid керує власним активним
  // станом через click, не через programmatic `.focus()` контейнера).
  await grid.click();

  await page.evaluate(async (text) => {
    await navigator.clipboard.writeText(text);
  }, value);

  await page.keyboard.press('Control+v');
}

/** Перевіряє, що вставка ВІДХИЛЕНА — з конкретною, розрізнюваною причиною. */
async function expectPasteDenied(page: Page): Promise<void> {
  await pasteFirstCell(page, '777');

  // ⛔ `grid.rejectedTitle` (`09-seed.sql`): модалка з переліком відхилених
  // комірок — не мовчазний збій і не той самий екран, що показує успіх.
  await expect(
    page.getByText('Some cells were not saved'),
    'вставку мали відхилити — гранта немає, а модалка відмови не з\'явилася',
  ).toBeVisible({ timeout: 15_000 });

  // ⚠ Причина — рівно `deny.NoGrant`, а не будь-яка відмова: перевіряємо
  // текст, а не лише факт модалки, інакше цей самий тест пройшов би і на
  // геть іншій, непов'язаній причині заборони.
  await expect(
    page.getByText('You do not have permission to edit this cell.'),
    'причина відмови не збігається з очікуваною (deny.NoGrant)',
  ).toBeVisible();

  await page.keyboard.press('Escape');
}

/** Перевіряє, що вставка ПРИЙНЯТА — сервер справді зберіг значення. */
async function expectPasteAccepted(page: Page): Promise<void> {
  await pasteFirstCell(page, '777');

  // ⛔ `data-save-status="saved"` з'являється лише ПІСЛЯ успішної відповіді
  // `PATCH /documents/{id}/cells` (`useCellPatch.ts`) і сама зникає за 2
  // секунди — це не клієнтська оптимістична позначка, а підтвердження
  // сервера, що комірку справді записано.
  await expect(
    page.locator('[data-save-status="saved"]'),
    'сервер не підтвердив збереження — грант не подіяв',
  ).toBeVisible({ timeout: 15_000 });

  // Модалки відмови в цьому шляху не мало бути жодного разу.
  await expect(page.getByText('Some cells were not saved')).toHaveCount(0);
}
