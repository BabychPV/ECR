import { expect, test, type APIRequestContext, type APIResponse, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { ensureNaeTree } from './airScope';

/**
 * NAE UX 05.11 - три дефекти живого стенда (людина), РЕАЛЬНИЙ e2e на `NAE_TREE`:
 *  1. Пікер NAE у формі «New row» дозволу пропонує лише вузли джерела (`SRC`): запит `entries?level=SRC`, жодного регіону,
 *     майданчика; підпис «<номер NAE> · <назва>».
 *  2. Поля форми рядка стоять рівно: верх контролів збігається в кожному рядку сітки, хай би що з полів мало підказку
 *     (Select зі «scope hint» поруч із TextInput без неї).
 *  3. Сторінка структури NAE не стрибає: дерево й панель вузла гортаються кожне окремо, вікно лишається на місці.
 *
 * Дані: вузли `NAE_TREE` (регіон › майданчик › 30 джерел) і тип дозволу з таблицею `Sources` заводить сам спек через API;
 * вузли знімаються в `afterAll` (листки першими). Опублікований тип сервер не видаляє (`ECR-PRM-4094`), тож код унікальний, а
 * стенд одноразовий. Локально агентом НЕ запускався (правило «Режим тестів»); CI: workflow `e2e-stand`, `grep` "NAE UX".
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Digits = String(Date.now()).slice(-5);
const NaeTree = 'NAE_TREE';
const SourceLevel = 'SRC';
const RegionName = `E2E UX Region ${Stamp}`;
const SiteName = `E2E UX Site ${Stamp}`;
const SourceCount = 30;

interface TreeNode {
  id: number;
  parentEntryId: number | null;
  levelCode: string | null;
  code: string;
  display: string;
  naeCode?: string | null;
}

let admin: APIRequestContext;
let created: number[] = [];
let sourceIds: number[] = [];
let regionId = 0;
let siteId = 0;
let permitId = 0;
let permitNumber = '';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function treeNodes(api: APIRequestContext): Promise<TreeNode[]> {
  const response = await api.get(`/api/v1/registries/${NaeTree}/tree?includeRetired=true`);

  return (await ok<{ nodes: TreeNode[] }>(response, 'дерево НВ')).nodes;
}

function column(code: string, order: number, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
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
    order,
    ...over,
  };
}

async function signedInContext(browser: Browser, width: number, height: number): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext({ viewport: { width, height } });
  const page = await context.newPage();
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });

  return { context, page };
}

/** Тип дозволу з таблицею `Sources` (текст, список NAE, дата) і дозвіл-чернетка цього типу. */
async function seedPermit(api: APIRequestContext): Promise<{ id: number; number: string }> {
  const code = `E2E_NAEUX_${Stamp}`;
  const made = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await api.post('/api/v1/permit-types', { data: { code, category: 'Water', nameL10n: { en: `E2E NAE UX ${Stamp}` } } }),
    'заведення типу',
  );
  const replaced = await ok<{ rowVersion: string }>(
    await api.put(`/api/v1/permit-types/${code}`, {
      data: {
        category: 'Water',
        nameL10n: made.nameL10n,
        rowVersion: made.rowVersion,
        rules: [],
        tables: [
          {
            code: 'Sources',
            nameL10n: { en: 'Sources (NAE)' },
            order: 1,
            columns: [
              column('Name', 1, { key: true, required: true, nameL10n: { en: 'Name' } }),
              column('Site', 2, { nameL10n: { en: 'Site / production' } }),
              column('Nae', 3, { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: NaeTree, nameL10n: { en: 'NAE' } }),
              column('Issued', 4, { dataType: 'Date', nameL10n: { en: 'Issued on' } }),
              column('Note', 5, { nameL10n: { en: 'Note' } }),
            ],
            rules: [],
          },
        ],
      },
    }),
    'заміна чернетки типу',
  );
  await ok(await api.post(`/api/v1/permit-types/${code}/publish`, { data: { reason: 'E2E NAE UX', rowVersion: replaced.rowVersion } }), 'публікація типу');
  const number = `E2E-NAEUX-${Stamp}`;
  const permit = await ok<{ id: number }>(
    await api.post('/api/v1/permits', {
      data: {
        typeCode: code,
        number,
        location: 'E2E facility',
        issueDate: '2026-01-05',
        validFrom: '2026-01-10',
        validTo: '2027-01-10',
        reportingFlags: { form300: false, form301: false, form302: false, tox: false },
      },
    }),
    'дозвіл',
  );

  return { id: permit.id, number };
}

