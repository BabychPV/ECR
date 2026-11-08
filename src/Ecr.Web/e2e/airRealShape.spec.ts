import { writeFileSync } from 'node:fs';
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
    // The added field is shown by the column's name in the permit type, not by its code.
    await expect(page.getByRole('region', { name: 'Matched columns' }).getByRole('rowheader', { name: Workshop, exact: true })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Matched columns' }).getByText(added?.code ?? '-', { exact: true })).toHaveCount(0);

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

    // 3. «Sources & parameters» shows ALL imported data, incl. the added column, and edits a row (P1 07.11); the raw
    // «Sources (NAE)» / «Substances of sources» tabs are hidden for AIR.
    await page.goto(`/admin/permits/${String(state.permitId)}?tab=air`);
    await expect(page.getByTestId('air-counts')).toHaveText('3 sources · 6 substance rows', { timeout: 30_000 });
    await expect(page.getByRole('columnheader', { name: Workshop })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Цех 2', exact: true })).toBeVisible();
    await expect(page.getByRole('tab', { name: /Sources \(NAE\)/ })).toHaveCount(0);
    await expect(page.getByRole('tab', { name: /Substances of sources/ })).toHaveCount(0);

    await page.getByRole('button', { name: 'Edit source 0003' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.locator(`[data-field="${added?.code ?? '-'}"]`).fill('Цех 3 (edited)');
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(dialog).toHaveCount(0, { timeout: 30_000 });
    await expect(page.getByRole('cell', { name: 'Цех 3 (edited)', exact: true })).toBeVisible({ timeout: 30_000 });
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

  /**
   * P1 UAT 07.11: a field added in the editor (name only) and «Re-analyse file» ended in «The request failed», «Next: preview»
   * stayed disabled. Root cause on the stand: the chosen file was changed on disk after it was chosen — Chromium then refuses
   * every upload of it (`fetch` → `TypeError: Failed to fetch`, no HTTP answer), while requests without the file (save profile)
   * still work. Nothing is applied here: only analyses.
   */
  test('Air real shape: a field without a column and a file changed after it was chosen never leave the user stuck', async ({ page }) => {
    const field = 'Workshop e2e field';
    const book = test.info().outputPath('params-e2e.xlsx');
    writeFileSync(book, realShapeWorkbook(RealShapeSources));

    await signIn(page);
    const prepared = scope;
    if (prepared === null) throw new Error('scope not prepared');
    await page.goto(`/admin/permits/${String(state.permitId)}/import`);
    await expect(page.getByRole('heading', { name: 'Region and area', level: 2 })).toBeVisible({ timeout: 30_000 });
    await fillScope(page, prepared);
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
    // From a path (not a buffer): the browser holds a real file-backed File, as when the user picks it.
    await page.locator('input[type="file"]').setInputFiles(book);
    await page.getByRole('button', { name: /Next: analyse file/ }).click();
    await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(page.locator('.pi-stat').first()).toContainText('3');

    // 1. «Add field» gives a name and nothing else: highlighted, the save is blocked with the reason, re-analysis still works.
    await page.getByText('Advanced: edit the mapping rules').click();
    await page.getByRole('button', { name: 'Edit mapping' }).click();
    await page.getByRole('textbox', { name: 'New field name' }).fill(field);
    await page.getByRole('button', { name: 'Add field', exact: true }).click();
    await expect(page.getByTestId('air-field-without-column')).toContainText(`Field "${field}" has no file column`);
    await expect(page.getByRole('button', { name: 'Save as profile…' })).toBeDisabled();
    await page.getByRole('button', { name: 'Re-analyse file' }).click();
    await expect(page.getByText(`The column for "${field}" was not found in the file header.`)).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText('The request failed')).toHaveCount(0);
    await expect(page.getByRole('button', { name: /Next: preview/ })).toBeEnabled({ timeout: 30_000 });

    // 2. The column picked from the file's columns: the field reads B «Цех».
    await page.getByRole('textbox', { name: `File column of field ${field}` }).click();
    await page.getByRole('option', { name: 'B — Цех' }).click();
    await expect(page.getByTestId('air-field-without-column')).toHaveCount(0);
    await expect(page.getByRole('button', { name: /Next: preview/ })).toBeDisabled();
    await page.getByRole('button', { name: 'Re-analyse file' }).click();
    await expect(page.getByRole('region', { name: 'Matched columns' }).getByRole('rowheader', { name: field, exact: true })).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole('button', { name: /Next: preview/ })).toBeEnabled({ timeout: 30_000 });

    // 3. The file is saved again on disk (as Excel does): the reason and «Choose the file again», never «The request failed».
    writeFileSync(book, realShapeWorkbook(RealShapeSources.slice(0, 2)));
    await page.getByRole('button', { name: 'Re-analyse file' }).click();
    const failure = page.getByTestId('air-send-failure');
    await expect(failure).toContainText('The file could not be sent', { timeout: 60_000 });
    await expect(page.getByText('The request failed')).toHaveCount(0);
    await failure.locator('input[type="file"]').setInputFiles(book);
    await expect(failure).toHaveCount(0, { timeout: 60_000 });
    await expect(page.locator('.pi-stat').first()).toContainText('2', { timeout: 60_000 });
  });

  /**
   * Human pass 2 (07.11, P2): «Save as profile…» under a code that exists said «changed after you opened it». Now: a taken code →
   * «A profile with this code already exists» + «Overwrite» (re-saved with the profile's current token); the built-in
   * ERA_TABLE_33 → its own refusal, only «Choose another code», and it stays unchanged. Nothing is imported.
   * ⚠ The profile PASS2_<stamp> stays on the stand; its code is unique per run.
   */
  test('Air pass2: a taken profile code offers Overwrite; the built-in profile is never overwritten', async ({ page }) => {
    const code = `PASS2_${Stamp}`;
    const builtInBefore = await ok<{ rowVersion: string }>(await admin.get('/api/v1/air-limits/import-profiles/ERA_TABLE_33'), 'built-in profile');
    await signIn(page);
    await toMapping(page, realShapeWorkbook(RealShapeSources));

    const save = async (name: string, profileCode: string): Promise<void> => {
      await page.getByRole('button', { name: 'Save as profile…' }).click();
      await page.getByRole('textbox', { name: 'Profile name' }).fill(name);
      await page.getByRole('textbox', { name: /^Profile code/ }).fill(profileCode);
      await page.getByRole('button', { name: 'Save profile', exact: true }).click();
    };

    await save('Pass2 e2e', code);
    await expect(page.getByText(`Profile ${code} saved and selected.`)).toBeVisible({ timeout: 30_000 });

    await save('Pass2 e2e again', code);
    const conflict = page.getByTestId('air-profile-conflict');
    await expect(conflict).toContainText('A profile with this code already exists', { timeout: 30_000 });
    await expect(page.getByText(/changed after you opened it/)).toHaveCount(0);
    await page.getByRole('button', { name: 'Overwrite', exact: true }).click();
    await expect(conflict).toHaveCount(0, { timeout: 30_000 });
    const stored = await ok<{ nameL10n: Record<string, string> }>(await admin.get(`/api/v1/air-limits/import-profiles/${code}`), 'saved profile');
    expect(stored.nameL10n['en']).toBe('Pass2 e2e again');

    await save('Mine', 'ERA_TABLE_33');
    await expect(conflict).toContainText('The built-in profile cannot be overwritten', { timeout: 30_000 });
    await expect(page.getByRole('button', { name: 'Overwrite', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Choose another code' }).click();
    await expect(conflict).toHaveCount(0);
    await expect(page.getByRole('textbox', { name: /^Profile code/ })).toBeFocused();
    const builtInAfter = await ok<{ rowVersion: string }>(await admin.get('/api/v1/air-limits/import-profiles/ERA_TABLE_33'), 'built-in profile');
    expect(builtInAfter.rowVersion).toBe(builtInBefore.rowVersion);
  });

  /**
   * Human pass 2 (07.11, P2): NAE codes missing in the tree gave «The file has errors — fix them and upload again». Now they are a
   * warning group: no «fix the file» gate, «Create all undecided under this node», «Run preview again», Apply. Apply stays
   * disabled while any code is undecided.
   */
  test('Air pass2: missing NAE are a warning group, created under a node from the preview, then applied', async ({ page }) => {
    const [first, second] = RealShapeSources as readonly [RealShapeSource, RealShapeSource, RealShapeSource];
    const fresh: RealShapeSource[] = [{ ...first, nae: '0901' }, { ...second, nae: '0902' }];
    await signIn(page);
    await toMapping(page, realShapeWorkbook(fresh));
    await page.getByRole('button', { name: /Next: preview/ }).click();
    await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible({ timeout: 30_000 });

    await expect(page.getByRole('heading', { name: /NAE codes missing in the tree: 2/ })).toBeVisible();
    await expect(page.getByTestId('air-missing-group')).toContainText('These are not file errors');
    await expect(page.getByText('The file has errors — fix them and upload again.')).toHaveCount(0);
    await expect(page.getByText('Decide every missing NAE code: create or skip.')).toBeVisible();
    const apply = page.getByRole('button', { name: /Apply import/ });
    await expect(apply).toBeDisabled();

    await page.getByRole('textbox', { name: /^Node for all undecided NAE/ }).click();
    await page.getByRole('option', { name: new RegExp(`${Region}_S$`) }).click();
    await page.getByRole('button', { name: 'Create all undecided (2) under this node' }).click();
    await expect(apply).toBeDisabled();
    await page.getByRole('button', { name: 'Run preview again' }).click();
    await expect(page.getByText('The preview is out of date')).toHaveCount(0, { timeout: 30_000 });
    await expect(apply).toBeEnabled({ timeout: 30_000 });
    await apply.click();
    await expect(page.getByRole('heading', { name: 'Result', level: 2 })).toBeVisible({ timeout: 60_000 });

    // The apply refuses any unresolved NAE: two sources in the card means both nodes were created under the site.
    await expect(page.getByText('NAE created in the tree')).toBeVisible();
    const card = await ok<{ sources: unknown[] }>(await admin.get(`/api/v1/permits/${String(state.permitId)}/air-limits`), 'card');
    expect(card.sources).toHaveLength(2);
  });

  /** Pass 3: «Region and area» → «File» with the workbook chosen; the analysis is NOT started. */
  async function toFile(page: Page, file: Buffer): Promise<void> {
    const prepared = scope;
    if (prepared === null) throw new Error('scope not prepared');
    await page.goto(`/admin/permits/${String(state.permitId)}/import`);
    await expect(page.getByRole('heading', { name: 'Region and area', level: 2 })).toBeVisible({ timeout: 30_000 });
    await fillScope(page, prepared);
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
    await page.locator('input[type="file"]').setInputFiles({ name: 'Параметры (e2e).xlsx', mimeType: Xlsx, buffer: file });
  }

  async function chooseProfile(page: Page, label: string): Promise<void> {
    await page.getByRole('textbox', { name: /^Import profile/ }).click();
    await page.getByRole('option', { name: label, exact: true }).click();
  }

  async function newProfile(code: string, name: string): Promise<void> {
    const builtIn = await ok<{ spec: unknown }>(await admin.get('/api/v1/air-limits/import-profiles/ERA_TABLE_33'), 'built-in profile');
    await ok(
      await admin.put(`/api/v1/air-limits/import-profiles/${code}`, { data: { nameL10n: { en: name }, spec: builtIn.spec, rowVersion: null } }),
      `profile ${code}`,
    );
  }

  /** UAT pass 3: the built-in profile is read-only and says so in visible text; it offers no edit and no delete. */
  test('Air pass3: the built-in profile is read-only and says why', async ({ page }) => {
    await signIn(page);
    await toFile(page, realShapeWorkbook(RealShapeSources));
    // The first profile by code is the built-in one, chosen by default.
    await expect(page.getByRole('textbox', { name: /^Import profile/ })).toHaveValue(/\(ERA_TABLE_33\)$/);
    await expect(page.getByTestId('air-profile-builtin')).toContainText('ERA_TABLE_33 is the built-in profile: read-only');
    await expect(page.getByRole('button', { name: 'Delete profile…' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Edit profile' })).toHaveCount(0);
  });

  /**
   * UAT pass 3 («existing profiles cannot be edited or re-saved»): «Edit profile» opens the chosen profile's mapping in the editor;
   * a field is added; «Save to profile …» asks first and re-saves under the SAME code with the profile's token and names.
   */
  test('Air pass3: edit an existing profile and re-save it under the same code', async ({ page }) => {
    const code = `PASS3E_${Stamp}`;
    const field = 'Workshop pass3';
    await newProfile(code, 'Pass3 edit e2e');
    const before = await ok<{ rowVersion: string }>(await admin.get(`/api/v1/air-limits/import-profiles/${code}`), 'profile');

    await signIn(page);
    await toFile(page, realShapeWorkbook(RealShapeSources));
    await chooseProfile(page, `Pass3 edit e2e (${code})`);
    await page.getByRole('button', { name: 'Edit profile' }).click();
    await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 30_000 });
    // The editor is open with the profile's fields; nothing to re-read yet.
    await expect(page.getByRole('textbox', { name: 'New field name' })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('button', { name: /Next: preview/ })).toBeEnabled();

    await page.getByRole('textbox', { name: 'New field name' }).fill(field);
    await page.getByRole('button', { name: 'Add field', exact: true }).click();
    await page.getByRole('textbox', { name: `File column of field ${field}` }).click();
    await page.getByRole('option', { name: 'B — Цех' }).click();
    await page.getByRole('button', { name: 'Re-analyse file' }).click();
    // A field that matches no table column is said to be not saved (pass 3), not silently dropped.
    await expect(page.getByTestId('air-unsaved-fields')).toContainText(field, { timeout: 60_000 });

    await page.getByRole('button', { name: `Save to profile ${code}…` }).click();
    const confirm = page.getByTestId('air-profile-resave-confirm');
    await expect(confirm).toContainText(`Overwrite profile ${code}?`);
    await confirm.getByRole('button', { name: 'Overwrite', exact: true }).click();
    await expect(page.getByText(`Profile ${code} saved and selected.`)).toBeVisible({ timeout: 30_000 });

    const after = await ok<{ rowVersion: string; nameL10n: Record<string, string>; spec: { fields: { name: string; column: string | null }[] } }>(
      await admin.get(`/api/v1/air-limits/import-profiles/${code}`),
      'profile',
    );
    expect(after.rowVersion).not.toBe(before.rowVersion);
    expect(after.nameL10n['en']).toBe('Pass3 edit e2e');
    expect(after.spec.fields.find((f) => f.name === field)?.column).toBe('B');
    await ok(await admin.delete(`/api/v1/air-limits/import-profiles/${code}?rowVersion=${encodeURIComponent(after.rowVersion)}`), 'cleanup');
  });

  /** UAT pass 3: «Delete profile…» asks first; after the delete the profile is gone and the built-in one is chosen. */
  test('Air pass3: delete a profile after a confirmation', async ({ page }) => {
    const code = `PASS3D_${Stamp}`;
    await newProfile(code, 'Pass3 delete e2e');

    await signIn(page);
    await toFile(page, realShapeWorkbook(RealShapeSources));
    await chooseProfile(page, `Pass3 delete e2e (${code})`);
    await page.getByRole('button', { name: 'Delete profile…' }).click();
    const confirm = page.getByTestId('air-profile-delete-confirm');
    await expect(confirm).toContainText(`Delete profile Pass3 delete e2e (${code})?`);
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(confirm).toHaveCount(0, { timeout: 30_000 });
    expect((await admin.get(`/api/v1/air-limits/import-profiles/${code}`)).status()).toBe(404);
    await expect(page.getByTestId('air-profile-builtin')).toBeVisible({ timeout: 30_000 });
    await page.getByRole('textbox', { name: /^Import profile/ }).click();
    await expect(page.getByRole('option', { name: `Pass3 delete e2e (${code})`, exact: true })).toHaveCount(0);
  });

  /**
   * UAT pass 3 («Цех» imported, the Sources grid shows «—»): the human's profile field was NAMED after the column («цех»), not
   * given its code (FILE_B…). Here the field «Add as new column» created is renamed to the column's display name in lower case;
   * the import now binds it to the column by that name — no «will not be saved» warning — and the grid shows the values.
   */
  test('Air pass3: a field named after the column «Цех» reaches the Sources grid', async ({ page }) => {
    const sources = RealShapeSources.map((source, i) => ({ ...source, workshop: `Цех P3-${String(i + 1)}` }));
    await signIn(page);
    await toMapping(page, realShapeWorkbook(sources));
    await addWorkshop(page);

    const added = page.getByRole('textbox', { name: 'Field name', exact: true }).last();
    await expect(added).toHaveValue(/^FILE_B/);
    await added.fill(Workshop.toLowerCase());
    await page.getByRole('button', { name: 'Re-analyse file' }).click();
    await expect(page.getByRole('button', { name: /Next: preview/ })).toBeEnabled({ timeout: 60_000 });
    await expect(page.getByTestId('air-unsaved-fields')).toHaveCount(0);

    await page.getByRole('button', { name: /Next: preview/ }).click();
    await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('air-unsaved-fields')).toHaveCount(0);
    await page.getByRole('button', { name: /Apply import/ }).click();
    await expect(page.getByRole('heading', { name: 'Result', level: 2 })).toBeVisible({ timeout: 60_000 });

    await page.goto(`/admin/permits/${String(state.permitId)}?tab=air`);
    await expect(page.getByRole('columnheader', { name: Workshop })).toBeVisible({ timeout: 30_000 });
    for (const source of sources) {
      await expect(page.getByRole('cell', { name: source.workshop, exact: true })).toBeVisible();
    }
  });

  /** Human pass 2 (07.11, P3): the step pills looked like buttons but did nothing. A completed step now goes back to it. */
  test('Air pass2: a completed step pill goes back to that step; the current and later ones are not buttons', async ({ page }) => {
    await signIn(page);
    await toMapping(page, realShapeWorkbook(RealShapeSources));
    const steps = page.getByRole('list', { name: 'Import steps' });
    await expect(steps.getByRole('button')).toHaveCount(2);
    await expect(steps.getByText('3 Mapping')).toHaveAttribute('aria-current', 'step');
    await expect(steps.getByRole('button', { name: '4 Preview' })).toHaveCount(0);

    await steps.getByRole('button', { name: '2 File' }).click();
    await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(steps.getByRole('button')).toHaveCount(1);
    // Forward only through the checks: the file is kept and analysed again by «Next».
    await page.getByRole('button', { name: /Next: analyse file/ }).click();
    await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 30_000 });
  });

  async function dropProfile(code: string): Promise<void> {
    const current = await ok<{ rowVersion: string }>(await admin.get(`/api/v1/air-limits/import-profiles/${code}`), `profile ${code}`);
    await ok(await admin.delete(`/api/v1/air-limits/import-profiles/${code}?rowVersion=${encodeURIComponent(current.rowVersion)}`), `delete ${code}`);
  }

  /**
   * UAT pass 3b (P2-1): after «Add as new column» made a column for file column B, a new import of the same file listed B again
   * under «Unmapped columns … Add as new column». A file column whose whole header equals the column's name is now read into it.
   * The header is unique to this run, so the other tests (which add «Цех (e2e)» for the header «Цех») are not affected.
   */
  test('Air pass3b: a re-import matches file column B to the column added for it, by its name', async ({ page }) => {
    const name = `Цех 3b ${Stamp}`;
    const file = realShapeWorkbook(RealShapeSources, name);
    await signIn(page);
    await toMapping(page, file);
    await page.getByText(/^Unmapped columns:/).click();
    await page.getByRole('button', { name: 'Add file column B as a new column' }).click();
    await expect(page.getByRole('textbox', { name: 'Column name' })).toHaveValue(name);
    await page.getByRole('button', { name: 'Add column', exact: true }).click();
    await expect(page.getByText(`Column «${name}» added. The file was re-read with it.`)).toBeVisible({ timeout: 60_000 });

    // A new import of the same file with the built-in profile: B is matched to the column by its name; nothing to add again.
    await toMapping(page, file);
    await expect(page.getByRole('region', { name: 'Matched columns' }).getByRole('rowheader', { name, exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Add file column B as a new column' })).toHaveCount(0);
  });

  /**
   * UAT pass 3b (P2-2, the human's profile TEST2): a field «test» with the SAME header as «emissionPointName» (column F). Required:
   * no source is read, and the banner says why (the sheet, the header rows and the finding). Optional: the sources are read and
   * the finding is only a warning on the field — before, either way gave «0 / 0» with no reason.
   */
  test('Air pass3b: a profile with a field on a column another field reads says why no source is read', async ({ page }) => {
    const builtIn = await ok<{ spec: { fields: Record<string, unknown>[] } }>(
      await admin.get('/api/v1/air-limits/import-profiles/ERA_TABLE_33'),
      'built-in profile',
    );
    const patterns = builtIn.spec.fields.find((field) => field['name'] === 'emissionPointName')?.['patterns'];
    const blocking = `P3BREQ_${Stamp}`;
    const optional = `P3BOPT_${Stamp}`;
    for (const [code, required] of [[blocking, true], [optional, false]] as const) {
      const spec = { ...builtIn.spec, fields: [...builtIn.spec.fields, { name: 'test', level: 'parent', patterns, required }] };
      await ok(
        await admin.put(`/api/v1/air-limits/import-profiles/${code}`, { data: { nameL10n: { en: `Pass3b ${code}` }, spec, rowVersion: null } }),
        `profile ${code}`,
      );
    }
    const claimed = 'Column F is already used by "emissionPointName" and cannot also be "test".';

    await signIn(page);
    await toFile(page, realShapeWorkbook(RealShapeSources));
    await chooseProfile(page, `Pass3b ${blocking} (${blocking})`);
    await page.getByRole('button', { name: /Next: analyse file/ }).click();
    const reason = page.getByTestId('air-no-rows-reason');
    await expect(reason).toContainText('No source was read (sheet', { timeout: 30_000 });
    await expect(reason).toContainText('header rows 4–6');
    await expect(reason).toContainText(claimed);

    await page.getByRole('list', { name: 'Import steps' }).getByRole('button', { name: '2 File' }).click();
    await chooseProfile(page, `Pass3b ${optional} (${optional})`);
    await page.getByRole('button', { name: /Next: analyse file/ }).click();
    await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText(claimed)).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('air-no-rows-reason')).toHaveCount(0);
    await expect(page.getByRole('button', { name: /Next: preview/ })).toBeEnabled();

    await dropProfile(blocking);
    await dropProfile(optional);
  });

  /** UAT pass 3b (P3): «Nothing is saved yet» stayed next to «Profile … saved and selected». */
  test('Air pass3b: after a profile is saved the Mapping subtitle says the data is not imported yet', async ({ page }) => {
    const code = `P3BSAVE_${Stamp}`;
    await signIn(page);
    await toMapping(page, realShapeWorkbook(RealShapeSources));
    const before = 'Check how the columns of the file are matched to fields. Nothing is saved yet.';
    await expect(page.getByText(before, { exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Save as profile…' }).click();
    await page.getByRole('textbox', { name: /^Profile name/ }).fill('Pass3b save e2e');
    await page.getByRole('textbox', { name: /^Profile code/ }).fill(code);
    await page.getByRole('button', { name: 'Save profile', exact: true }).click();
    await expect(page.getByText(`Profile ${code} saved and selected.`)).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText("The profile is saved; the permit's data is not imported yet.", { exact: true })).toBeVisible();
    await expect(page.getByText(before, { exact: true })).toHaveCount(0);

    await dropProfile(code);
  });
});
