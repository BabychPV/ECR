import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type Browser, type BrowserContext, type Locator, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';

/**
 * Категорії констант і похідні аргументи на сторінці версії методології (план CONST-CATEGORY,
 * lane/integ-methodology-rc) на живому стенді:
 *   1. діалог «Categories»: додати категорію (PUT), опис видно в переліку, зайняту не прибрати;
 *      діалог адресований (`?mcats=1`) і закривається Escape;
 *   2. категорія константи — вибір зі списку (а не вільний текст); таблиця показує колонку Category
 *      («Shared» для порожньої); фільтр за категорією й «Shared» працює і переживає перезавантаження
 *      (`?ccat=`);
 *   3. діалог формули: поля «Declared arguments» немає, похідний список показує `@Flow?` з позначкою «?»;
 *   4. справжній axe `color-contrast` у світлій і темній темі на 1440 і 500 px, без горизонтальної
 *      прокрутки на 500 px; знімки в `ECR_E2E_SHOTS_DIR`.
 *
 * ⚠ Дані — свої: методологія `E2E_MCAT_<час>`, чернетка з двома константами й формулою, тимчасові роль
 * і користувач-методолог. `afterAll` повертає тему методолога до світлої, видаляє чернетку (з нею —
 * перелік категорій, константи й формулу), знімає роль із користувача і видаляє роль; саму методологію API не видаляє — порожній залишок на одноразовому стенді (як
 * `methodologyUi.spec.ts`).
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "Methodology categories"`.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

/**
 * ⚠ Роль стенда `E2EAdmin` не має `Calculation.EditConstant` (запис констант і переліку категорій), а
 * роль продукту/стенда тут не змінюється. Тому — свій методолог (той самий прийом, що
 * `airLimitsReal.spec.ts`): тимчасова роль із правами методології й користувач із нею; `afterAll`
 * знімає з нього роль і видаляє роль (користувача API не видаляє).
 */
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const EditorRole = `E2EMCAT${Stamp}`;
const Editor = {
  userName: `e2e-mcat-${Stamp.toLowerCase()}`,
  issued: 'E2E-Meth0d-2026!',
  password: 'E2E-Meth0d-Work-2026!',
};
const EditorPermissions = [
  'Calculation.View',
  'Calculation.EditFormula',
  'Calculation.EditConstant',
  'Calculation.EditRule',
  'Template.View',
];
let editorRoleId = 0;
let editorUserId = 0;
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const ShotsDir = process.env['ECR_E2E_SHOTS_DIR'] ?? '';

const CategoryCode = 'E2E_Loc_A';
const CategoryDescription = 'E2E zone A before MR';
const Categorised = 'E2E_K1';
const SharedOne = 'E2E_K2';

interface Seed {
  readonly methodologyId: number;
  readonly versionId: number;
}

let seed: Seed | null = null;

