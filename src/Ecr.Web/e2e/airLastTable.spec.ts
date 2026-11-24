import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';
import { build401, type Shape401Source } from './air401Shape';
import { ensureNaeTree, ensureSubstances, fillScope, prepareScope, removeScopeEntries, type PreparedScope } from './airScope';

/**
 * Air last table (D-569, P-1123-1, людина 23.11: «кастомні колонки які я додаю, в моєму прикладі Цех, не додаються … загальне
 * правило одна остання завантажена таблиця актуальна») — the REAL wizard, as the human did it on live:
 *  1. a file of the 401 test file's layout (B4:B6 merged «Цех»), «Advanced → Edit mapping → Add field» named after the added
 *     column, level «Source», no file column, no header text → preview → apply → the card shows the «Цех» values and the line
 *     «Current table: <file>, <date>, <user>» (before the fix the column stayed empty);
 *  2. a second table with removed, added and changed rows → the preview lists exactly what it replaces → apply → only the rows of
 *     the second table remain, «Цех» empty in the file is empty on the card (no carry-over).
 * The spec makes its own permit, NAE nodes, scope entries and the «Цех <stamp>» column, and removes what the API can remove.
 *
 * Run: `tools/e2e-stand.ps1 -Grep "Air last table"`; CI — workflow `e2e-stand` with `grep`. Another stand (a scratch copy):
 * `ECR_E2E_ADMIN_USER` / `ECR_E2E_ADMIN_PASSWORD` name the account (never printed).
 */
const Admin = {
  user: process.env['ECR_E2E_ADMIN_USER'] ?? 'e2e-admin',
  password: process.env['ECR_E2E_ADMIN_PASSWORD'] ?? 'E2E-Adm1n-Work-2026!',
};
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Region = `E2ELT${Stamp}`;
const Area = 'OFFSHORE';
const NaeTree = 'NAE_TREE';
const Title = `Цех ${Stamp}`;
const Xlsx = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
const Shots = process.env['ECR_E2E_SHOTS'] ?? '';

const First: readonly Shape401Source[] = [
  { nae: '7101', workshop: 'WS-1', name: 'Source one', substances: [{ code: '0301', tons: 1 }, { code: '0337', tons: 2 }] },
  { nae: '7102', workshop: 'WS-2', name: 'Source two', substances: [{ code: '0301', tons: 3 }] },
  { nae: '7103', workshop: 'WS-3', name: 'Source three', substances: [{ code: '0330', tons: 4 }] },
];
const Second: readonly Shape401Source[] = [
  // 7101: «Цех» empty now, 0337 removed, 0330 added, 0301 with a new t/year; 7102 and 7103 gone; 7104 new.
  { nae: '7101', workshop: null, name: 'Source one', substances: [{ code: '0301', tons: 1.5 }, { code: '0330', tons: 9 }] },
  { nae: '7104', workshop: 'WS-4', name: 'Source four', substances: [{ code: '0337', tons: 8 }] },
];

const state = { permitId: 0, permitNumber: `E2E-LT-${Stamp}`, regionNodeId: 0, typeColumn: '', adminDisplayName: '' };
let scope: PreparedScope | null = null;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function addNode(api: APIRequestContext, registryId: number, code: string, parent: number | null, level: string, values: Record<string, string>): Promise<number> {
  return (await ok<{ id: number }>(
    await api.post(`/api/v1/registries/${NaeTree}/entries`, {
      data: { registryDefId: registryId, code, display: { values: { en: code } }, parentEntryId: parent, levelCode: level, values },
    }),
    `node ${code}`,
  )).id;
}

interface TypeColumn { code: string; order: number; [key: string]: unknown }
interface TypeTable { code: string; columns: TypeColumn[]; [key: string]: unknown }
interface PermitType { rowVersion: string; nameL10n: unknown; category: string; rules: unknown; tables: TypeTable[]; hasUnpublishedChanges: boolean }

