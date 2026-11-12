import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';
import { SmallSources, workbook, type AirSourceRow } from './airFixture';
import { ensureNaeTree, ensureSubstances, fillScope, prepareScope, removeScopeEntries, type PreparedScope } from './airScope';

/**
 * Контраст кроку «Preview» майстра імпорту AIR (аудит 2026-11, F13 P2 до U-8): позначки серйозності знахідок (`IssuesTable`,
 * `.pi-sev`) і група відсутніх НАЕ — СПРАВЖНІЙ axe `color-contrast` у живому браузері, у світлій І темній темі. vitest/jsdom
 * контрасту не рахує (немає розкладки й обчислених кольорів поверх сірого тла таблиці), а U-8 перевів `.pi-sev` на статусні
 * токени теми (`--mantine-color-statusError-text` / `statusWarning-text`) — саме тут видно, чи вони тримають 4.5:1.
 *
 * Знахідки гарантовано: `SmallSources` дає попередження `regValueEmpty` (порожня т/рік), а додаткове джерело з НАЕ, якого
 * немає в дереві (`MissingNae`), — групу «NAE codes missing in the tree». Тест вимагає, щоб axe справді ОЦІНИВ позначки
 * серйозності (вони в `passes`), а не лише «0 порушень»: інакше порожній чи прихований крок дав би хибно-зелений результат.
 *
 * Дані спек створює сам (вузли дерева НАЕ свого регіону, записи Region/Area, дозвіл AIR) і прибирає в `afterAll`; тема
 * адміністратора — назад light. Запуск: CI workflow `e2e-stand` за ім'ям файлу (`airImportContrast.spec.ts`) або
 * `-g "Air import contrast"`. Локально агентом НЕ запускався (живе вікно W15r) — першим прогоном буде CI.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Region = `E2EAC${Stamp}`;
const Area = 'OFFSHORE';
const NaeTree = 'NAE_TREE';
const Xlsx = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
/** НАЕ, якого в дереві немає: дає групу відсутніх НАЕ на кроці перегляду. */
const MissingNae = '0099';
const Sources: readonly AirSourceRow[] = [
  ...SmallSources,
  { nae: MissingNae, name: 'Unknown stack', type: 'Т', height: 8, substances: [['0301', 'Nitrogen dioxide', 0.2, null, 0.6]] },
];

const state = { permitId: 0, regionNodeId: 0 };
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
  await expect(html).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

interface ContrastRun {
  readonly violations: string[];
  /** Вузли, які axe не зміг оцінити (складне тло) — для позначок серйозності це теж провал. */
  readonly incomplete: string[];
  /** Скільки позначок серйозності axe оцінив і визнав контрастними. */
  readonly severityPasses: number;
}

/** Справжній axe-core (скрипт із node_modules), лише `color-contrast`, увесь документ. */
async function contrast(page: Page): Promise<ContrastRun> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(async () => {
    type Node = { target: unknown[] };
    type Rule = { id: string; nodes: Node[] };
    const axe = (window as unknown as {
      axe: { run: (c: Document, o: object) => Promise<{ violations: Rule[]; incomplete: Rule[]; passes: Rule[] }> };
    }).axe;
    const result = await axe.run(document, { runOnly: { type: 'rule', values: ['color-contrast'] } });
    const list = (rules: Rule[]): string[] => rules.flatMap((r) => r.nodes.map((n) => String(n.target.join(' '))));
    const isSeverity = (target: string): boolean => target.includes('pi-sev');

    return {
      violations: list(result.violations),
      incomplete: list(result.incomplete).filter(isSeverity),
      severityPasses: list(result.passes).filter(isSeverity).length,
    };
  });
}

test.describe('Air import contrast', () => {
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

    // Своє дерево регіону з НАЕ `SmallSources`; `MissingNae` навмисно не заводиться.
    state.regionNodeId = await addNode(admin, treeId, `${Region}_R`, null, 'REGION', { RegionCode: Region });
    const areaNode = await addNode(admin, treeId, `${Region}_A`, state.regionNodeId, 'AREA', { RegionCode: Region, AreaCode: Area });
    const site = await addNode(admin, treeId, `${Region}_S`, areaNode, 'SITE', { RegionCode: Region, AreaCode: Area });
    for (const source of SmallSources) {
      await addNode(admin, treeId, `${Region}_N${source.nae}`, site, 'SRC', { NaeCode: source.nae, RegionCode: Region, AreaCode: Area });
    }
    scope = await prepareScope(admin, Region, Area, `C${Stamp}`);

    state.permitId = (await ok<{ id: number }>(
      await admin.post('/api/v1/permits', {
        data: { typeCode: 'AIR', number: `E2E-AC-${Stamp}`, location: null, issueDate: null, validFrom: '2026-01-01', validTo: '2031-01-01', reportingFlags: null },
      }),
      'AIR permit',
    )).id;
  });

  test.afterAll(async ({ browser }) => {
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

    // Тема адміністратора назад на light: вподобання на сервері перекриває localStorage.
    const context = await browser.newContext();
    const page = await context.newPage();
    await signIn(page);
    await setScheme(page, 'light');
    await context.close();
    await admin.dispose();
    expect(problems, 'cleanup').toEqual([]);
  });

  for (const scheme of ['light', 'dark'] as const) {
    test(`Air import contrast: preview findings (IssuesTable) and missing NAE - axe color-contrast ${scheme}`, async ({ browser }) => {
      const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
      const page = await context.newPage();
      try {
        await page.emulateMedia({ colorScheme: scheme });
        await signIn(page);
        await setScheme(page, scheme);

        const prepared = scope;
        if (prepared === null) throw new Error('scope not prepared');
        await page.goto(`/admin/permits/${String(state.permitId)}/import`);
        await expect(page.getByRole('heading', { name: 'Region and area', level: 2 })).toBeVisible({ timeout: 30_000 });
        await fillScope(page, prepared);
        await page.getByRole('button', { name: 'Next', exact: true }).click();

        await expect(page.getByRole('heading', { name: 'File', level: 2 })).toBeVisible({ timeout: 30_000 });
        await page.locator('input[type="file"]').setInputFiles({ name: 'air-contrast.xlsx', mimeType: Xlsx, buffer: workbook(Sources) });
        await page.getByRole('button', { name: /Next: analyse file/ }).click();
        await expect(page.getByRole('heading', { name: 'Mapping', level: 2 })).toBeVisible({ timeout: 30_000 });
        await page.getByRole('button', { name: /Next: preview/ }).click();
        await expect(page.getByRole('heading', { name: 'Preview', level: 2 })).toBeVisible({ timeout: 30_000 });

        // Знахідки є: попередження в таблиці знахідок і група відсутніх НАЕ.
        await expect(page.locator('.pi-sev[data-severity="Warning"]').first()).toBeVisible({ timeout: 30_000 });
        await expect(page.getByTestId('air-missing-group')).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);

        const run = await contrast(page);
        expect.soft(run.violations, `color-contrast ${scheme}`).toEqual([]);
        expect.soft(run.incomplete, `severity badges axe could not evaluate (${scheme})`).toEqual([]);
        expect(run.severityPasses, `severity badges evaluated by axe (${scheme})`).toBeGreaterThan(0);
      } finally {
        await context.close();
      }
    });
  }
});