test.describe('NAE UX', () => {
  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  test.beforeAll(async ({ playwright }, info) => {
    test.setTimeout(240_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    const login = await admin.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } });
    expect(login.ok(), `вхід: ${String(login.status())}`).toBe(true);

    const defId = await ensureNaeTree(admin);
    const definition = await ok<{ codeMode?: string | null }>(await admin.get(`/api/v1/registries/${NaeTree}/definition`), 'опис NAE_TREE');
    const auto = definition.codeMode === 'Auto';
    let counter = 0;
    const make = async (name: string, level: string, parent: number | null, naeCode: string | null): Promise<number> => {
      counter += 1;
      const body = await ok<{ id: number }>(
        await admin.post(`/api/v1/registries/${NaeTree}/entries`, {
          data: {
            id: null,
            registryDefId: defId,
            code: auto ? '' : `UX${Stamp}${String(counter)}`,
            display: { values: { en: name } },
            parentEntryId: parent,
            levelCode: level,
            values: naeCode === null ? {} : { NaeCode: naeCode },
          },
        }),
        `вузол ${name}`,
      );
      created.push(body.id);

      return body.id;
    };
    const region = await make(RegionName, 'REGION', null, null);
    const site = await make(SiteName, 'SITE', region, null);
    regionId = region;
    siteId = site;
    sourceIds = [];
    for (let index = 0; index < SourceCount; index += 1) {
      sourceIds.push(await make(`E2E UX Source ${Stamp} ${String(index + 1).padStart(2, '0')}`, SourceLevel, site, `${Digits}${String(index).padStart(2, '0')}`));
    }

    const seeded = await seedPermit(admin);
    permitId = seeded.id;
    permitNumber = seeded.number;
  });

  test.afterAll(async () => {
    test.setTimeout(240_000);
    const problems: string[] = [];
    const nodes = await treeNodes(admin);
    const parent = new Map(nodes.map((node) => [node.id, node.parentEntryId]));
    const depth = (id: number): number => {
      let d = 0;
      for (let at = parent.get(id) ?? null; at !== null; at = parent.get(at) ?? null) d += 1;

      return d;
    };
    for (const id of [...created].sort((a, b) => depth(b) - depth(a))) {
      const response = await admin.delete(`/api/v1/registries/${NaeTree}/entries/${String(id)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`node ${String(id)}: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
    }
    created = [];
    await admin.dispose();
    expect(problems, 'прибирання стенда').toEqual([]);
  });

  test('NAE UX: the NAE picker of a permit row offers source nodes only', async ({ browser }) => {
    const { context, page } = await signedInContext(browser, 1440, 900);
    await page.goto(`/admin/permits/${String(permitId)}`);
    await expect(page.getByRole('heading', { level: 1, name: permitNumber })).toBeVisible({ timeout: 30_000 });
    await page.getByRole('tab', { name: /^Sources/ }).click();

    // Запит пікера йде до відкриття списку: форма «New row» читає значення одразу.
    const picked = page.waitForRequest((request) => request.url().includes(`/registries/${NaeTree}/entries?`));
    await page.getByRole('button', { name: 'Add row' }).first().click();
    const drawer = page.getByRole('dialog', { name: 'New row' });
    await expect(drawer).toBeVisible();
    const request = await picked;
    const query = new URL(request.url()).searchParams;
    expect(query.get('level'), `запит пікера: ${request.url()}`).toBe(SourceLevel);
    expect(query.get('asOf'), 'темпоральний довідник вимагає asOf').toMatch(/^\d{4}-\d{2}-\d{2}$/);

    await drawer.getByRole('textbox', { name: /^NAE/ }).click();
    const options = page.getByRole('listbox', { name: /^NAE/ }).getByRole('option');
    await expect(options.first()).toBeVisible({ timeout: 30_000 });
    const shown = (await options.allTextContents()).map((text) => text.trim());

    // Еталон - дерево: рівно вузли джерела; підпис «номер · назва».
    const nodes = await treeNodes(admin);
    const expected = new Set(
      nodes
        .filter((node) => node.levelCode === SourceLevel)
        .map((node) => `${node.naeCode ?? node.code} · ${node.display}`),
    );
    expect(shown.length, 'пікер порожній').toBeGreaterThan(0);
    for (const label of shown) {
      expect(label, `підпис «${label}» не схожий на «номер · назва»`).toMatch(/^\S+ · .+/);
      expect(expected.has(label), `«${label}» - не вузол джерела (SRC)`).toBe(true);
    }
    // Регіон і майданчик цього спеку (та будь-які не-SRC вузли) не пропонуються; його джерела - пропонуються.
    const notSource = nodes.filter((node) => node.levelCode !== SourceLevel).map((node) => node.display);
    for (const name of [RegionName, SiteName, ...notSource.slice(0, 50)]) {
      expect(
        shown.some((label) => label.endsWith(` · ${name}`) || label === name),
        `вузол «${name}» не повинен пропонуватись`,
      ).toBe(false);
    }
    expect(shown.some((label) => label.includes(`E2E UX Source ${Stamp} 01`)), 'власне джерело спеку не запропоновано').toBe(true);
    await context.close();
  });

  test('NAE UX: row form fields line up (control tops match in every grid row)', async ({ browser }) => {
    const { context, page } = await signedInContext(browser, 1440, 900);
    await page.goto(`/admin/permits/${String(permitId)}`);
    await expect(page.getByRole('heading', { level: 1, name: permitNumber })).toBeVisible({ timeout: 30_000 });
    await page.getByRole('tab', { name: /^Sources/ }).click();
    await page.getByRole('button', { name: 'Add row' }).first().click();
    const drawer = page.getByRole('dialog', { name: 'New row' });
    await expect(drawer).toBeVisible();
    await expect(drawer.getByRole('textbox', { name: /^NAE/ })).toBeVisible({ timeout: 30_000 });
    // Поле дати вантажиться окремим чанком: чекаємо справжній input, а не скелет.
    await expect(drawer.locator('[data-pp-field="Issued"] input:not([type="hidden"])').first()).toBeVisible({ timeout: 30_000 });

    const cells = drawer.locator('.pp-form-grid > .pp-field');
    await expect(cells).toHaveCount(5);
    const measured: { code: string; cellTop: number; controlTop: number; described: boolean }[] = [];
    for (const cell of await cells.all()) {
      const code = (await cell.getAttribute('data-pp-field')) ?? '';
      const control = cell.locator('input:not([type="hidden"])').first();
      const cellBox = await cell.boundingBox();
      const controlBox = await control.boundingBox();
      expect(cellBox, `клітинка ${code}`).not.toBeNull();
      expect(controlBox, `контрол ${code}`).not.toBeNull();
      measured.push({
        code,
        cellTop: cellBox?.y ?? 0,
        controlTop: controlBox?.y ?? 0,
        described: (await cell.locator('.mantine-InputWrapper-description').count()) > 0,
      });
    }
    // Підказка є лише у списку NAE - саме це й розводило поля.
    expect(measured.find((entry) => entry.code === 'Nae')?.described, 'у списку NAE немає підказки області').toBe(true);
    expect(measured.find((entry) => entry.code === 'Site')?.described, 'у «Site / production» не має бути підказки').toBe(false);

    // 1) Кожне поле: відстань від верху клітинки до контролу однакова (підказка під полем, а не над ним).
    const offsets = measured.map((entry) => entry.controlTop - entry.cellTop);
    for (const [index, offset] of offsets.entries()) {
      expect(Math.abs(offset - (offsets[0] ?? 0)), `поле ${measured[index]?.code ?? ''}: зсув контролу ${String(offset)}px проти ${String(offsets[0])}px`).toBeLessThanOrEqual(1);
    }
    // 2) Кожен рядок сітки (клітинки з одним верхом): верх контролів збігається, ±1 px, для КОЖНОЇ пари.
    const rows = new Map<number, typeof measured>();
    for (const entry of measured) {
      const key = Math.round(entry.cellTop);
      rows.set(key, [...(rows.get(key) ?? []), entry]);
    }
    for (const [top, members] of rows) {
      for (const first of members) {
        for (const second of members) {
          expect(
            Math.abs(first.controlTop - second.controlTop),
            `рядок сітки y=${String(top)}: ${first.code} (${String(first.controlTop)}) і ${second.code} (${String(second.controlTop)})`,
          ).toBeLessThanOrEqual(1);
        }
      }
    }
    await context.close();
  });

  test('NAE UX: the server rejects a NAE lookup value that is not a source node', async () => {
    // Пікер не-SRC вузлів не пропонує, тож пакет шлемо напряму тим самим ендпоінтом, що й форма рядка.
    const permit = await ok<{ tables: { code: string; registryCode: string }[] }>(await admin.get(`/api/v1/permits/${String(permitId)}`), 'дозвіл');
    const registry = permit.tables.find((table) => table.code === 'Sources')?.registryCode ?? '';
    expect(registry, 'реєстр таблиці Sources').not.toBe('');

    for (const [what, entryId] of [['регіон', regionId], ['майданчик', siteId]] as const) {
      const report = await ok<{
        applied: boolean;
        rows: { errors: { field: string | null; errorCode: string; messageKey: string }[] }[];
      }>(
        await admin.post(`/api/v1/registries/${registry}/entries/batch?dryRun=false`, {
          data: {
            items: [
              {
                clientRowId: 'save',
                op: 'upsert',
                id: null,
                code: null,
                baseVersion: null,
                values: { PERMIT: String(permitId), Name: `Rejected ${what}`, Nae: String(entryId) },
              },
            ],
          },
        }),
        `пакет зі значенням «${what}»`,
      );
      // HTTP 200, але applied=false: відмова лежить у rows[i].errors[] на полі стовпця.
      expect(report.applied, `${what}: пакет не повинен застосуватись`).toBe(false);
      const error = report.rows.flatMap((row) => row.errors).find((entry) => entry.field === 'Nae');
      expect(error, `${what}: помилки на полі Nae немає`).toBeDefined();
      expect(error?.errorCode).toBe('ECR-REG-0422');
      expect(error?.messageKey).toBe('err.ECR-REG-0422.naeSourceRequired');
    }
  });

  test('NAE UX: the structure page does not scroll; tree and details scroll on their own', async ({ browser }) => {
    const { context, page } = await signedInContext(browser, 1280, 720);
    await page.goto(`/admin/permits/nae?node=${String(sourceIds[0] ?? 0)}`);
    const form = page.getByRole('form', { name: 'Node attributes' });
    await expect(form).toBeVisible({ timeout: 60_000 });

    // Дерево звужуємо до власних вузлів спеку (пошук розгортає збіги): 32 рядки - більше за вікно дерева.
    await page.getByTestId('tree-search').fill(`E2E UX`);
    const viewport = page.getByTestId('tree-viewport');
    await expect(page.getByRole('treeitem').first()).toBeVisible({ timeout: 30_000 });
    expect(await viewport.evaluate((element) => element.scrollHeight > element.clientHeight), 'дерево має переповнювати своє вікно').toBe(true);

    const side = page.locator('.ecr-tree-side');
    expect(await side.evaluate((element) => element.scrollHeight > element.clientHeight), 'панель вузла має переповнювати своє вікно').toBe(true);
    expect(await page.evaluate(() => window.scrollY), 'на старті вікно не прокручене').toBe(0);

    // Гортаємо панель вузла до низу: вікно лишається на місці, прокручується сама панель.
    await side.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
    await expect.poll(() => side.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
    expect(await page.evaluate(() => window.scrollY), 'панель вузла гортається, вікно - ні').toBe(0);
    expect(await page.evaluate(() => document.documentElement.scrollHeight <= window.innerHeight + 1), 'сторінка не вища за вікно').toBe(true);

    // Дерево: трохи вниз, потім вибір іншого вузла - ні дерево, ні вікно не стрибають.
    await viewport.evaluate((element) => {
      element.scrollTop = 120;
    });
    await expect.poll(() => viewport.evaluate((element) => element.scrollTop)).toBe(120);
    const items = page.getByRole('treeitem');
    const before = new URL(page.url()).searchParams.get('node');
    await items.nth(3).click();
    await expect.poll(() => new URL(page.url()).searchParams.get('node'), { message: 'вибір вузла не змінив адресу' }).not.toBe(before);
    await expect(form).toBeVisible({ timeout: 30_000 });

    expect(await viewport.evaluate((element) => element.scrollTop), 'дерево стрибнуло після вибору вузла').toBe(120);
    expect(await page.evaluate(() => window.scrollY), 'вікно стрибнуло після вибору вузла').toBe(0);
    await context.close();
  });
});