/** «Add as new column» the way the wizard does it: new version → PUT with the column → publish. */
async function addSourcesColumn(api: APIRequestContext, code: string, name: string): Promise<void> {
  let type = await ok<PermitType>(await api.get('/api/v1/permit-types/AIR'), 'AIR type');
  if (!type.hasUnpublishedChanges) type = await ok<PermitType>(await api.post('/api/v1/permit-types/AIR/new-version'), 'new version');
  const tables = type.tables.map((table) =>
    table.code !== 'AIR_SOURCES'
      ? table
      : {
          ...table,
          columns: [
            ...table.columns,
            {
              code, nameL10n: { en: name, ru: name, kz: name }, dataType: 'String', order: Math.max(...table.columns.map((c) => c.order)) + 1,
              required: false, key: false, choices: null, group: null, listRegistryCode: null, listScope: null, precision: null, scale: null,
              siblingTableCode: null, unitId: null,
            },
          ],
        },
  );
  const replaced = await ok<{ rowVersion: string }>(
    await api.put('/api/v1/permit-types/AIR', { data: { rowVersion: type.rowVersion, nameL10n: type.nameL10n, category: type.category, rules: type.rules, tables } }),
    'replace AIR',
  );
  await ok(await api.post('/api/v1/permit-types/AIR/publish', { data: { reason: 'e2e Air last table: «Цех» column', rowVersion: replaced.rowVersion } }), 'publish AIR');
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function shot(page: Page, name: string): Promise<void> {
  if (Shots === '') return;
  await page.screenshot({ path: `${Shots}/${name}.png`, fullPage: true });
}

/** Region → File → Mapping of the wizard with `file` (named `name`). */
async function toMapping(page: Page, file: Buffer, name: string): Promise<void> {
  await page.goto(`/admin/permits/${String(state.permitId)}/import`);
  await expect(page.getByRole('heading', { name: 'Region and area', level: 2 })).toBeVisible({ timeout: 30_000 });
  if (scope === null) throw new Error('scope not prepared');
  await fillScope(page, scope);
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
  await page.locator('input[type="file"]').setInputFiles({ name, mimeType: Xlsx, buffer: file });
  await page.getByRole('button', { name: /Next: analyse file/ }).click();
  await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 60_000 });
}

async function toPreview(page: Page): Promise<void> {
  await page.getByRole('button', { name: /Next: preview/ }).click();
  await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('air-row-changes')).toBeVisible({ timeout: 30_000 });
}

async function apply(page: Page): Promise<void> {
  const button = page.getByRole('button', { name: /Apply import/ });
  await expect(button).toBeEnabled();
  await button.click();
  await expect(page.getByRole('heading', { name: 'Result', level: 2 })).toBeVisible({ timeout: 120_000 });
}

