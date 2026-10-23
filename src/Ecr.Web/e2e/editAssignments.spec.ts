import { expect, test, type Browser, type Page } from '@playwright/test';
import { openDocumentMore } from './documentMore';

/**
 * EA-5g: «передача місяця» проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`): шторка
 * «Edit access», майстер, смуга виконавця, «Заповнено», «Валідувати»/«Повернути», «My tasks».
 *
 * Автор документа - `e2e-admin`, виконавець - `e2e-operator`. Сценарій послідовний: кожен крок спирається на
 * стан попереднього (передано → заповнено → повернуто → знову заповнено → валідовано).
 *
 * ⚠ Написано без локального прогону (на машині автора немає стенда): перший живий прогін - CI. Локатори - за
 * `data-testid` і англійськими підписами каталогу (мова стенда - en).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Executor = { user: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!', search: 'E2E operator' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;

async function signIn(page: Page, user: { user: string; password: string }): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  // Роль, а не підпис: `getByLabel(/Password/)` резолвиться ще й у кнопку-тумблер видимості пароля.
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function asUser(browser: Browser, user: { user: string; password: string }): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await signIn(page, user);

  return page;
}

async function openDrawer(page: Page): Promise<void> {
  await page.goto(DocumentUrl);
  await openDocumentMore(page);
  await page.getByRole('menuitem', { name: /Edit access|Доступ к редактированию/ }).click();
  await expect(page.getByRole('dialog').first()).toBeVisible({ timeout: 15_000 });
}

async function rowActions(page: Page, itemName: RegExp): Promise<void> {
  const months = page.getByTestId('edit-assignments-months');
  await months.getByRole('button', { name: /Actions for|Действия для/ }).first().click();
  await page.getByRole('menuitem', { name: itemName }).click();
}

test.describe('Передача місяця: власник → виконавець → валідація', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );
  // Вхід + холодне відкриття документа на 91 таблицю (Vite, CI-раннер) інколи не вкладаються в типові 30 с.
  test.describe.configure({ mode: 'serial', timeout: 120_000 });

  // Дефект 21.10: після вибраного місяця ні Cancel, ні «X», ні Esc не закривали майстер (`onExitUnsaved` повертав false без
  // підтвердження). Кожна дорога закриває ЛИШЕ майстер; шторка лишається, фокус вертається на «Assign month…».
  test('майстер «Assign month» закривається через Cancel, X і Esc; шторка лишається, фокус на кнопці', async ({ browser }) => {
    const admin = await asUser(browser, Admin);
    await openDrawer(admin);

    const assign = admin.getByRole('button', { name: /Assign month|Передать месяц/ });
    // Корінь `[data-panel]` без розміру (Playwright вважає його hidden) - дивимось на сам діалог шторки.
    const drawer = admin.locator('[data-panel="edit-assignments"] [role="dialog"]');
    const wizardBody = admin.getByTestId('wizard-step-body');

    const closers: ReadonlyArray<readonly [string, () => Promise<void>]> = [
      ['Cancel', () => admin.getByTestId('wizard-cancel').click()],
      ['X', () => admin.locator('button.mantine-Modal-close').click()],
      ['Esc', () => admin.keyboard.press('Escape')],
    ];

    // 5 кіл підряд в одній сторінці: фокус і Esc у реальному браузері плавають, одного проходу мало.
    for (let round = 1; round <= 5; round += 1) {
      for (const [closer, closeIt] of closers) {
        const name = `коло ${String(round)}/5, ${closer}`;
        await assign.click();
        await expect(wizardBody, name).toBeVisible();
        // «dirty»: саме з обраним місяцем закриття й не працювало.
        await admin.getByRole('dialog').last().getByText(/^(February|Февраль)$/).click();
        await closeIt();

        await expect(wizardBody, `${name}: майстер закрито`).toHaveCount(0);
        await expect(drawer, `${name}: шторка лишилась`).toBeVisible();
        await expect(assign, `${name}: фокус на «Assign month…»`).toBeFocused();
      }
    }
    await admin.context().close();
  });

  test('власник передає січень виконавцю через майстер', async ({ browser }) => {
    const admin = await asUser(browser, Admin);
    await openDrawer(admin);

    await admin.getByRole('button', { name: /Assign month|Передать месяц/ }).click();
    // Mantine Chip ховає <input> поза в'юпортом (`check()` падає «outside of the viewport»): клікаємо по підпису чипа,
    // а що січень справді обрано - перевіряємо за станом прихованого чекбокса.
    await admin.getByRole('dialog').getByText(/^(January|Январь)$/).click();
    await expect(admin.getByRole('checkbox', { name: /January|Январь/ })).toBeChecked();
    await admin.getByTestId('wizard-next').click();

    await admin.getByLabel(/Find people|Найти людей/).fill(Executor.search);
    await admin.getByRole('button', { name: /E2E operator/ }).first().click({ timeout: 15_000 });
    await admin.getByTestId('wizard-next').click();

    await expect(admin.getByTestId('wizard-summary')).toBeVisible();
    await admin.getByTestId('wizard-apply').click();

    // Рядок «січень» у матриці, статус «Assigned».
    const months = admin.getByTestId('edit-assignments-months');
    await expect(months.getByRole('rowheader', { name: /January|Январь/ })).toBeVisible({ timeout: 20_000 });
    await expect(months).toContainText(/E2E operator/);
    await admin.context().close();
  });

  test('виконавець бачить смугу, ставить «Заповнено»; смуга змінюється на «чекає валідації»', async ({ browser }) => {
    const page = await asUser(browser, Executor);
    await page.goto(DocumentUrl);

    const strip = page.getByTestId('executor-strip');
    await expect(strip).toBeVisible({ timeout: 30_000 });
    await expect(strip).toContainText(/January|Январь/);

    await page.getByTestId('executor-strip-fill').click();
    await page.getByTestId('executor-strip-fill-confirm').click();

    await expect(page.getByTestId('executor-strip-fill')).toHaveCount(0, { timeout: 20_000 });
    // `GET /me/edit-assignments` віддає виконавцю лише Assigned/Returned (EditAssignmentDtos: AssignedToMe), тож після
    // «Заповнено» смуга зникає цілком; «чекає валідації» бачить власник (наступний тест).
    await expect(page.getByTestId('executor-strip')).toHaveCount(0, { timeout: 20_000 });
    await page.context().close();
  });

  test('власник бачить «Мої задачі → Awaiting my validation» і повертає місяць з коментарем', async ({ browser }) => {
    const admin = await asUser(browser, Admin);
    await admin.goto(DocumentUrl);

    await admin.getByRole('button', { name: /My tasks|Мои задачи/ }).click();
    await expect(admin.getByTestId('my-tasks-validation')).toBeVisible({ timeout: 30_000 });
    await admin.keyboard.press('Escape');

    await openDrawer(admin);
    await rowActions(admin, /Return|Вернуть/);
    const dialog = admin.getByRole('dialog', { name: /Return|Вернуть/ });
    await dialog.getByRole('textbox').fill('Check row 4');
    await dialog.getByRole('button', { name: /^(Return|Вернуть)$/ }).click();

    await expect(admin.getByTestId('edit-assignments-months')).toContainText(/Returned|Возвращено/, { timeout: 20_000 });
    await admin.context().close();
  });

  test('виконавець бачить причину повернення й заповнює знову; власник валідує', async ({ browser }) => {
    const executor = await asUser(browser, Executor);
    await executor.goto(DocumentUrl);
    await expect(executor.getByTestId('executor-strip')).toContainText('Check row 4', { timeout: 30_000 });
    await executor.getByTestId('executor-strip-fill').click();
    await executor.getByTestId('executor-strip-fill-confirm').click();
    await expect(executor.getByTestId('executor-strip-fill')).toHaveCount(0, { timeout: 20_000 });
    await executor.context().close();

    const admin = await asUser(browser, Admin);
    await openDrawer(admin);
    await rowActions(admin, /^(Validate|Проверить)$/);
    const dialog = admin.getByRole('dialog', { name: /Validate|Валидировать/ });
    await dialog.getByRole('button', { name: /^(Validate|Проверить)$/ }).click();

    await expect(admin.getByTestId('edit-assignments-months')).toContainText(/Validated|Проверено/, { timeout: 20_000 });
    await admin.context().close();
  });

  // ⛔ Прибирання за собою. Передача «січень → e2e-operator» лишалась у документі стенда після файла, і `e2e-operator`
  // назавжди ставав «виконавцем діючої передачі» (RC20 D-3, `EditAccessFacts.IsExecutor`): його відмова запису в
  // частині поза передачею стала `deny.NotAssignedToYou` («попросіть власника передати»), а не
  // `deny.InsufficientGrantLevel`. Це правильна поведінка продукту, але вона ламала `security.spec.ts` (Q-258
  // перевіряє саме поріг гранта Read < Write для оператора БЕЗ передач; файли йдуть за алфавітом, `editAssignments`
  // раніше за `security`). Тому власник знімає передачу тією самою дією «Remove», що й у шторці. `afterAll` - щоб
  // прибирання відбулося й тоді, коли сценарій впав посередині.
  test.afterAll(async ({ browser }, testInfo) => {
    if (PeriodKey === '' || DocumentId === '') return;
    // Хук має власний ліміт (типові 30 с, `describe.configure({timeout})` на нього не діє): вхід + шторка + знімання
    // на холодному CI-стенді вже самі займають ~10 с. Як і в `cellColorLive.spec.ts`.
    testInfo.setTimeout(120_000);

    const admin = await asUser(browser, Admin);
    try {
      await openDrawer(admin);
      const rowMenu = admin.getByTestId('edit-assignments-months').getByRole('button', { name: /Actions for|Действия для/ });
      // Матриця вантажиться асинхронно: без передач рядка не буде - тоді чекання закінчується тихо, і нема що знімати.
      await rowMenu.first().waitFor({ state: 'visible', timeout: 15_000 }).catch(() => undefined);
      // Цикл: передач може бути більше однієї, якщо сценарій обірвався й повторювався.
      for (let guard = 0; guard < 5; guard += 1) {
        const before = await rowMenu.count();
        if (before === 0) break;

        await rowActions(admin, /^(Remove|Снять передачу)$/);
        const dialog = admin.getByRole('dialog', { name: /Take .* back from the executors|Забрать .* у исполнителей/ });
        await dialog.getByRole('button', { name: /^(Remove|Снять передачу)$/ }).click();
        await expect(dialog).toBeHidden({ timeout: 20_000 });
        // ⛔ Гонитва «рядок ще в DOM, але вже зникає»: діалог закривається за 204 на DELETE, а список перечитується
        // ПІСЛЯ (GET …/edit-assignments). `count()` у цей проміжок ще бачив старий рядок, а наступний `click()` на
        // ньому висів без ліміту до таймауту хука (CI 38283603905: «afterAll hook timeout of 30000ms», кінцевий
        // стан шторки - «Nothing is handed over yet»). Тож чекаємо, поки рядків стане на один менше.
        await expect(rowMenu).toHaveCount(before - 1, { timeout: 20_000 });
      }

      await expect(rowMenu, 'передачі стенда не знято: security.spec побачить оператора-виконавця').toHaveCount(0, {
        timeout: 20_000,
      });
    } finally {
      await admin.context().close();
    }
  });
});