async function signedIn(
  browser: Browser,
  who: { readonly userName: string; readonly password: string } = Admin,
): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: who.userName, password: who.password },
  });
  expect(login.ok(), `вхід ${who.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);
  return context;
}

async function signIn(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  await page.goto('/login');
  await page.evaluate((colour: string) => localStorage.setItem('mantine-color-scheme-value', colour), scheme);
  await page.reload();
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  // Сторінку веде методолог із `Calculation.EditConstant` (див. `Editor` угорі).
  await page.getByLabel(/User name|Ім'я/i).fill(Editor.userName);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Editor.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });

  // ⚠ Тема з сервера перемагає localStorage (`usePreferenceSync`) — пишемо налаштування і перевіряємо атрибут.
  const put = await page.request.put('/api/v1/me/preferences/theme', {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify(scheme),
  });
  expect(put.ok(), `тема: ${String(put.status())} ${await put.text()}`).toBe(true);
  await page.reload();
  await expect
    .poll(() => page.evaluate(() => document.documentElement.getAttribute('data-mantine-color-scheme')), { timeout: 15_000 })
    .toBe(scheme);
}

const pageUrl = (data: Seed): string => `/admin/methodologies/${String(data.methodologyId)}/versions`;

async function openPage(page: Page, data: Seed, query = ''): Promise<Locator> {
  await page.goto(`${pageUrl(data)}${query}`);
  const table = page.getByTestId('methodology-constants');
  await expect(table).toBeVisible({ timeout: 30_000 });
  return table;
}

function constantRow(table: Locator, code: string): Locator {
  return table.locator('tbody tr', { hasText: code });
}

async function openCategories(page: Page): Promise<Locator> {
  await page.getByTestId('methodology-constants-categories').click();
  const dialog = page.getByRole('dialog', { name: /Constant categories/i });
  await expect(dialog).toBeVisible({ timeout: 15_000 });
  await expect(dialog.getByTestId('methodology-categories')).toBeVisible();
  return dialog;
}

async function shot(page: Page, testInfo: { attach: (n: string, o: { body: Buffer; contentType: string }) => Promise<void> }, name: string): Promise<void> {
  const body = await page.screenshot({ fullPage: false });
  await testInfo.attach(name, { body, contentType: 'image/png' });
  if (ShotsDir !== '') {
    mkdirSync(ShotsDir, { recursive: true });
    await page.screenshot({ path: path.join(ShotsDir, `${name}.png`) });
  }
}

test.describe('Methodology categories: list dialog, pick-list, filter, derived arguments', () => {
  test.describe.configure({ timeout: 240_000, mode: 'serial' });

  test.beforeAll(async ({ browser }) => {
    if (PeriodKey === '') throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD (tools/e2e-stand.ps1)');

    const admin = await signedIn(browser);
    try {
      // Тимчасовий методолог: роль із правами методології і користувач із нею (пароль — одразу змінений).
      const role = await admin.request.post('/api/v1/roles', {
        data: { code: EditorRole, nameL10n: { en: 'E2E methodology editor' }, permissionCodes: EditorPermissions },
      });
      expect(role.ok(), `роль методолога: ${String(role.status())} ${await role.text()}`).toBe(true);
      editorRoleId = ((await role.json()) as { id: number }).id;
      const user = await admin.request.post('/api/v1/users', {
        data: {
          userName: Editor.userName,
          provider: 'Local',
          sid: null,
          displayName: 'E2E methodology editor',
          initialPassword: Editor.issued,
          roleCodes: [EditorRole],
        },
      });
      expect(user.ok(), `методолог: ${String(user.status())} ${await user.text()}`).toBe(true);
      editorUserId = ((await user.json()) as { id: number }).id;
      const first = await signedIn(browser, { userName: Editor.userName, password: Editor.issued });
      try {
        const changed = await first.request.post('/api/v1/auth/change-password', {
          data: { currentPassword: Editor.issued, newPassword: Editor.password },
        });
        expect(changed.ok(), `пароль методолога: ${String(changed.status())} ${await changed.text()}`).toBe(true);
      } finally {
        await first.close();
      }

      const stamp = String(Date.now());
      const created = await admin.request.post('/api/v1/methodologies', {
        data: { code: `E2E_MCAT_${stamp}`, group: null, kind: 'DataDriven', nameL10n: { en: `E2E methodology categories ${stamp}` } },
      });
      expect(created.ok(), `методологія: ${await created.text()}`).toBe(true);
      const methodologyId = ((await created.json()) as { id: number }).id;

      const draft = await admin.request.post(`/api/v1/methodologies/${String(methodologyId)}/versions`, {
        data: { versionNumber: '1.0', copyFromVersionId: null, level: 'Configuration' },
      });
      expect(draft.ok(), `чернетка: ${await draft.text()}`).toBe(true);
      const versionId = ((await draft.json()) as { id: number }).id;
      const base = `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}`;

      // Дві спільні константи одного виду (текст — без одиниці); категорію першій дає тест через UI.
      // ⚠ Від імені методолога: `Calculation.EditConstant` є лише в нього.
      const editor = await signedIn(browser, Editor);
      for (const code of [Categorised, SharedOne]) {
        const constant = await editor.request.put(`${base}/constants/${code}`, {
          data: {
            kind: 'Text',
            value: null,
            unitId: null,
            textValue: `value of ${code}`,
            validFrom: null,
            validTo: null,
            category: null,
            substanceEntryId: null,
            source: null,
          },
        });
        expect(constant.ok(), `константа ${code}: ${await constant.text()}`).toBe(true);
      }
      await editor.close();

      // Формула з необов'язковим аргументом — маркер `?` у самому виразі.
      const formula = await admin.request.put(`${base}/formulas/E2E_F1`, {
        data: { expression: '@Flow? * 2', resultType: 'Number', outputUnitId: null },
      });
      expect(formula.ok(), `формула: ${await formula.text()}`).toBe(true);

      seed = { methodologyId, versionId };
    } finally {
      await admin.close();
    }
  });

  test.afterAll(async ({ browser }) => {
    const problems: string[] = [];

    // Тема методолога — назад на світлу, поки в нього ще є роль (вподобання на сервері).
    if (editorUserId !== 0) {
      try {
        const editor = await signedIn(browser, Editor);
        await editor.request.put('/api/v1/me/preferences/theme', { headers: { 'Content-Type': 'application/json' }, data: JSON.stringify('light') });
        await editor.close();
      } catch (error) {
        problems.push(`тема методолога: ${String(error)}`);
      }
    }

    const admin = await signedIn(browser);
    try {
      if (seed !== null) {
        const removed = await admin.request.delete(`/api/v1/methodologies/${String(seed.methodologyId)}/versions/${String(seed.versionId)}`);
        if (![200, 204, 404].includes(removed.status())) problems.push(`видалення чернетки: ${String(removed.status())}`);
      }
      // Користувач лишається без ролей (видалити його API не дає), тимчасова роль видаляється.
      if (editorUserId !== 0) {
        const stripped = await admin.request.put(`/api/v1/users/${String(editorUserId)}/roles`, { data: { roleCodes: [] } });
        if (!stripped.ok()) problems.push(`ролі методолога: ${String(stripped.status())} ${(await stripped.text()).slice(0, 300)}`);
      }
      if (editorRoleId !== 0) {
        const removedRole = await admin.request.delete(`/api/v1/roles/${String(editorRoleId)}`);
        if (removedRole.status() >= 300 && removedRole.status() !== 404) problems.push(`роль: ${String(removedRole.status())} ${(await removedRole.text()).slice(0, 300)}`);
      }
    } finally {
      await admin.close();
    }
    expect(problems, 'прибирання після набору').toEqual([]);
  });

  test('categories dialog adds a category; the constant picks it from the list; the table filters by category', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    const table = await openPage(page, data);

    // Обидві константи спільні: колонка Category каже «Shared».
    await expect(constantRow(table, Categorised).locator('td').nth(1)).toHaveText('Shared');

    // 1. Діалог переліку адресований і порожній.
    let dialog = await openCategories(page);
    await expect(page).toHaveURL(/[?&]mcats=1/);
    await dialog.getByLabel('Code', { exact: true }).fill(CategoryCode);
    await dialog.getByLabel('Description', { exact: true }).fill(CategoryDescription);
    const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().includes(`/categories/${CategoryCode}`));
    await dialog.getByRole('button', { name: 'Add category' }).click();
    expect((await saved).status()).toBe(200);
    const categoryRow = dialog.locator(`tr[data-category-code="${CategoryCode}"]`);
    await expect(categoryRow).toContainText(CategoryDescription);
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden({ timeout: 10_000 });
    await expect(page).not.toHaveURL(/mcats=/);

    // 2. Категорія константи — вибір зі списку.
    await constantRow(table, Categorised).getByRole('button', { name: 'Edit' }).click();
    const constantDialog = page.getByRole('dialog', { name: /Constant/i });
    await expect(constantDialog).toBeVisible({ timeout: 10_000 });
    const category = constantDialog.getByRole('textbox', { name: 'Category', exact: true });
    await category.click();
    await page.getByRole('option', { name: CategoryCode, exact: true }).click();
    await expect(category).toHaveValue(CategoryCode);
    await constantDialog.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(constantDialog).toBeHidden({ timeout: 15_000 });
    await expect(constantRow(table, Categorised).locator('td').nth(1)).toHaveText(CategoryCode, { timeout: 15_000 });
    await expect(constantRow(table, SharedOne).locator('td').nth(1)).toHaveText('Shared');

    // 3. Фільтр за категорією — в адресі, переживає перезавантаження.
    const filter = page.getByRole('textbox', { name: 'Filter by category' });
    await filter.click();
    await page.getByRole('option', { name: CategoryCode, exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`[?&]ccat=${CategoryCode}`));
    await expect(table.locator('tbody tr')).toHaveCount(1);
    await expect(constantRow(table, Categorised)).toHaveCount(1);
    await page.reload();
    await expect(page.getByTestId('methodology-constants').locator('tbody tr')).toHaveCount(1, { timeout: 30_000 });

    const reloadedFilter = page.getByRole('textbox', { name: 'Filter by category' });
    await reloadedFilter.click();
    await page.getByRole('option', { name: 'Shared', exact: true }).click();
    await expect(page.getByTestId('methodology-constants').locator('tbody tr')).toHaveCount(1);
    await expect(constantRow(page.getByTestId('methodology-constants'), SharedOne)).toHaveCount(1);

    // 4. Зайняту категорію не прибрати: кнопка недоступна, лічильник — 1.
    dialog = await openCategories(page);
    const used = dialog.locator(`tr[data-category-code="${CategoryCode}"]`);
    await expect(used.locator('td').nth(2)).toHaveText('1');
    await expect(used.getByRole('button', { name: 'Remove' })).toBeDisabled();
    await expect(used.getByRole('button', { name: 'Retire' })).toBeDisabled();
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden({ timeout: 10_000 });
  });

  test('formula dialog: no declared-arguments input; the derived list marks @Flow? as optional', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openPage(page, data);

    const formulaRow = page.locator('tr', { hasText: 'E2E_F1' }).first();
    await formulaRow.getByRole('button', { name: 'Edit' }).click();
    const dialog = page.getByRole('dialog', { name: /Formula/i });
    await expect(dialog).toBeVisible({ timeout: 10_000 });

    await expect(dialog.getByRole('textbox', { name: /Declared arguments/i })).toHaveCount(0);
    const derived = dialog.getByTestId('derived-arguments');
    await expect(derived).toBeVisible();
    const flow = derived.locator('tr[data-argument="@Flow?"]');
    await expect(flow).toBeVisible();
    await expect(flow).toContainText('Flow');
    await expect(flow).toContainText('optional');
    await expect(derived.locator('textarea, input')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden({ timeout: 10_000 });
  });

  for (const scheme of ['light', 'dark'] as const) {
    test(`${scheme}: real axe color-contrast at 1440 and 500 px, no horizontal scroll, screenshots`, async ({ page }, testInfo) => {
      const data = seed!;
      await page.setViewportSize({ width: 1440, height: 900 });
      await signIn(page, scheme);
      expect(await page.evaluate(() => document.documentElement.getAttribute('data-mantine-color-scheme'))).toBe(scheme);
      const table = await openPage(page, data);
      await table.scrollIntoViewIfNeeded();

      expect(await axeViolations(page, 'color-contrast'), `color-contrast constants @1440 ${scheme}`).toEqual([]);
      await shot(page, testInfo, `methodology-categories-table-${scheme}-1440`);

      const dialog = await openCategories(page);
      await expect(dialog.locator(`tr[data-category-code="${CategoryCode}"]`)).toBeVisible({ timeout: 15_000 });
      expect(await axeViolations(page, 'color-contrast'), `color-contrast categories dialog @1440 ${scheme}`).toEqual([]);
      await shot(page, testInfo, `methodology-categories-dialog-${scheme}-1440`);
      await page.keyboard.press('Escape');
      await expect(dialog).toBeHidden({ timeout: 10_000 });

      await page.setViewportSize({ width: 500, height: 900 });
      await page.waitForTimeout(300);
      const overflow = await page.evaluate(() => ({
        doc: document.documentElement.scrollWidth - document.documentElement.clientWidth,
        body: document.body.scrollWidth - document.body.clientWidth,
      }));
      expect(overflow.doc, `горизонтальна прокрутка сторінки @500 ${scheme}`).toBeLessThanOrEqual(1);
      expect(overflow.body).toBeLessThanOrEqual(1);
      await page.getByTestId('methodology-constants').scrollIntoViewIfNeeded();
      expect(await axeViolations(page, 'color-contrast'), `color-contrast constants @500 ${scheme}`).toEqual([]);
      await shot(page, testInfo, `methodology-categories-table-${scheme}-500`);

      const small = await openCategories(page);
      await expect(small.locator(`tr[data-category-code="${CategoryCode}"]`)).toBeVisible({ timeout: 15_000 });
      expect(await axeViolations(page, 'color-contrast'), `color-contrast categories dialog @500 ${scheme}`).toEqual([]);
      await shot(page, testInfo, `methodology-categories-dialog-${scheme}-500`);
      await page.keyboard.press('Escape');
      await page.setViewportSize({ width: 1440, height: 900 });
    });
  }
});
