import { expect, test, type APIRequestContext, type APIResponse, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { buildXlsx, type Cell } from './airXlsx';

/**
 * NAE tree manual - РЕАЛЬНИЙ e2e структури НВ без PI AF: «Add region», «Create regions from registries» (лише для порожнього дерева),
 * імпорт дерева з Excel (шаблон -> перегляд -> застосування), попередження про відмінні назви, axe light+dark на 1440 і 500.
 *
 * Дані: довідник регіонів `LAND_REGION` (запис свого регіону створює спек, якщо довідника немає - створює й довідник) і вузли дерева
 * `NAE_TREE`, яких не було на старті; усе, що спек додав, знімається в `afterAll` (листки першими), роль і тимчасовий користувач -
 * теж (користувач лишається без ролей), тема адміністратора повертається на light.
 *
 * ⚠ Припущення (серверна реалізація лінії `integ-nae-tree-manual` на момент написання - заглушки): регіон кореня - КОД запису
 * `LAND_REGION`, шаблон імпорту - аркуш `NAE` зі стовпцями `Region, Area, Site, Equipment, NAE code, Name_En, Name_Ru, Name_Kz, On_Offshore`.
 *
 * Запуск (стенд - `tools/e2e-stand.ps1`; CI - workflow `e2e-stand` з `grep`): `-Grep "NAE tree manual"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Region = `E2ENAE${Stamp}`;
const Area = 'OFFSHORE';
const Digits = String(Date.now()).slice(-6);
const RoleCode = `E2ENAEM${Stamp}`;
const Editor = { user: `e2e-nae-manual-${Stamp.toLowerCase()}`, issued: 'E2E-Ed1tor-2026!', password: 'E2E-Ed1tor-Work-2026!' };
const NaeTree = 'NAE_TREE';
const LandRegion = 'LAND_REGION';
const Xlsx = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
const Header: readonly Cell[] = ['Region', 'Area', 'Site', 'Equipment', 'NAE code', 'Name_En', 'Name_Ru', 'Name_Kz', 'On_Offshore'];
const SiteName = `E2E Site ${Stamp}`;

interface TreeNode {
  id: number;
  parentEntryId: number | null;
  levelCode: string;
  code: string;
  display: string;
}
let initialIds = new Set<number>();
let initialEmpty = false;
let landRegionEntryCreated = false;
let roleId = 0;
let userId = 0;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function login(api: APIRequestContext, user: { user: string; password: string }): Promise<void> {
  const response = await api.post('/api/v1/login/local', { data: { userName: user.user, password: user.password } });
  expect(response.ok(), `вхід ${user.user}: ${String(response.status())}`).toBe(true);
}

function name(code: string): { values: Record<string, string> } {
  return { values: { en: code } };
}

async function treeNodes(api: APIRequestContext): Promise<TreeNode[]> {
  const response = await api.get(`/api/v1/registries/${NaeTree}/tree`);
  if (response.status() === 404) return [];

  return (await ok<{ nodes: TreeNode[] }>(response, 'дерево НВ')).nodes;
}

/** Довідник регіонів і запис власного регіону (код = `Region`), якщо їх немає. */
async function ensureLandRegion(api: APIRequestContext): Promise<void> {
  if ((await api.get(`/api/v1/registries/${LandRegion}/definition`)).status() === 404) {
    await ok(await api.post('/api/v1/registries', { data: { code: LandRegion, nameL10n: { en: 'Regions' }, isTemporal: false } }), 'створення LAND_REGION');
    await ok(
      await api.put(`/api/v1/registries/${LandRegion}/definition`, {
        headers: { 'If-Match': '1' },
        data: {
          fields: [{ id: null, code: 'REGION_CODE', nameL10n: name('Code'), dataType: 'String', ordinal: 1, isRequired: true, isKey: true, lookupRegistryDefId: null, unitId: null }],
          rules: [], reason: 'e2e fixture: regions',
        },
      }),
      'опис LAND_REGION',
    );
  }
  const batch = await ok<{ applied: boolean }>(
    await api.post(`/api/v1/registries/${LandRegion}/entries/batch?dryRun=false`, {
      data: { items: [{ clientRowId: Region, op: 'upsert', id: null, code: Region, baseVersion: null, values: { REGION_CODE: Region } }] },
    }),
    'регіон e2e',
  );
  expect(batch.applied, 'регіон e2e не застосований').toBe(true);
  landRegionEntryCreated = true;
}

async function signIn(page: Page, user: { user: string; password: string }): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Тема `scheme` на `<html>`: сервер зберігає вподобання і перекриває localStorage, тож перемикаємо через меню користувача. */
async function ensureScheme(page: Page, userName: string, scheme: 'light' | 'dark'): Promise<void> {
  const html = page.locator('html');
  await expect(page.getByRole('button', { name: new RegExp(userName, 'i') }).first()).toBeVisible({ timeout: 30_000 });
  if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
    await page.getByRole('button', { name: new RegExp(userName, 'i') }).first().click();
    await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
    await page.keyboard.press('Escape');
  }
  await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

async function openAs(
  browser: Browser, user: { user: string; password: string }, scheme: 'light' | 'dark', width = 1440,
): Promise<{ page: Page; context: BrowserContext }> {
  const context = await browser.newContext({ viewport: { width, height: 900 } });
  const page = await context.newPage();
  await page.emulateMedia({ colorScheme: scheme });
  await signIn(page, user);
  await ensureScheme(page, user.user, scheme);
  await page.reload();
  await ensureScheme(page, user.user, scheme);

  return { page, context };
}

async function axeAll(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(async () => {
    const axe = (window as unknown as {
      axe: { run: (c: Document, o: object) => Promise<{ violations: { id: string; nodes: { target: unknown[] }[] }[] }> };
    }).axe;
    const result = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } });

    return result.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${String(n.target.join(' '))}`));
  });
}

function workbookFor(names: readonly [string, string]): Buffer {
  const row = (suffix: string, pump: string): Cell[] => [Region, Area, SiteName, 'Pumps', `${Digits}${suffix}`, pump, null, null, null];

  return buildXlsx([Header, row('1', names[0]), row('2', names[1])], 'NAE');
}

async function chooseFile(page: Page, file: Buffer): Promise<void> {
  await page.locator('input[type="file"]').setInputFiles({ name: 'nae-tree.xlsx', mimeType: Xlsx, buffer: file });
}

async function openExcel(page: Page): Promise<void> {
  await page.goto('/admin/permits/nae');
  await page.locator('[data-nae-excel-open]').click();
  await expect(page.locator('[data-nae-excel]')).toBeVisible();
}

test.describe('NAE tree manual', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });
  let admin: APIRequestContext;

  test.beforeAll(async ({ playwright }, info) => {
    test.setTimeout(180_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await login(admin, Admin);
    await ensureLandRegion(admin);

    const before = await treeNodes(admin);
    initialIds = new Set(before.map((node) => node.id));
    initialEmpty = before.length === 0;

    // Тимчасова роль і користувач: редагування даних і опису реєстрів (створення довідника НВ, якщо його ще немає).
    const role = await ok<{ id: number }>(
      await admin.post('/api/v1/roles', {
        data: { code: RoleCode, nameL10n: { en: 'E2E NAE manual' }, permissionCodes: ['Registry.View', 'Registry.EditData', 'Registry.EditDefinition', 'Registry.Publish'] },
      }),
      'роль',
    );
    roleId = role.id;
    const user = await ok<{ id: number }>(
      await admin.post('/api/v1/users', {
        data: { userName: Editor.user, provider: 'Local', sid: null, displayName: 'E2E NAE manual', initialPassword: Editor.issued, roleCodes: [RoleCode] },
      }),
      'користувач',
    );
    userId = user.id;
    const editorApi = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await login(editorApi, { user: Editor.user, password: Editor.issued });
    await ok(await editorApi.post('/api/v1/auth/change-password', { data: { currentPassword: Editor.issued, newPassword: Editor.password } }), 'пароль');
    await editorApi.dispose();
  });

  test.afterAll(async ({ browser }) => {
    test.setTimeout(180_000);
    const problems: string[] = [];

    // Вузли, яких не було на старті: найглибші першими.
    const nodes = await treeNodes(admin);
    const mine = nodes.filter((node) => !initialIds.has(node.id));
    const parent = new Map(nodes.map((node) => [node.id, node.parentEntryId]));
    const depth = (id: number): number => {
      let d = 0;
      for (let at = parent.get(id) ?? null; at !== null; at = parent.get(at) ?? null) d += 1;

      return d;
    };
    for (const node of [...mine].sort((a, b) => depth(b.id) - depth(a.id))) {
      const response = await admin.delete(`/api/v1/registries/${NaeTree}/entries/${String(node.id)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`node ${node.code}: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
    }

    // Запис власного регіону.
    if (landRegionEntryCreated) {
      const rows = await admin.get(`/api/v1/registries/${LandRegion}/rows?q=${Region}&limit=50`);
      const found = rows.ok() ? ((await rows.json()) as { items: { id: number; code: string }[] }).items.find((row) => row.code === Region) : undefined;
      if (found !== undefined) {
        const response = await admin.delete(`/api/v1/registries/${LandRegion}/entries/${String(found.id)}`);
        if (response.status() >= 300 && response.status() !== 404) problems.push(`region: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
      }
    }

    // Користувач лишається без ролей (видалити його API не дає), роль видаляється.
    if (userId !== 0) await admin.put(`/api/v1/users/${String(userId)}/roles`, { data: { roleCodes: [] } });
    if (roleId !== 0) {
      const response = await admin.delete(`/api/v1/roles/${String(roleId)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`role: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
    }

    // Тема адміністратора назад на light: вподобання зберігається на сервері і перекриває localStorage.
    const reset = await openAs(browser, Admin, 'light');
    await reset.context.close();
    await admin.dispose();
    expect(problems, 'прибирання стенда').toEqual([]);
  });

  test('NAE tree manual: seed regions from registries only while the tree is empty', async ({ browser }) => {
    const { page, context } = await openAs(browser, { user: Editor.user, password: Editor.password }, 'light');
    await page.goto('/admin/permits/nae');
    if (initialEmpty) {
      await expect(page.getByText('The NAE tree is empty')).toBeVisible();
      for (const way of [/Load from PI AF/, /Import from Excel:/, /By hand/]) await expect(page.locator('[data-nae-empty]').getByText(way)).toBeVisible();
      const seeded = page.waitForResponse((response) => response.url().includes('/nae-tree/seed-roots'));
      await page.locator('[data-nae-seed-roots]').click();
      const seedResponse = await seeded;
      expect(seedResponse.status(), `seed-roots: ${(await seedResponse.text()).slice(0, 800)}`).toBeLessThan(300);
      // Корені створюються з усіх живих записів LAND_REGION: порожній стан зникає, у дереві з'являються вузли REGION.
      await expect
        .poll(async () => (await treeNodes(admin)).filter((node) => node.levelCode === 'REGION' && node.parentEntryId === null).length, {
          message: 'корені регіонів', timeout: 30_000,
        })
        .toBeGreaterThan(0);
      await expect(page.locator('[data-nae-empty]')).toHaveCount(0, { timeout: 30_000 });
    } else {
      // Дерево не порожнє: пропозиції немає, а сервер відмовляє 422.
      await expect(page.locator('[data-nae-add-region-open]')).toBeVisible();
      await expect(page.locator('[data-nae-seed-roots]')).toHaveCount(0);
      expect((await admin.post('/api/v1/nae-tree/seed-roots')).status()).toBe(422);
    }
    await context.close();
  });

  test('NAE tree manual: add region', async ({ browser }) => {
    const { page, context } = await openAs(browser, { user: Editor.user, password: Editor.password }, 'light');
    await page.goto('/admin/permits/nae');
    await page.locator('[data-nae-add-region-open]').click();
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel(/^Region/).selectOption(Region);
    // Район - select зі списку AIR_AREA (OFFSHORE/ONSHORE), не текстове поле (людина 04.11).
    await dialog.getByLabel(/^Area/).selectOption(Area);
    await dialog.getByLabel('Name (English)').fill(`E2E NAE ${Stamp}`);
    await dialog.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(dialog).toBeHidden({ timeout: 30_000 });

    const nodes = await treeNodes(admin);
    expect(nodes.some((node) => node.levelCode === 'REGION' && node.display.includes(Region)), 'вузол регіону').toBe(true);
    expect(nodes.some((node) => node.levelCode === 'AREA' && node.display.includes(`E2E NAE ${Stamp}`)), 'вузол району з назвою').toBe(true);
    await context.close();
  });

  test('NAE tree manual: import from Excel - template, preview, apply, warnings', async ({ browser }) => {
    const { page, context } = await openAs(browser, { user: Editor.user, password: Editor.password }, 'light');
    await openExcel(page);

    // Шаблон завантажується файлом .xlsx.
    const download = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Download template' }).click();
    expect((await download).suggestedFilename()).toMatch(/\.xlsx$/);

    await expect(page.locator('[data-nae-excel-apply]')).toBeDisabled();
    await chooseFile(page, workbookFor([`Pump ${Digits}1`, `Pump ${Digits}2`]));
    await page.getByRole('button', { name: 'Preview', exact: true }).click();
    await expect(page.locator('[data-nae-excel-report="preview"]')).toBeVisible({ timeout: 30_000 });
    await expect(page.locator('[data-nae-excel-issues="error"]')).toHaveCount(0);
    await expect(page.locator('[data-nae-excel-apply]')).toBeEnabled();

    await page.locator('[data-nae-excel-apply]').click();
    await expect(page.locator('[data-nae-excel-report="applied"]')).toBeVisible({ timeout: 60_000 });
    const nodes = await treeNodes(admin);
    for (const pump of [`Pump ${Digits}1`, `Pump ${Digits}2`]) {
      expect(nodes.some((node) => node.display.includes(pump)), `вузол ${pump}`).toBe(true);
    }
    await expect(page.getByText(Region).first()).toBeVisible();

    // Повторний перегляд того ж файлу: нічого нового. Інші назви - попередження (назви не перезаписуються).
    await chooseFile(page, workbookFor([`Pump ${Digits}1`, `Pump ${Digits}2`]));
    await page.getByRole('button', { name: 'Preview', exact: true }).click();
    await expect(page.getByText(/Nothing new/)).toBeVisible({ timeout: 30_000 });
    await expect(page.locator('[data-nae-excel-apply]')).toBeDisabled();

    await chooseFile(page, workbookFor([`Pump ${Digits}1 renamed`, `Pump ${Digits}2`]));
    await page.getByRole('button', { name: 'Preview', exact: true }).click();
    await expect(page.locator('[data-nae-excel-issues="warning"]')).toBeVisible({ timeout: 30_000 });
    expect(await page.locator('[data-nae-excel-issues="warning"]').innerText()).toContain('Names are not overwritten');
    await context.close();
  });

  for (const scheme of ['light', 'dark'] as const) {
    for (const width of [1440, 500]) {
      test(`NAE tree manual: axe ${scheme} ${String(width)}`, async ({ browser }) => {
        const { page, context } = await openAs(browser, { user: Editor.user, password: Editor.password }, scheme, width);
        if (scheme === 'dark') await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'dark');
        await openExcel(page);
        await chooseFile(page, workbookFor([`Pump ${Digits}1`, `Pump ${Digits}2`]));
        await page.getByRole('button', { name: 'Preview', exact: true }).click();
        await expect(page.locator('[data-nae-excel-report]')).toBeVisible({ timeout: 30_000 });
        expect(await axeAll(page), `axe ${scheme} ${String(width)}: імпорт з Excel`).toEqual([]);

        await page.locator('[data-nae-add-region-open]').click();
        await expect(page.getByRole('dialog')).toBeVisible();
        expect(await axeAll(page), `axe ${scheme} ${String(width)}: Add region`).toEqual([]);
        await context.close();
      });
    }
  }
});
