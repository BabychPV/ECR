import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';
import { SmallSources, workbook } from './airFixture';
import {
  ensureNaeTree,
  ensureSubstances,
  fillScope,
  prepareScope,
  removeScopeEntries,
  scopeFields,
  type PreparedScope,
} from './airScope';

/**
 * Air params (AIR-ON-AIR, людина 02.11: «таблиця параметрів відноситься до пермиту локації та регіону … ця кнопка має бути тут»,
 * «Region і Area береться з Registrates») - РЕАЛЬНИЙ e2e: дозвіл типу AIR → «Import parameters…» поруч із Renew/More → крок
 * «Region and area» (Region - запис довідника, Area - лише ділянки цього регіону) → файл → відповідність → перегляд → apply →
 * вкладка «Sources & parameters» показує рядки. axe light/dark на 1440/500. Дані спек створює сам і прибирає в `afterAll`
 * (дозволи, вузли дерева НАЕ, записи Region/Area, тимчасові роль і користувач; тема адміністратора - назад light).
 *
 * Запуск: `tools/e2e-stand.ps1 -Grep "Air params"`; CI - workflow `e2e-stand` з `grep`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Region = `E2EAP${Stamp}`;
const Area = 'OFFSHORE';
const NaeTree = 'NAE_TREE';
const ViewerRole = `E2EAPV${Stamp}`;
const Viewer = { user: `e2e-ap-viewer-${Stamp.toLowerCase()}`, issued: 'E2E-Ap-Vi3wer-2026!', password: 'E2E-Ap-Vi3wer-Work-2026!' };
const Xlsx = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

const state = { permitId: 0, permitNumber: `E2E-AP-${Stamp}`, regionNodeId: 0, roleId: 0, userId: 0 };
const createdPermits: number[] = [];
let scope: PreparedScope | null = null;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function addNode(api: APIRequestContext, registryId: number, code: string, parent: number | null, level: string, values: Record<string, string>): Promise<number> {
  const created = await ok<{ id: number }>(
    await api.post(`/api/v1/registries/${NaeTree}/entries`, {
      data: { registryDefId: registryId, code, display: { values: { en: code } }, parentEntryId: parent, levelCode: level, values },
    }),
    `node ${code}`,
  );

  return created.id;
}

async function signIn(page: Page, user: { user: string; password: string }): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
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
  await expect(html).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
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

test.describe('Air params', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });
  let admin: APIRequestContext;

  test.beforeAll(async ({ playwright }, info) => {
    test.setTimeout(180_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await ok(await admin.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } }), 'admin login');

    const treeId = await ensureNaeTree(admin);
    await ensureSubstances(admin);
    // Ідемпотентне (fbec26fee; старт стенда вже провізіонував тип AIR від імені системи).
    await ok(await admin.post('/api/v1/air-limits/provision'), 'provision');

    // Своє дерево регіону з усіма НАЕ файлу: перегляд без відсутніх НАЕ.
    state.regionNodeId = await addNode(admin, treeId, `${Region}_R`, null, 'REGION', { RegionCode: Region });
    const areaNode = await addNode(admin, treeId, `${Region}_A`, state.regionNodeId, 'AREA', { RegionCode: Region, AreaCode: Area });
    const site = await addNode(admin, treeId, `${Region}_S`, areaNode, 'SITE', { RegionCode: Region, AreaCode: Area });
    for (const source of SmallSources) {
      await addNode(admin, treeId, `${Region}_N${source.nae}`, site, 'SRC', { NaeCode: source.nae, RegionCode: Region, AreaCode: Area });
    }

    // Region/Area за видом колонок типу AIR: Text — код регіону дерева НАЕ; Registry — запис із такою назвою; Area — OFFSHORE.
    scope = await prepareScope(admin, Region, Area, `P${Stamp}`);

    const permit = await ok<{ id: number }>(
      await admin.post('/api/v1/permits', {
        data: { typeCode: 'AIR', number: state.permitNumber, location: null, issueDate: null, validFrom: '2026-01-01', validTo: '2031-01-01', reportingFlags: null },
      }),
      'AIR permit',
    );
    state.permitId = permit.id;
    createdPermits.push(permit.id);

    // Тимчасовий користувач лише з переглядом: кнопки імпорту в нього немає.
    state.roleId = (await ok<{ roleId: number }>(
      await admin.post('/api/v1/roles', { data: { code: ViewerRole, nameL10n: { en: 'E2E air params viewer' }, permissionCodes: ['Registry.View'] } }),
      'viewer role',
    )).roleId;
    state.userId = (await ok<{ userId: number }>(
      await admin.post('/api/v1/users', {
        data: { userName: Viewer.user, provider: 'Local', sid: null, displayName: 'E2E air params viewer', initialPassword: Viewer.issued, roleCodes: [ViewerRole] },
      }),
      'viewer user',
    )).userId;
    const viewerApi = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await ok(await viewerApi.post('/api/v1/login/local', { data: { userName: Viewer.user, password: Viewer.issued } }), 'viewer login');
    await ok(await viewerApi.post('/api/v1/auth/change-password', { data: { currentPassword: Viewer.issued, newPassword: Viewer.password } }), 'viewer password');
    await viewerApi.dispose();
  });

  test.afterAll(async ({ browser }) => {
    test.setTimeout(180_000);
    const problems: string[] = [];
    for (const id of createdPermits) {
      const response = await admin.delete(`/api/v1/permits/${String(id)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`permit ${String(id)}: ${String(response.status())}`);
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
    if (state.userId !== 0) await admin.put(`/api/v1/users/${String(state.userId)}/roles`, { data: { roleCodes: [] } });
    if (state.roleId !== 0) {
      const response = await admin.delete(`/api/v1/roles/${String(state.roleId)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`role: ${String(response.status())}`);
    }

    // Тема адміністратора назад на light: вподобання на сервері перекриває localStorage.
    const context = await browser.newContext();
    const page = await context.newPage();
    await signIn(page, Admin);
    await setScheme(page, 'light');
    await context.close();
    await admin.dispose();
    expect(problems, 'cleanup').toEqual([]);
  });

  test('Air params: Import parameters… from the AIR permit header - region, area, file, mapping, preview, apply, tab rows', async ({ page }) => {
    await signIn(page, Admin);
    await page.goto(`/admin/permits/${String(state.permitId)}`);
    await expect(page.getByRole('tab', { name: 'Sources & parameters' })).toBeVisible({ timeout: 30_000 });
    // Кнопка в шапці дозволу, поруч із Renew/More (як в Excel).
    await page.getByRole('link', { name: 'Import parameters…' }).first().click();
    await expect(page).toHaveURL(new RegExp(`/admin/permits/${String(state.permitId)}/import`));

    // 1 Region and area: номер дозволу, Region за видом колонки, Area — лише після регіону.
    const prepared = scope;
    if (prepared === null) throw new Error('scope not prepared');
    await expect(page.getByRole('heading', { name: 'Region and area', level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('textbox', { name: 'Permit number' })).toHaveValue(state.permitNumber);
    await expect(page.getByRole('textbox', { name: /^Area/ })).toHaveCount(0);
    await fillScope(page, prepared);
    await expect(page.getByText('Region or area not in the NAE tree')).toHaveCount(0);
    await page.getByRole('button', { name: 'Next', exact: true }).click();

    // 2 File → 3 Mapping → 4 Preview.
    await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
    await page.locator('input[type="file"]').setInputFiles({ name: 'air-params.xlsx', mimeType: Xlsx, buffer: workbook(SmallSources) });
    await page.getByRole('button', { name: /Next: analyse file/ }).click();
    await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 30_000 });
    await page.getByRole('button', { name: /Next: preview/ }).click();
    await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText('Region or area not in the NAE tree')).toHaveCount(0);
    await expect(page.getByRole('heading', { name: /NAE codes missing in the tree/ })).toHaveCount(0);

    // 5 Apply → Result → вкладка показує рядки.
    const apply = page.getByRole('button', { name: /Apply import/ });
    await expect(apply).toBeEnabled();
    await apply.click();
    await expect(page.getByRole('heading', { name: 'Result', level: 2 })).toBeVisible({ timeout: 60_000 });

    await page.goto(`/admin/permits/${String(state.permitId)}?tab=air`);
    await expect(page.getByRole('tab', { name: 'Sources & parameters', selected: true })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('air-counts')).toHaveText('3 sources · 4 substance rows', { timeout: 30_000 });
    await expect(page.getByTestId('air-source-row')).toHaveCount(3);
    // Колонка «Region / area» картки — область імпорту рядка.
    await expect(page.getByTestId('air-source-scope').first()).toContainText(prepared.regionKind === 'Text' ? Region : 'E2ER_');

    // Сервер записав область імпорту на рядках (запис Region, коли колонка — довідник).
    const card = await ok<{ sources: { region?: string | null; regionEntryId?: number | null }[] }>(
      await admin.get(`/api/v1/permits/${String(state.permitId)}/air-limits`),
      'card',
    );
    expect(card.sources).toHaveLength(3);
    if (prepared.regionEntryId !== null) expect(card.sources.every((s) => s.regionEntryId === prepared.regionEntryId)).toBe(true);
  });

  test('Air params: REGION/AREA are ordinary AIR_SOURCES columns in the permit type editor, with their kind', async ({ page }) => {
    // The kind is configured in the existing column editor (String = Text; Lookup + shared registry = Registry; Lookup + type
    // list = List); the wizard reads the same columns through scope-fields. Read-only here: the live AIR type is not changed.
    const fields = await scopeFields(admin);
    await signIn(page, Admin);
    for (const field of [fields.region, fields.area]) {
      await page.goto(`/admin/permits/types/AIR?t=AIR_SOURCES&panel=${field.columnCode}`);
      const inspector = page.getByTestId('column-inspector');
      await expect(inspector).toBeVisible({ timeout: 30_000 });
      await expect(inspector.getByRole('textbox', { name: 'Code' })).toHaveValue(field.columnCode);
      const type = inspector.getByRole('textbox', { name: 'Type', exact: true });
      if (field.kind === 'Text') {
        await expect(type).toHaveValue('Text');
        continue;
      }
      await expect(type).toHaveValue('Pick from a list');
      const from = inspector.getByRole('textbox', { name: 'Choices come from' });
      if (field.kind === 'Registry') {
        await expect(from).toHaveValue('A shared registry (used by many types)');
        await expect(inspector.getByRole('textbox', { name: 'Registry' })).toHaveValue(new RegExp(field.registryCode ?? '-'));
      } else {
        await expect(from).toHaveValue('A list only for this permit type');
        await expect(inspector.getByLabel('Values, one per line')).not.toHaveValue('');
      }
    }
  });

  test('Air params: a viewer sees the tab but no Import parameters…', async ({ browser }) => {
    const context = await browser.newContext();
    const page = await context.newPage();
    try {
      await signIn(page, { user: Viewer.user, password: Viewer.password });
      await page.goto(`/admin/permits/${String(state.permitId)}?tab=air`);
      await expect(page.getByTestId('air-counts')).toBeVisible({ timeout: 30_000 });
      await expect(page.getByRole('link', { name: 'Import parameters…' })).toHaveCount(0);
    } finally {
      await context.close();
    }
  });

  for (const scheme of ['light', 'dark'] as const) {
    for (const width of [1440, 500]) {
      test(`Air params: axe ${scheme} ${String(width)} - tab and scope step`, async ({ browser }) => {
        const context = await browser.newContext({ viewport: { width, height: 900 } });
        const page = await context.newPage();
        try {
          await page.emulateMedia({ colorScheme: scheme });
          await signIn(page, Admin);
          await setScheme(page, scheme);
          await page.goto(`/admin/permits/${String(state.permitId)}?tab=air`);
          await expect(page.getByTestId('air-counts')).toBeVisible({ timeout: 30_000 });
          await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
          expect.soft(await axe(page), `tab ${scheme} ${String(width)}`).toEqual([]);
          const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
          expect.soft(overflow, 'horizontal overflow').toBeLessThanOrEqual(1);

          await page.goto(`/admin/permits/${String(state.permitId)}/import`);
          await expect(page.getByRole('heading', { name: 'Region and area', level: 2 })).toBeVisible({ timeout: 30_000 });
          if (scope !== null) await fillScope(page, scope);
          await expect(page.getByRole('textbox', { name: /^Area/ })).toBeVisible();
          expect.soft(await axe(page), `scope step ${scheme} ${String(width)}`).toEqual([]);
        } finally {
          await context.close();
        }
      });
    }
  }
});
