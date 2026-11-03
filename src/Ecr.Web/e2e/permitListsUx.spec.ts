import { expect, test, type Page } from '@playwright/test';
import { ensureNaeTree } from './airScope';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * Permit lists UX (людина 03.11: «сторінка Lists незрозуміла») - екран Configure › Permits › Lists на живому стенді:
 * одне речення-вступ; стовпці List · Kind · Values · Used by (лічильник словами, споживачі «тип · таблиця › стовпець»);
 * вид словами без дубля «Kept in…» під назвою; фільтр «All kinds» з лічильниками; шеврон у рядку; Enter відкриває шторку;
 * рядок «NAE structure» веде на вкладку НАЕ, а не до шторки. axe (wcag2a/2aa) у light і dark на 1440 і 500 px; тему
 * адміністратора `afterAll` повертає на light.
 *
 * Запуск: `tools/e2e-stand.ps1 -Grep "Permit lists UX"`; CI - workflow `e2e-stand` з `grep`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Тема через меню користувача (вподобання зберігає сервер), і перевірка атрибута на `<html>`. */
async function setScheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  const html = page.locator('html');
  const menu = page.getByRole('button', { name: new RegExp(Admin.user, 'i') }).first();
  await expect(menu).toBeVisible({ timeout: 30_000 });
  if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
    await menu.click();
    await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
    await page.keyboard.press('Escape');
  }
  await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

async function axe(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(async () => {
    const run = (window as unknown as {
      axe: { run: (c: Document, o: object) => Promise<{ violations: { id: string; nodes: { target: unknown[] }[] }[] }> };
    }).axe;
    const result = await run.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } });

    return result.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${String(n.target.join(' '))}`));
  });
}

const column = (code: string, over: Record<string, unknown>): Record<string, unknown> => ({
  code,
  nameL10n: { en: code },
  dataType: 'String',
  unitId: null,
  required: false,
  key: false,
  group: null,
  precision: null,
  scale: null,
  listScope: null,
  listRegistryCode: null,
  siblingTableCode: null,
  choices: null,
  order: 0,
  ...over,
});

/** Заводить і ПУБЛІКУЄ тип з таблицею `Items`: ключ `Name`, список «лише тип» (Alpha, Beta) і стовпець зі спільним деревом НАЕ. */
async function publishType(page: Page, code: string, method: string): Promise<void> {
  const made = await page.request.post('/api/v1/permit-types', {
    data: { code, category: 'Other', nameL10n: { en: `E2E_ lists ux ${code}` } },
  });
  expect(made.status(), await made.text()).toBe(201);
  const draft = (await made.json()) as { rowVersion: string };
  const columns = [
    column('Name', { key: true, required: true, order: 1 }),
    column(method, { dataType: 'Lookup', listScope: 'TypeOnly', choices: ['Alpha', 'Beta'], order: 2 }),
    column('Site', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'NAE_TREE', order: 3 }),
  ];
  const saved = await page.request.put(`/api/v1/permit-types/${code}`, {
    data: {
      category: 'Other',
      nameL10n: { en: `E2E_ lists ux ${code}` },
      rowVersion: draft.rowVersion,
      rules: [],
      tables: [{ code: 'Items', nameL10n: { en: 'Items' }, order: 1, rules: [], columns }],
    },
  });
  expect(saved.status(), await saved.text()).toBe(200);
  const replaced = (await saved.json()) as { rowVersion: string };
  const published = await page.request.post(`/api/v1/permit-types/${code}/publish`, {
    data: { reason: 'E2E permit lists UX type', rowVersion: replaced.rowVersion },
  });
  expect(published.status(), await published.text()).toBeLessThan(300);
}

test.describe('Permit lists UX', () => {
  test.describe.configure({ mode: 'serial', timeout: 240_000 });
  const created = new CreatedPermitTypes();

  test.afterEach(async ({ page }) => {
    await created.cleanup(page);
  });

  test.afterAll(async ({ browser }, info) => {
    const baseURL = info.project.use.baseURL;
    const context = await browser.newContext(baseURL === undefined ? {} : { baseURL });
    const page = await context.newPage();
    try {
      await signIn(page);
      await setScheme(page, 'light');
    } finally {
      await context.close();
    }
  });

  test('понятний перелік: вступ, Values/Used by, вид словами, фільтр, шеврон, Enter, NAE; axe light/dark 1440/500', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    await ensureNaeTree(page.request);

    const stamp = String(Date.now());
    const code = created.track(`E2EListsUx${stamp}`);
    const method = `E2E_UxMethod${stamp}`;
    await publishType(page, code, method);

    for (const scheme of ['light', 'dark'] as const) {
      await setScheme(page, scheme);

      for (const width of [1440, 500]) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto('/admin/permits/lists');
        const where = `${scheme} @${String(width)}`;

        await expect(page.getByText('Lists give the choices for drop-down columns in permit tables. Click a list to edit its values.'), where).toBeVisible({ timeout: 30_000 });
        await expect(page.getByText(/Kept in/), `${where}: дубля «Kept in…» під назвою немає`).toHaveCount(0);
        await expect(page.locator('[data-list-table] thead').getByText('Used by', { exact: true }), where).toBeVisible();
        await expect(page.locator('[data-list-table] thead').getByText('Values', { exact: true }), where).toBeVisible();

        const row = page.locator('[data-list-table] table tbody tr', { hasText: method });
        await expect(row, where).toBeVisible({ timeout: 30_000 });
        await expect(row, `${where}: лічильник словами`).toContainText('2 values');
        await expect(row, `${where}: вид словами`).toContainText('Only for one permit type');
        await expect(row, `${where}: споживач «тип · таблиця › стовпець»`).toContainText(`E2E_ lists ux ${code} · Items › ${method}`);
        await expect(row.locator('[data-list-chevron]'), where).toHaveCount(1);
        await expect(row, where).toHaveCSS('cursor', 'pointer');

        const filter = page.getByLabel('Kinds', { exact: true });
        await expect(filter, `${where}: фільтр з підказкою «All kinds»`).toHaveAttribute('placeholder', 'All kinds');

        expect(await axe(page), `axe ${where}`).toEqual([]);

        if (scheme === 'dark') {
          await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'dark');
        }
      }
    }
    await setScheme(page, 'light');
    await page.setViewportSize({ width: 1440, height: 900 });

    // Фільтр показує лічильники видів.
    await page.goto('/admin/permits/lists');
    await page.getByLabel('Kinds', { exact: true }).click();
    await expect(page.getByRole('option', { name: /^Shared list \(\d+\)$/ })).toBeVisible();
    await expect(page.getByRole('option', { name: /^One permit type \(\d+\)$/ })).toBeVisible();
    await page.keyboard.press('Escape');

    // Клавіатура: Tab -> рядок -> Enter відкриває шторку.
    const row = page.locator('[data-list-table] table tbody tr', { hasText: method });
    await row.focus();
    await page.keyboard.press('Enter');
    const drawer = page.getByRole('dialog');
    await expect(drawer).toBeVisible({ timeout: 10_000 });
    await expect(drawer).toContainText('Alpha');
    await page.keyboard.press('Escape');
    await expect(drawer).toBeHidden({ timeout: 10_000 });

    // Рядок «NAE structure» веде на вкладку НАЕ, а не до шторки значень.
    const nae = page.locator('[data-list-table] table tbody tr', { hasText: /NAE/ }).first();
    await expect(nae).toBeVisible({ timeout: 30_000 });
    await nae.click();
    await expect(page).toHaveURL(/\/admin\/permits\/nae/, { timeout: 15_000 });
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
});
