import { expect, test, type APIRequestContext, type APIResponse, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { manySources, SmallSources, workbook, type AirSourceRow } from './airFixture';

/**
 * Air limits (NAE N3 картка + N5 майстер імпорту) - РЕАЛЬНИЙ e2e без підміни відповідей: сервер (provision, профілі, analyse/preview/apply,
 * картка) і клієнт разом, на стенді з даними, які створює сам спек і прибирає в `afterAll`.
 *
 * Дані: реєстри NAE_TREE (дерево) і LAND_AIR_SUBSTANCE (довідник речовин) - їх на свіжому стенді немає, а `POST /air-limits/provision`
 * без них відповідає 422 `sharedRegistryUnknown`; спек заводить їх звичайним API реєстрів (як `NaeTreeProvisioner`), якщо їх немає.
 * Реєстр видалити API не дає - він лишається (тимчасова база стенда), усе інше (дозволи, вузли дерева, роль і право перегляду) знімається.
 * Регіон кожного прогону свій (`E2EAIR<штамп>`), тож прогони не стикаються; книги будуються в коді (`airXlsx.ts`, без пакетів).
 *
 * Запуск (стенд - `tools/e2e-stand.ps1`; CI - workflow `e2e-stand` з `grep`): `-Grep "Air limits real"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Operator = { user: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Region = `E2EAIR${Stamp}`;
const Area = 'OFFSHORE';
const ViewerRole = `E2EAIRV${Stamp}`;
const Viewer = { user: `e2e-air-viewer-${Stamp.toLowerCase()}`, issued: 'E2E-Vi3wer-2026!', password: 'E2E-Vi3wer-Work-2026!' };
const NaeTree = 'NAE_TREE';
const Substances = 'LAND_AIR_SUBSTANCE';
/** Файл з двома НАЕ, яких немає в дереві (0090, 0091): для кроків із відсутніми НАЕ після того, як 0001-0003 уже в дереві. */
const WithMissing: readonly AirSourceRow[] = [
  ...SmallSources,
  { nae: '0090', name: 'Extra one', type: 'Т', height: 8, substances: [['0301', 'Nitrogen dioxide', 0.3, null, 0.9]] },
  { nae: '0091', name: 'Extra two', type: 'П1', height: 3, substances: [['0337', 'Carbon oxide', 0.2, null, 0.7]] },
];
const ShotsDir = process.env['ECR_E2E_SHOTS'] ?? '';
const Xlsx = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

interface Ids {
  siteId: number;
  p1: number;
  p2: number;
  p3: number;
  p1Number: string;
  p2Number: string;
}
const ids = {} as Ids;
const createdPermits: number[] = [];
let regionNodeId = 0;
let viewerUserId = 0;
let viewerRoleId = 0;

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

/** Реєстр дерева НАЕ - як `NaeTreeProvisioner` (поля, складений ключ, п'ять рівнів), якщо його немає. */
async function ensureNaeTree(api: APIRequestContext): Promise<number> {
  const existing = await api.get(`/api/v1/registries/${NaeTree}/definition`);
  if (existing.status() === 404) {
    await ok(await api.post('/api/v1/registries', { data: { code: NaeTree, nameL10n: { en: 'NAE structure' }, isTemporal: true } }), 'створення NAE_TREE');
    const fields = ['NaeCode', 'RegionCode', 'AreaCode', 'AfPath', 'SourceType', 'PointName'].map((code, index) => ({
      id: null, code, nameL10n: name(code), dataType: 'String', ordinal: index + 1, isRequired: false, isKey: index === 0,
      lookupRegistryDefId: null, unitId: null,
    }));
    await ok(
      await api.put(`/api/v1/registries/${NaeTree}/definition`, {
        headers: { 'If-Match': '1' },
        data: {
          fields, rules: [], reason: 'e2e fixture: NAE tree',
          keys: [{ id: null, code: 'NAE_BUSINESS', nameL10n: name('NAE business key'), fieldCodes: ['NaeCode', 'RegionCode', 'AreaCode'], isPrimary: false, ignoreCase: true, isActive: true }],
        },
      }),
      'опис NAE_TREE',
    );
    const levels = [['REGION', false], ['AREA', true], ['SITE', false], ['EQUIP', true], ['SRC', false]] as const;
    await ok(
      await api.put(`/api/v1/registries/${NaeTree}/hierarchy`, {
        data: { isHierarchical: true, levels: levels.map(([code, isOptional]) => ({ code, name: name(code), isOptional })) },
      }),
      'рівні NAE_TREE',
    );
  }

  return (await ok<{ id: number }>(await api.get(`/api/v1/registries/${NaeTree}/definition`), 'NAE_TREE')).id;
}

/** Довідник речовин (поле SUBSTANCE_CODE, записи E<код>), якщо його немає. */
async function ensureSubstances(api: APIRequestContext): Promise<void> {
  if ((await api.get(`/api/v1/registries/${Substances}/definition`)).status() !== 404) return;
  await ok(await api.post('/api/v1/registries', { data: { code: Substances, nameL10n: { en: 'Air substances' }, isTemporal: false } }), 'створення довідника речовин');
  await ok(
    await api.put(`/api/v1/registries/${Substances}/definition`, {
      headers: { 'If-Match': '1' },
      data: {
        fields: [{ id: null, code: 'SUBSTANCE_CODE', nameL10n: name('Code'), dataType: 'String', ordinal: 1, isRequired: true, isKey: true, lookupRegistryDefId: null, unitId: null }],
        rules: [], reason: 'e2e fixture: substances',
      },
    }),
    'опис довідника речовин',
  );
  const items = ['0301', '0337', '0330'].map((code) => ({
    clientRowId: code, op: 'upsert', id: null, code: `E${String(Number(code))}`, baseVersion: null, values: { SUBSTANCE_CODE: String(Number(code)) },
  }));
  const batch = await ok<{ applied: boolean }>(await api.post(`/api/v1/registries/${Substances}/entries/batch?dryRun=false`, { data: { items } }), 'речовини');
  expect(batch.applied, 'речовини не застосовані').toBe(true);
}

async function addNode(
  api: APIRequestContext, registryId: number, code: string, parent: number | null, level: string, values: Record<string, string>,
): Promise<number> {
  const created = await ok<{ id: number }>(
    await api.post(`/api/v1/registries/${NaeTree}/entries`, {
      data: { registryDefId: registryId, code, display: name(code), parentEntryId: parent, levelCode: level, values },
    }),
    `вузол ${code}`,
  );

  return created.id;
}

async function createPermit(api: APIRequestContext, number: string, validFrom = '2026-01-01', validTo = '2031-01-01'): Promise<number> {
  const created = await ok<{ id: number }>(
    await api.post('/api/v1/permits', { data: { typeCode: 'AIR_LIMITS', number, location: null, issueDate: null, validFrom, validTo, reportingFlags: null } }),
    `дозвіл ${number}`,
  );
  createdPermits.push(created.id);

  return created.id;
}

function importUrl(permitId: number, action: 'preview' | 'apply'): string {
  return `/api/v1/permits/${String(permitId)}/air-limits/import/${action}`;
}

/** Імпорт через API (для великих даних): preview -> усі відсутні НАЕ створити під вузлом -> apply. */
async function importViaApi(api: APIRequestContext, permitId: number, file: Buffer): Promise<{ sources: number; substances: number }> {
  const multipart = (request: object): { file: { name: string; mimeType: string; buffer: Buffer }; request: string } => ({
    file: { name: 'air.xlsx', mimeType: Xlsx, buffer: file },
    request: JSON.stringify(request),
  });
  const base = { region: Region, area: Area };
  const preview = await ok<{ preview: { missing: { kind: string; code: string }[] } }>(
    await api.post(importUrl(permitId, 'preview'), { multipart: multipart(base), timeout: 300_000 }),
    'preview через API',
  );
  const resolutions = preview.preview.missing.map((m) => ({ kind: m.kind, code: m.code, action: 'create', createUnder: String(ids.siteId) }));
  const again = await ok<{ preview: { fingerprint: string } }>(
    await api.post(importUrl(permitId, 'preview'), { multipart: multipart({ ...base, resolutions }), timeout: 300_000 }),
    'другий preview через API',
  );

  return ok(
    await api.post(importUrl(permitId, 'apply'), { multipart: multipart({ ...base, resolutions, expectedFingerprint: again.preview.fingerprint }), timeout: 300_000 }),
    'apply через API',
  );
}

async function treeNodes(api: APIRequestContext): Promise<{ id: number; parentEntryId: number | null; levelCode: string; code: string }[]> {
  const tree = await ok<{ nodes: { id: number; parentEntryId: number | null; levelCode: string; code: string }[] }>(
    await api.get(`/api/v1/registries/${NaeTree}/tree`),
    'дерево НАЕ',
  );

  return tree.nodes;
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

async function chooseFile(page: Page, file: Buffer, fileName = 'air-limits.xlsx'): Promise<void> {
  await page.locator('input[type="file"]').setInputFiles({ name: fileName, mimeType: Xlsx, buffer: file });
}

/** Select Mantine: відкрити за підписом поля й вибрати варіант за назвою. */
async function pick(page: Page, label: string | RegExp, option: string | RegExp): Promise<void> {
  await page.getByRole('textbox', typeof label === 'string' ? { name: label, exact: true } : { name: label }).click();
  await page.getByRole('option', { name: option }).click();
}

async function fillFileStep(page: Page, file: Buffer): Promise<void> {
  await chooseFile(page, file);
  // Region and area are picked from GET /air-limits/scope-codes (no free text); the area list depends on the region.
  await pick(page, /^Region/, new RegExp(`^${Region}`));
  await pick(page, /^Area/, new RegExp(`^${Area}`));
}

async function toPreview(page: Page, file: Buffer): Promise<void> {
  await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
  await fillFileStep(page, file);
  await page.getByRole('button', { name: /Next: analyse file/ }).click();
  await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 30_000 });
  await page.getByRole('button', { name: /Next: preview/ }).click();
  await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible({ timeout: 30_000 });
}