test.describe('Air last table', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });
  let admin: APIRequestContext;

  test.beforeAll(async ({ playwright }, info) => {
    test.setTimeout(180_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    // The card names who applied the table by display name (AirCurrentTableDto.ImportedBy), not by login.
    state.adminDisplayName = (await ok<{ displayName?: string }>(
      await admin.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } }),
      'admin login',
    )).displayName ?? '';
    expect(state.adminDisplayName, 'login response has no displayName').not.toBe('');

    const treeId = await ensureNaeTree(admin);
    await ensureSubstances(admin);
    await ok(await admin.post('/api/v1/air-limits/provision'), 'provision');

    state.regionNodeId = await addNode(admin, treeId, `${Region}_R`, null, 'REGION', { RegionCode: Region });
    const areaNode = await addNode(admin, treeId, `${Region}_A`, state.regionNodeId, 'AREA', { RegionCode: Region, AreaCode: Area });
    const site = await addNode(admin, treeId, `${Region}_S`, areaNode, 'SITE', { RegionCode: Region, AreaCode: Area });
    for (const nae of ['7101', '7102', '7103', '7104']) {
      await addNode(admin, treeId, `${Region}_N${nae}`, site, 'SRC', { NaeCode: nae, RegionCode: Region, AreaCode: Area });
    }
    scope = await prepareScope(admin, Region, Area, `L${Stamp}`);

    state.typeColumn = `FILE_B_${Stamp}`;
    await addSourcesColumn(admin, state.typeColumn, Title);

    state.permitId = (await ok<{ id: number }>(
      await admin.post('/api/v1/permits', {
        data: { typeCode: 'AIR', number: state.permitNumber, location: null, issueDate: null, validFrom: '2026-01-01', validTo: '2031-01-01', reportingFlags: null },
      }),
      'AIR permit',
    )).id;
  });

  test.afterAll(async () => {
    test.setTimeout(180_000);
    const problems: string[] = [];
    if (state.permitId !== 0) {
      const response = await admin.delete(`/api/v1/permits/${String(state.permitId)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`permit: ${String(response.status())}`);
    }
    const tree = await ok<{ nodes: { id: number; parentEntryId: number | null; levelCode: string; code: string }[] }>(await admin.get(`/api/v1/registries/${NaeTree}/tree`), 'tree');
    const mine = new Set<number>([state.regionNodeId]);
    for (let grew = true; grew;) {
      grew = false;
      for (const node of tree.nodes) {
        if (node.parentEntryId !== null && mine.has(node.parentEntryId) && !mine.has(node.id)) {
          mine.add(node.id);
          grew = true;
        }
      }
    }
    for (const level of ['SRC', 'EQUIP', 'SITE', 'AREA', 'REGION']) {
      for (const node of tree.nodes.filter((n) => mine.has(n.id) && n.levelCode === level)) {
        const response = await admin.delete(`/api/v1/registries/${NaeTree}/entries/${String(node.id)}`);
        if (response.status() >= 300 && response.status() !== 404) problems.push(`node ${node.code}: ${String(response.status())}`);
      }
    }
    problems.push(...(await removeScopeEntries(admin, scope?.created ?? [])));
    await admin.dispose();
    expect(problems, 'cleanup').toEqual([]);
  });

  test('Air last table: «Цех» added in the wizard is imported from the 401 layout and the card names the current table', async ({ page }) => {
    await signIn(page);
    await toMapping(page, build401(First, Title), 'params-401-first.xlsx');

    // The human's way: Advanced → Edit mapping → Add field named after the column, level «Source», nothing else.
    await page.getByText('Advanced: edit the mapping rules').click();
    await page.getByRole('button', { name: 'Edit mapping' }).click();
    await page.getByRole('textbox', { name: 'New field name' }).fill(Title);
    await page.getByRole('button', { name: 'Add field' }).click();
    await page.getByRole('textbox', { name: `Level of field ${Title}` }).click();
    await page.getByRole('option', { name: 'Source' }).click();
    await page.getByRole('button', { name: 'Re-analyse file' }).click();
    await expect(page.getByText(/All required columns were found/)).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText(`The column for "${Title}" was not found in the file header.`)).toHaveCount(0);

    await toPreview(page);
    await expect(page.getByTestId('air-changes-sources')).toHaveText('Sources: 3 added, 0 changed, 0 removed');
    await shot(page, 'e2e-01-preview-first');
    await apply(page);

    await page.goto(`/admin/permits/${String(state.permitId)}?tab=air`);
    await expect(page.getByTestId('air-counts')).toHaveText('3 sources · 4 substance rows', { timeout: 30_000 });
    for (const value of ['WS-1', 'WS-2', 'WS-3']) await expect(page.getByText(value, { exact: true })).toBeVisible();
    await expect(page.getByTestId('air-current-table')).toContainText('params-401-first.xlsx');
    await expect(page.getByTestId('air-current-table')).toContainText(state.adminDisplayName);
    await shot(page, 'e2e-02-card-first');
  });

  test('Air last table: a second table replaces every row — the preview lists what it replaces, only its rows remain, no carry-over', async ({ page }) => {
    await signIn(page);
    await toMapping(page, build401(Second, Title), 'params-401-second.xlsx');
    await toPreview(page);

    await expect(page.getByTestId('air-changes-sources')).toHaveText('Sources: 1 added, 1 changed, 2 removed');
    await expect(page.getByTestId('air-changes-substances')).toHaveText('Substance rows: 2 added, 1 changed, 3 removed');
    const rows = page.getByTestId('air-row-change');
    await expect(rows.filter({ hasText: '7102' }).first()).toHaveAttribute('data-kind', 'Removed');
    await expect(rows.filter({ hasText: '7104' }).first()).toHaveAttribute('data-kind', 'Added');
    await expect(rows.filter({ hasText: `${Title}: WS-1 → (empty)` })).toHaveCount(1);
    await expect(rows.filter({ hasText: '1 → 1.5' })).toHaveCount(1);
    await shot(page, 'e2e-03-preview-second');
    await apply(page);

    await page.goto(`/admin/permits/${String(state.permitId)}?tab=air`);
    await expect(page.getByTestId('air-counts')).toHaveText('2 sources · 3 substance rows', { timeout: 30_000 });
    await expect(page.getByText('WS-4', { exact: true })).toBeVisible();
    for (const gone of ['WS-1', 'WS-2', 'WS-3']) await expect(page.getByText(gone, { exact: true })).toHaveCount(0);
    await expect(page.getByTestId('air-current-table')).toContainText('params-401-second.xlsx');
    await shot(page, 'e2e-04-card-second');

    const card = await ok<{ sources: { naeCode: string; added?: Record<string, unknown> | null }[] }>(
      await admin.get(`/api/v1/permits/${String(state.permitId)}/air-limits`),
      'card',
    );
    expect(card.sources.map((s) => s.naeCode).sort()).toEqual(['7101', '7104']);
    expect(card.sources.find((s) => s.naeCode === '7101')?.added?.[state.typeColumn]).toBeUndefined();
  });
});
