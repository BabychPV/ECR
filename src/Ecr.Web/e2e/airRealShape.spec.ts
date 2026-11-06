import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';
import { RealShapeSources, realShapeWorkbook, type RealShapeSource } from './airRealShape';
import { ensureNaeTree, ensureSubstances, fillScope, prepareScope, removeScopeEntries, type PreparedScope } from './airScope';

/**
 * Air real shape (P1 06.11, human: the permit's «Import parameters…» must take «7) Параметры_2024_final.xlsx» as it is) —
 * REAL e2e on a sanitized workbook of the same shape (`airRealShape.ts`: 3 title rows, 3-row merged header, number row,
 * section row, sources merged down over their substances):
 *   1. the header block (rows 4–6, data from 8) is found by itself and shown as editable numbers;
 *   2. the unmapped file column B «Цех» is added as a NEW column of the sources table (permit-type new version + publish) and
 *      read by the next analysis; preview → 3 new → apply;
 *   3. re-import = OVERWRITE: a file without one source and with one changed gives new 0 / changed 1 / unchanged 1 /
 *      removed 1; adding B again reuses the column (no new type version).
 *
 * Run: `tools/e2e-stand.ps1 -Grep "Air real shape"`; CI — workflow `e2e-stand` with `grep`.
 * ⚠ The stand's AIR type keeps the added column (published types are not rolled back); a rerun reuses it.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Region = `E2ERS${Stamp}`;
const Area = 'OFFSHORE';
const NaeTree = 'NAE_TREE';
const Xlsx = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
const Workshop = 'Цех (e2e)';

const state = { permitId: 0, permitNumber: `E2E-RS-${Stamp}`, regionNodeId: 0 };
let scope: PreparedScope | null = null;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function addNode(api: APIRequestContext, registryId: number, code: string, parent: number | null, level: string, values: Record<string, string>): Promise<number> {
  return (
    await ok<{ id: number }>(
      await api.post(`/api/v1/registries/${NaeTree}/entries`, {
        data: { registryDefId: registryId, code, display: { values: { en: code } }, parentEntryId: parent, levelCode: level, values },
      }),
      `node ${code}`,
    )
  ).id;
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** «Import parameters…» → region and area → file → «Mapping». */
async function toMapping(page: Page, file: Buffer): Promise<void> {
  const prepared = scope;
  if (prepared === null) throw new Error('scope not prepared');
  await page.goto(`/admin/permits/${String(state.permitId)}/import`);
  await expect(page.getByRole('heading', { name: 'Region and area', level: 2 })).toBeVisible({ timeout: 30_000 });
  await fillScope(page, prepared);
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
  await page.locator('input[type="file"]').setInputFiles({ name: 'Параметры (e2e).xlsx', mimeType: Xlsx, buffer: file });
  await page.getByRole('button', { name: /Next: analyse file/ }).click();
  await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 30_000 });
}

/** «Add file column B as a new column» with the name, Text, one value per Source; waits for the re-read. */
async function addWorkshop(page: Page): Promise<void> {
  await page.getByText(/^Unmapped columns:/).click();
  await page.getByRole('button', { name: 'Add file column B as a new column' }).click();
  await page.getByRole('textbox', { name: 'Column name' }).fill(Workshop);
  await page.getByRole('button', { name: 'Add column', exact: true }).click();
  await expect(page.getByText(`Column «${Workshop}» added. The file was re-read with it.`)).toBeVisible({ timeout: 60_000 });
  await expect(page.getByRole('button', { name: 'Add file column B as a new column' })).toHaveCount(0, { timeout: 60_000 });
}

async function diff(page: Page, label: string): Promise<string> {
  return (await page.locator('dl.pi-diff dt', { hasText: new RegExp(`^${label}$`) }).locator('xpath=following-sibling::dd[1]').innerText()).trim();
}

async function airVersion(api: APIRequestContext): Promise<number> {
  return (await ok<{ version: number }>(await api.get('/api/v1/permit-types/AIR'), 'AIR type')).version;
}