/** Нічого не лишилось у сирому вигляді: ключі каталогу й плейсхолдери не мають потрапляти на екран. */
async function expectNoRawKeys(page: Page): Promise<void> {
  const text = await page.locator('main').innerText();
  expect(text, 'на екрані сирий ключ каталогу').not.toMatch(/err\.ECR-|permits\.air\./);
  expect(text, 'на екрані нерозгорнутий плейсхолдер').not.toMatch(/\{[a-zA-Z]+\}/);
}

/** color-contrast з числами: селектор, колір тексту/тла, коефіцієнт (для діагностики, а не лише «є порушення»). */
async function contrastDetails(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(async () => {
    const axe = (window as unknown as {
      axe: { run: (c: Document, o: object) => Promise<{ violations: { nodes: { target: unknown[]; any: { data?: { fgColor?: string; bgColor?: string; contrastRatio?: number; fontSize?: string } }[] }[] }[] }> };
    }).axe;
    const result = await axe.run(document, { runOnly: { type: 'rule', values: ['color-contrast'] } });

    return result.violations.flatMap((v) =>
      v.nodes.map((n) => {
        const d = n.any[0]?.data;
        return `${String(n.target.join(' '))} fg=${d?.fgColor ?? '?'} bg=${d?.bgColor ?? '?'} ratio=${String(d?.contrastRatio ?? '?')} size=${d?.fontSize ?? '?'}`;
      }),
    );
  });
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

async function shot(page: Page, scheme: string, width: number, label: string): Promise<void> {
  if (ShotsDir === '') return;
  mkdirSync(ShotsDir, { recursive: true });
  await page.screenshot({ path: join(ShotsDir, `${label}-${scheme}-${String(width)}.png`), fullPage: true });
}

test.describe('Air limits real', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });
  let admin: APIRequestContext;
  let operator: APIRequestContext;

  test.beforeAll(async ({ playwright }, info) => {
    test.setTimeout(180_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await login(admin, Admin);
    operator = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await login(operator, Operator);

    const treeId = await ensureNaeTree(admin);
    await ensureSubstances(admin);
    await ok(await admin.post('/api/v1/air-limits/provision'), 'provision');

    regionNodeId = await addNode(admin, treeId, `${Region}_R`, null, 'REGION', { RegionCode: Region });
    const areaId = await addNode(admin, treeId, `${Region}_A`, regionNodeId, 'AREA', { RegionCode: Region, AreaCode: Area });
    ids.siteId = await addNode(admin, treeId, `${Region}_S`, areaId, 'SITE', { RegionCode: Region, AreaCode: Area });
    await addNode(admin, treeId, `${Region}_N0001`, ids.siteId, 'SRC', { NaeCode: '0001', RegionCode: Region, AreaCode: Area });

    ids.p1Number = `E2E-AIR-1-${Stamp}`;
    ids.p2Number = `E2E-AIR-2-${Stamp}`;
    ids.p1 = await createPermit(admin, ids.p1Number);
    ids.p2 = await createPermit(admin, ids.p2Number);
    ids.p3 = await createPermit(admin, `E2E-AIR-3-${Stamp}`);

    // Користувач лише з переглядом реєстрів: перевіряє режим «тільки читання».
    const role = await ok<{ id: number }>(
      await admin.post('/api/v1/roles', { data: { code: ViewerRole, nameL10n: { en: 'E2E air viewer' }, permissionCodes: ['Registry.View'] } }),
      'роль перегляду',
    );
    viewerRoleId = role.id;
    const user = await ok<{ id: number }>(
      await admin.post('/api/v1/users', {
        data: { userName: Viewer.user, provider: 'Local', sid: null, displayName: 'E2E air viewer', initialPassword: Viewer.issued, roleCodes: [ViewerRole] },
      }),
      'користувач перегляду',
    );
    viewerUserId = user.id;
    const viewerApi = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await login(viewerApi, { user: Viewer.user, password: Viewer.issued });
    await ok(await viewerApi.post('/api/v1/auth/change-password', { data: { currentPassword: Viewer.issued, newPassword: Viewer.password } }), 'пароль перегляду');
    await viewerApi.dispose();
  });

  test.afterAll(async ({ browser }) => {
    test.setTimeout(180_000);
    const problems: string[] = [];
    // Дозволи (разом з рядками таблиць) - до вузлів дерева, на які вони посилаються.
    for (const id of createdPermits) {
      const response = await admin.delete(`/api/v1/permits/${String(id)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`permit ${String(id)}: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
    }

    // Вузли регіону - листки першими.
    const nodes = await treeNodes(admin);
    const mine = new Set<number>([regionNodeId]);
    for (let grew = true; grew;) {
      grew = false;
      for (const node of nodes) {
        if (node.parentEntryId !== null && mine.has(node.parentEntryId) && !mine.has(node.id)) {
          mine.add(node.id);
          grew = true;
        }
      }
    }
    const order = ['SRC', 'EQUIP', 'SITE', 'AREA', 'REGION'];
    for (const level of order) {
      for (const node of nodes.filter((n) => mine.has(n.id) && n.levelCode === level)) {
        const response = await admin.delete(`/api/v1/registries/${NaeTree}/entries/${String(node.id)}`);
        if (response.status() >= 300 && response.status() !== 404) problems.push(`node ${node.code}: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
      }
    }

    // Користувач лишається без ролей (видалити його API не дає), роль видаляється.
    if (viewerUserId !== 0) await admin.put(`/api/v1/users/${String(viewerUserId)}/roles`, { data: { roleCodes: [] } });
    if (viewerRoleId !== 0) {
      const response = await admin.delete(`/api/v1/roles/${String(viewerRoleId)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`role: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
    }

    // Тема адміністратора назад на light: вподобання зберігається на сервері і перекриває localStorage.
    const reset = await openAs(browser, Admin, 'light');
    await reset.context.close();
    await admin.dispose();
    await operator.dispose();
    expect(problems, 'прибирання стенда').toEqual([]);
  });

  test('Air limits real: provisioned type, profile list and an empty card', async ({ page }) => {
    const profiles = await ok<{ code: string; builtIn: boolean }[]>(await admin.get('/api/v1/air-limits/import-profiles'), 'профілі');
    expect(profiles.some((p) => p.code === 'ERA_TABLE_33' && p.builtIn)).toBe(true);

    await signIn(page, Admin);
    await page.goto(`/admin/permits/${String(ids.p1)}?tab=air`);
    await expect(page.getByRole('tab', { name: 'Air limits', selected: true })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('heading', { name: 'Emission sources' })).toBeVisible();
    await expect(page.getByTestId('air-counts')).toHaveText('0 sources · 0 substance rows');
    await expect(page.getByText('No emission sources yet')).toBeVisible();
    await expect(page.getByRole('link', { name: /Import from Excel/ })).toBeVisible();
    await expectNoRawKeys(page);
  });

  test('Air limits real: wizard end to end - mapping, issues, missing NAE, apply, counts, card', async ({ page }) => {
    await signIn(page, Admin);
    await page.goto(`/admin/permits/${String(ids.p1)}?tab=air`);
    await page.getByRole('link', { name: /Import from Excel/ }).click();
    await expect(page).toHaveURL(new RegExp(`/admin/permits/${String(ids.p1)}/import`));
    const file = workbook(SmallSources);

    // 1 File -> 2 Mapping
    await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('combobox', { name: /Import profile/ }).or(page.getByRole('textbox', { name: /Import profile/ }))).toHaveValue(/ERA table 3\.3/);
    await page.getByRole('button', { name: /Next: analyse file/ }).click();
    await expect(page.getByText('Choose a workbook.')).toBeVisible();
    // Region/area: pickers fed by GET /air-limits/scope-codes; the area depends on the region.
    await chooseFile(page, file);
    await expect(page.getByRole('textbox', { name: /^Area/ })).toBeDisabled();
    await page.getByRole('button', { name: /Next: analyse file/ }).click();
    await expect(page.getByText('Choose the region.')).toBeVisible();
    await page.getByRole('textbox', { name: /^Region/ }).click();
    await expect(page.getByRole('option', { name: new RegExp(`^${Region}`) })).toBeVisible();
    await page.keyboard.press('Escape');
    await pick(page, /^Region/, new RegExp(`^${Region}`));
    await page.getByRole('button', { name: /Next: analyse file/ }).click();
    await expect(page.getByText('Choose the area.')).toBeVisible();
    await page.getByRole('textbox', { name: /^Area/ }).click();
    // Areas of this region only. The REGION group node carries RegionCode without AreaCode, so the server also offers "-" (No area).
    await expect(page.getByRole('option')).toHaveText(['No area', Area]);
    await page.keyboard.press('Escape');
    await pick(page, /^Area/, new RegExp(`^${Area}`));
    await page.getByRole('button', { name: /Next: analyse file/ }).click();
    await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 30_000 });
    const matched = page.getByRole('table').first();
    await expect(matched).toContainText('naeCode');
    await expect(matched).toContainText('tonsPerYear');
    await expect(page.getByText(/Header rows 2–3 · data from row 5/)).toBeVisible();
    await expectNoRawKeys(page);

    // 3 Preview: errors for the two NAE codes that are not in the tree, a warning for the empty t/year cell.
    await page.getByRole('button', { name: /Next: preview/ }).click();
    await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('heading', { name: /NAE codes missing in the tree: 2/ })).toBeVisible();
    const issues = page.getByRole('region', { name: 'Import findings' });
    await expect(issues.getByRole('row', { name: /Warning\s+6\s+I\s+tonsPerYear/ })).toBeVisible();
    await expect(issues.getByRole('row', { name: /Error\s+7\s+B\s+naeCode/ })).toBeVisible();
    await expect(issues.getByRole('row', { name: /Error\s+8\s+B\s+naeCode/ })).toBeVisible();
    await expectNoRawKeys(page);
    const apply = page.getByRole('button', { name: /Apply import/ });
    await expect(apply).toBeDisabled();

    // Решення: 0002 створити під площадкою, 0003 пропустити.
    await pick(page, 'Action for NAE 0002', 'Create under a node');
    await pick(page, 'Node to create NAE 0002 under', new RegExp(`${Region}_S$`));
    await pick(page, 'Action for NAE 0003', 'Skip this NAE');
    await expect(page.getByText('The preview is out of date').first()).toBeVisible();
    await expect(apply).toBeDisabled();
    await page.getByRole('button', { name: 'Run preview again' }).click();
    await expect(page.getByText('The preview is out of date')).toHaveCount(0, { timeout: 30_000 });
    await expect(page.getByRole('heading', { name: /NAE codes missing in the tree/ })).toBeVisible();
    await expect(issues.getByRole('row', { name: /Error/ })).toHaveCount(0);
    await expect(issues.getByRole('row', { name: /Warning\s+6\s+I\s+tonsPerYear/ })).toBeVisible();
    await expect(apply).toBeEnabled();

    // 4 Apply -> result
    await apply.click();
    await expect(page.getByRole('heading', { name: 'Result', level: 2 })).toBeVisible({ timeout: 60_000 });
    const result = page.getByTestId('air-import-result');
    await expect(result.getByText('Sources in the permit').locator('b')).toHaveText('2');
    await expect(result.getByText('Substance rows inserted').locator('b')).toHaveText('3');
    await expect(result.getByText('NAE created in the tree').locator('b')).toHaveText('1');
    await expectNoRawKeys(page);

    // Card shows the data.
    await page.getByRole('link', { name: 'Open the permit card' }).click();
    await expect(page.getByTestId('air-counts')).toHaveText('2 sources · 3 substance rows', { timeout: 30_000 });
    const rows = page.getByTestId('air-source-row');
    await expect(rows).toHaveCount(2);
    await expect(rows.first()).toContainText('0001');
    await expect(rows.first()).toContainText('Boiler stack');
    await page.getByRole('button', { name: 'Substances of source 0001' }).click();
    const sub = page.getByRole('table', { name: 'Substances of source 0001' });
    await expect(sub).toContainText('0301');
    // Числа з повною шкалою сервера (0.5000000000000000) показані без нулів хвоста й без округлення; назва речовини - з довідника.
    const values = async (row: RegExp): Promise<string[]> => (await sub.getByRole('row', { name: row }).getByRole('cell').allInnerTexts()).slice(2);
    expect(await values(/0301/)).toEqual(['0.5', '25', '1.2']);
    expect(await values(/0337/)).toEqual(['0.25', '—', '—']); // порожнє t/year - прочерк, не 0
    expect(await sub.innerText(), 'нулі хвоста десяткових').not.toMatch(/\d\.\d*0{4,}/);
    await page.locator('label', { hasText: /^Totals$/ }).click();
    const totals = page.getByRole('table', { name: /Totals per substance|Substance/ }).or(page.getByRole('region', { name: 'Totals per substance' }));
    await expect(totals.first()).toContainText('0330');

    // Server truth: the same numbers through the API; ownerKey, leaf level and created node.
    const card = await ok<{ sources: { naeCode: string; sourceType: string | null; substances: unknown[] }[]; substanceRows: number; totals: unknown[] }>(
      await admin.get(`/api/v1/permits/${String(ids.p1)}/air-limits`),
      'картка',
    );
    expect(card.sources.map((s) => s.naeCode).sort()).toEqual(['0001', '0002']);
    expect(card.substanceRows).toBe(3);
    expect(card.sources.find((s) => s.naeCode === '0001')?.sourceType).toBe('T');
    const created = (await treeNodes(admin)).find((n) => n.code !== undefined && n.parentEntryId === ids.siteId && n.levelCode === 'SRC' && !n.code.endsWith('N0001'));
    expect(created, 'створений вузол 0002 під площадкою рівня SRC').toBeDefined();
  });

  test('Air limits real: rows of another permit need an explicit confirmation', async ({ page }) => {
    await signIn(page, Admin);
    await page.goto(`/admin/permits/${String(ids.p2)}/import`);
    await toPreview(page, workbook(SmallSources));
    // 0001 і 0002 уже в дереві; лишився 0003 - пропускаємо.
    await expect(page.getByRole('heading', { name: /NAE codes missing in the tree: 1/ })).toBeVisible();
    await pick(page, 'Action for NAE 0003', 'Skip this NAE');
    await page.getByRole('button', { name: 'Run preview again' }).click();
    await expect(page.getByText('The preview is out of date')).toHaveCount(0, { timeout: 30_000 });

    const others = page.getByRole('group', { name: /OTHER permits/ });
    await expect(others).toBeVisible();
    await expect(others).toContainText(ids.p1Number); // owner label from the server, not the raw ownerKey
    await expect(others).toContainText('2 NAE · 3 rows');
    const apply = page.getByRole('button', { name: /Apply import/ });
    await expect(apply).toBeDisabled();
    await expect(page.getByText('Confirm the replacement of the other permits’ rows.')).toBeVisible();
    await others.getByRole('checkbox').check();
    await expect(apply).toBeEnabled();
    await apply.click();

    await expect(page.getByRole('heading', { name: 'Result', level: 2 })).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText('Rows of other permits that were replaced')).toBeVisible();
    await expect(page.locator('main')).toContainText(`${ids.p1Number}: 2 NAE · 3 rows`);
    const first = await ok<{ sources: unknown[] }>(await admin.get(`/api/v1/permits/${String(ids.p1)}/air-limits`), 'картка P1');
    const second = await ok<{ sources: unknown[] }>(await admin.get(`/api/v1/permits/${String(ids.p2)}/air-limits`), 'картка P2');
    expect(first.sources).toHaveLength(0);
    expect(second.sources).toHaveLength(2);
  });

  test('Air limits real: a plan that changed after the preview is refused and needs a new preview', async ({ page }) => {
    await signIn(page, Admin);
    await page.goto(`/admin/permits/${String(ids.p2)}/import`);
    await toPreview(page, workbook(SmallSources));
    await pick(page, 'Action for NAE 0003', 'Skip this NAE');
    await page.getByRole('button', { name: 'Run preview again' }).click();
    await expect(page.getByText('The preview is out of date')).toHaveCount(0, { timeout: 30_000 });
    const apply = page.getByRole('button', { name: /Apply import/ });
    await expect(apply).toBeEnabled();

    // Хтось інший додає НАЕ 0003 в дерево: фактичний план змінився, відбиток переглянутого - ні.
    const tree = await ok<{ id: number }>(await admin.get(`/api/v1/registries/${NaeTree}/definition`), 'NAE_TREE');
    await addNode(admin, tree.id, `${Region}_N0003`, ids.siteId, 'SRC', { NaeCode: '0003', RegionCode: Region, AreaCode: Area });

    await apply.click();
    await expect(page.getByRole('alert').first()).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible();
    await expect(page.getByText('The preview is out of date').first()).toBeVisible();
    await expect(apply).toBeDisabled();
    await expectNoRawKeys(page);

    await page.getByRole('button', { name: 'Run preview again' }).click();
    await expect(page.getByText('The preview is out of date')).toHaveCount(0, { timeout: 30_000 });
    await expect(page.getByRole('heading', { name: /NAE codes missing in the tree/ })).toHaveCount(0);
    await expect(apply).toBeEnabled();
    await apply.click();
    await expect(page.getByRole('heading', { name: 'Result', level: 2 })).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId('air-import-result').getByText('Sources in the permit').locator('b')).toHaveText('3');
  });

  test('Air limits real: read-only viewer and a user without the register right', async ({ browser }) => {
    const viewer = await openAs(browser, { user: Viewer.user, password: Viewer.password }, 'light');
    try {
      const page = viewer.page;
      await page.goto(`/admin/permits/${String(ids.p2)}?tab=air`);
      await expect(page.getByTestId('air-counts')).toHaveText('3 sources · 4 substance rows', { timeout: 30_000 });
      await expect(page.getByRole('link', { name: /Import from Excel/ })).toHaveCount(0);
      await page.goto(`/admin/permits/${String(ids.p2)}/import`);
      await expect(page.getByText('View only')).toBeVisible({ timeout: 30_000 });
      await expect(page.getByRole('button', { name: /Next: analyse file/ })).toBeDisabled();

      // Сервер теж не пускає: analyse без права EditData -> 403.
      const denied = await viewer.context.request.post('/api/v1/air-limits/import/analyse', {
        multipart: { file: { name: 'a.xlsx', mimeType: Xlsx, buffer: workbook(SmallSources) }, request: '{}' },
      });
      expect(denied.status()).toBe(403);
    } finally {
      await viewer.context.close();
    }

    // Оператор (немає Registry.View): картка недоступна й на сервері.
    expect((await operator.get(`/api/v1/permits/${String(ids.p2)}/air-limits`)).status()).toBe(403);
    const op = await openAs(browser, Operator, 'light');
    try {
      await op.page.goto(`/admin/permits/${String(ids.p2)}?tab=air`);
      await expect(op.page.getByRole('button', { name: /Import from Excel/ })).toHaveCount(0);
      await expect(op.page.getByRole('link', { name: /Import from Excel/ })).toHaveCount(0);
      await expect(op.page.getByTestId('air-counts')).toHaveCount(0);
    } finally {
      await op.context.close();
    }
  });

  test('Air limits real: a card with hundreds of sources stays usable', async ({ page }) => {
    const imported = await importViaApi(admin, ids.p3, workbook(manySources(300, 1000)));
    expect(imported.sources).toBe(300);
    expect(imported.substances).toBe(600);

    await signIn(page, Admin);
    await page.goto(`/admin/permits/${String(ids.p3)}?tab=air`);
    await expect(page.getByTestId('air-counts')).toHaveText('300 sources · 600 substance rows', { timeout: 60_000 });
    const rows = page.getByTestId('air-source-row');
    await expect(rows).toHaveCount(300);
    const heights = await rows.evaluateAll((list) => list.slice(0, 20).map((row) => row.getBoundingClientRect().height));
    expect(Math.max(...heights), `висота рядків: ${heights.join(', ')}`).toBeLessThanOrEqual(56);
    const region = page.getByRole('region', { name: 'Emission sources table' });
    const scrolls = await region.evaluate((element) => ({ client: element.clientHeight, scroll: element.scrollHeight }));
    expect(scrolls.scroll, 'таблиця прокручується всередині власної області').toBeGreaterThan(scrolls.client);
    expect(scrolls.client).toBeLessThanOrEqual(900);
    // Рядок далеко в списку розкривається, і речовини видно.
    await page.getByRole('button', { name: 'Substances of source 1299' }).scrollIntoViewIfNeeded();
    await page.getByRole('button', { name: 'Substances of source 1299' }).click();
    await expect(page.getByRole('table', { name: 'Substances of source 1299' })).toContainText('0301');
    // Фільтр.
    await page.getByRole('textbox', { name: 'Filter sources' }).fill('1150');
    await expect(rows).toHaveCount(1);
    const started = Date.now();
    await page.getByRole('textbox', { name: 'Filter sources' }).fill('');
    await expect(rows).toHaveCount(300);
    expect(Date.now() - started, 'повернення повного списку').toBeLessThan(5_000);
  });

  for (const scheme of ['light', 'dark'] as const) {
    for (const width of [1440, 500]) {
      test(`Air limits real: contrast and screenshots - ${scheme} ${String(width)}`, async ({ browser }) => {
        const { page, context } = await openAs(browser, Admin, scheme, width);
        try {
          const clean = async (label: string): Promise<void> => {
            await expectNoRawKeys(page);
            const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
            expect.soft(overflow, `${label}: сторінка з горизонтальним переповненням`).toBeLessThanOrEqual(1);
            const colour = await contrastDetails(page);
            expect.soft(colour, `${label}: контраст ${scheme} ${String(width)}`).toEqual([]);
            const all = await axeAll(page);
            expect.soft(all, `${label}: axe wcag2a/aa ${scheme} ${String(width)}`).toEqual([]);
            await shot(page, scheme, width, label);
          };

          await page.goto(`/admin/permits/${String(ids.p2)}?tab=air`);
          await expect(page.getByTestId('air-counts')).toBeVisible({ timeout: 30_000 });
          await clean('card');
          await page.getByRole('button', { name: 'Substances of source 0001' }).click();
          await expect(page.getByRole('table', { name: 'Substances of source 0001' })).toBeVisible();
          await clean('card-expanded');
          await page.locator('label', { hasText: /^Totals$/ }).click();
          await expect(page.getByRole('region', { name: 'Totals per substance' })).toBeVisible();
          await clean('card-totals');

          await page.goto(`/admin/permits/${String(ids.p2)}/import`);
          await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
          await clean('wizard-file');
          const file = workbook(WithMissing);
          await fillFileStep(page, file);
          await page.getByRole('button', { name: /Next: analyse file/ }).click();
          await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 30_000 });
          await clean('wizard-mapping');
          await page.getByRole('button', { name: 'Edit mapping' }).click();
          await expect(page.getByRole('region', { name: 'Mapping editor' })).toBeVisible();
          await clean('wizard-mapping-editor');
          await page.getByRole('button', { name: 'Discard changes' }).click();
          await page.getByRole('button', { name: /Next: preview/ }).click();
          await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible({ timeout: 30_000 });
          await clean('wizard-preview');
          await pick(page, 'Action for NAE 0091', 'Create under a node');
          await clean('wizard-preview-create');
          await pick(page, 'Action for NAE 0091', 'Skip this NAE');
          await pick(page, 'Action for NAE 0090', 'Skip this NAE');
          await page.getByRole('button', { name: 'Run preview again' }).click();
          await expect(page.getByText('The preview is out of date')).toHaveCount(0, { timeout: 30_000 });
          await clean('wizard-preview-resolved');
        } finally {
          await context.close();
        }
      });
    }
  }

  test('Air limits real: keyboard path through the wizard and focus on every step change', async ({ page }) => {
    await signIn(page, Admin);
    await page.goto(`/admin/permits/${String(ids.p2)}/import`);
    await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
    const focusedName = async (): Promise<string> =>
      page.evaluate(() => {
        const el = document.activeElement as HTMLElement | null;
        return `${el?.tagName ?? ''}|${el?.getAttribute('aria-label') ?? ''}|${(el?.innerText ?? '').slice(0, 60)}`;
      });

    // Файл: кнопка вибору файлу відкривається з клавіатури.
    const picker = page.getByRole('button', { name: /Excel workbook/ });
    await picker.focus();
    const chooser = page.waitForEvent('filechooser');
    await page.keyboard.press('Enter');
    await (await chooser).setFiles({ name: 'air.xlsx', mimeType: Xlsx, buffer: workbook(WithMissing) });
    // Регіон: пошук у списку - друкуємо, стрілка вниз до першого збігу, Enter; ділянка - стрілка, Enter.
    const regionInput = page.getByRole('textbox', { name: /^Region/ });
    await regionInput.focus();
    await page.keyboard.type(Region);
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    await expect(regionInput).toHaveValue(new RegExp(`^${Region}`));
    await page.keyboard.press('Tab');
    await page.keyboard.press('ArrowDown'); // opens the list on "No area" ("-" sorts first)
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    await expect(page.getByRole('textbox', { name: /^Area/ })).toHaveValue(new RegExp(`^${Area}`));
    // До «Next» - Tab-ами, без миші; кільце фокуса видно.
    const next = page.getByRole('button', { name: /Next: analyse file/ });
    let steps = 0;
    while (!(await next.evaluate((el) => el === document.activeElement)) && steps < 12) {
      await page.keyboard.press('Tab');
      steps += 1;
    }
    expect(await next.evaluate((el) => el === document.activeElement), `Next недосяжна Tab-ами за ${String(steps)} кроків; фокус: ${await focusedName()}`).toBe(true);
    const ring = await next.evaluate((el) => {
      const style = getComputedStyle(el);
      return { outline: style.outlineStyle, width: style.outlineWidth, shadow: style.boxShadow };
    });
    expect(ring.outline !== 'none' || ring.shadow !== 'none', `кільце фокуса на Next: ${JSON.stringify(ring)}`).toBe(true);
    await page.keyboard.press('Enter');

    // Крок змінився: фокус на заголовку кроку.
    await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeFocused({ timeout: 30_000 });
    await page.getByRole('button', { name: /Next: preview/ }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeFocused({ timeout: 30_000 });
    // Рішення про відсутнє НАЕ - клавіатурою: Select відкривається стрілкою, вибір - Enter.
    const action = page.getByRole('textbox', { name: 'Action for NAE 0091', exact: true });
    await action.focus();
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    await expect(action).toHaveValue('Skip this NAE');
    // «Back» повертає на попередній крок і теж ставить фокус на заголовок.
    await page.getByRole('button', { name: 'Back' }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeFocused();
  });
});