test.describe('Air real shape', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });
  let admin: APIRequestContext;

  test.beforeAll(async ({ playwright }, info) => {
    test.setTimeout(180_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await ok(await admin.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } }), 'admin login');

    const treeId = await ensureNaeTree(admin);
    await ensureSubstances(admin);
    await ok(await admin.post('/api/v1/air-limits/provision'), 'provision');

    state.regionNodeId = await addNode(admin, treeId, `${Region}_R`, null, 'REGION', { RegionCode: Region });
    const areaNode = await addNode(admin, treeId, `${Region}_A`, state.regionNodeId, 'AREA', { RegionCode: Region, AreaCode: Area });
    const site = await addNode(admin, treeId, `${Region}_S`, areaNode, 'SITE', { RegionCode: Region, AreaCode: Area });
    for (const source of RealShapeSources) {
      await addNode(admin, treeId, `${Region}_N${source.nae}`, site, 'SRC', { NaeCode: source.nae, RegionCode: Region, AreaCode: Area });
    }
    scope = await prepareScope(admin, Region, Area, `RS${Stamp}`);

    state.permitId = (
      await ok<{ id: number }>(
        await admin.post('/api/v1/permits', {
          data: { typeCode: 'AIR', number: state.permitNumber, location: null, issueDate: null, validFrom: '2026-01-01', validTo: '2031-01-01', reportingFlags: null },
        }),
        'AIR permit',
      )
    ).id;
  });

  test.afterAll(async () => {
    test.setTimeout(180_000);
    const problems: string[] = [];
    if (state.permitId !== 0) {
      const response = await admin.delete(`/api/v1/permits/${String(state.permitId)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`permit: ${String(response.status())}`);
    }
    const tree = await ok<{ nodes: { id: number; parentEntryId: number | null; levelCode: string; code: string }[] }>(
      await admin.get(`/api/v1/registries/${NaeTree}/tree`),
      'NAE tree',
    );
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

  test('Air real shape: header rows found, file column B added as a new column, 3 new sources applied', async ({ page }) => {
    await signIn(page);
    await toMapping(page, realShapeWorkbook(RealShapeSources));

    // 1. Where the table is: found by itself, editable.
    await expect(page.getByRole('textbox', { name: 'Header starts at row' })).toHaveValue('4');
    await expect(page.getByRole('textbox', { name: 'Header ends at row' })).toHaveValue('6');
    await expect(page.getByRole('textbox', { name: 'Data starts at row' })).toHaveValue('8');
    await expect(page.getByTestId('air-header-report')).toContainText('group rows 1');

    // 2. «Add as new column» for B «Цех»: the permit type gets the column; the file is re-read with it.
    await addWorkshop(page);
    const type = await ok<{ tables: { code: string; columns: { code: string; nameL10n: Record<string, string> }[] }[] }>(
      await admin.get('/api/v1/permit-types/AIR'),
      'AIR type',
    );
    const added = type.tables.find((t) => t.code === 'AIR_SOURCES')?.columns.find((c) => c.nameL10n['en'] === Workshop);
    expect(added?.code, 'the sources table has the new column').toMatch(/^FILE_B(_\d+)?$/);
    await expect(page.getByRole('region', { name: 'Matched columns' }).getByText(added?.code ?? '-', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: /Next: preview/ }).click();
    await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible({ timeout: 30_000 });
    expect(await diff(page, 'New')).toBe('3');
    const apply = page.getByRole('button', { name: /Apply import/ });
    await expect(apply).toBeEnabled();
    await apply.click();
    await expect(page.getByRole('heading', { name: 'Result', level: 2 })).toBeVisible({ timeout: 60_000 });

    const card = await ok<{ sources: unknown[]; substanceRows: number }>(await admin.get(`/api/v1/permits/${String(state.permitId)}/air-limits`), 'card');
    expect(card.sources).toHaveLength(3);
    expect(card.substanceRows).toBe(6);
  });

  test('Air real shape: re-import overwrites — new 0, changed 1, unchanged 1, removed 1; column B is reused', async ({ page }) => {
    const [first, second] = RealShapeSources as readonly [RealShapeSource, RealShapeSource, RealShapeSource];
    const changed: RealShapeSource[] = [first, { ...second, height: second.height + 5 }];
    const before = await airVersion(admin);

    await signIn(page);
    await toMapping(page, realShapeWorkbook(changed));
    await addWorkshop(page);
    expect(await airVersion(admin), 'the same column again is reused, the type is not republished').toBe(before);

    await page.getByRole('button', { name: /Next: preview/ }).click();
    await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible({ timeout: 30_000 });
    expect([await diff(page, 'New'), await diff(page, 'Changed'), await diff(page, 'Unchanged'), await diff(page, 'Removed')]).toEqual(['0', '1', '1', '1']);

    await page.getByRole('button', { name: /Apply import/ }).click();
    await expect(page.getByRole('heading', { name: 'Result', level: 2 })).toBeVisible({ timeout: 60_000 });
    const card = await ok<{ sources: unknown[]; substanceRows: number }>(await admin.get(`/api/v1/permits/${String(state.permitId)}/air-limits`), 'card');
    expect(card.sources).toHaveLength(2);
    expect(card.substanceRows).toBe(3);
  });
});
