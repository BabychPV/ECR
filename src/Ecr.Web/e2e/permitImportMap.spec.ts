import { fileURLToPath } from 'node:url';
import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';
import { AreaRegistry, ensureNaeTree, RegionRegistry } from './airScope';

/**
 * Вставка й імпорт рядків дозволу із зіставленням стовпців (людина 06.11): на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжнє розв'язання значень за кодом і назвою (`POST …/import/lookups`) у трьох різних
 * реєстрах (NAE_TREE, LAND_REGION, AIR_AREA), Region/Area, виведені з предків джерела в справжньому дереві НАЕ, блок «все або
 * нічого» з переліком нерозв'язаних значень, запис через `entries/batch` і `.xlsx`, прочитаний сервером, з вибором аркуша.
 *
 * Детермінізм: усе, що створює вставка, має мітку прогону (тип, вузли, записи регіону/району). Книга-фікстура статична
 * (`fixtures/permit-import-two-sheets.xlsx`: аркуш «Readme» першим, «Sources» другим; номер НАЕ `E2EIMX1`) - її вузол і регіон
 * створюються, якщо їх немає (повторний прогін на тому самому стенді їх перевикористовує).
 *
 * Запуск (стенд - `tools/e2e-stand.ps1`; на CI - workflow `e2e-stand`): `npx playwright test -g "Permit import mapping"`.
 * Локально агентом НЕ запускався (правило «Режим тестів»).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const NaeTree = 'NAE_TREE';
const Workbook = fileURLToPath(new URL('./fixtures/permit-import-two-sheets.xlsx', import.meta.url));
/** Фіксовані вузол і регіон книги-фікстури. */
const XlsxNae = 'E2EIMX1';
const XlsxRegion = 'E2EIMXR';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);
  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

interface TreeNode {
  id: number;
  code: string;
}

async function treeNodes(api: APIRequestContext): Promise<TreeNode[]> {
  return (await ok<{ nodes: TreeNode[] }>(await api.get(`/api/v1/registries/${NaeTree}/tree`), 'дерево НАЕ')).nodes;
}

async function addNode(
  api: APIRequestContext,
  treeId: number,
  code: string,
  display: string,
  parent: number | null,
  level: string,
  values: Record<string, string>,
): Promise<number> {
  const created = await ok<{ id: number }>(
    await api.post(`/api/v1/registries/${NaeTree}/entries`, {
      data: { registryDefId: treeId, code, display: { values: { en: display } }, parentEntryId: parent, levelCode: level, values },
    }),
    `вузол ${code}`,
  );
  return created.id;
}

/** Вузол за кодом або новий. */
async function ensureNode(
  api: APIRequestContext,
  treeId: number,
  code: string,
  display: string,
  parent: number | null,
  level: string,
  values: Record<string, string>,
): Promise<number> {
  const found = (await treeNodes(api)).find((node) => node.code === code);
  return found?.id ?? addNode(api, treeId, code, display, parent, level, values);
}

/** Реєстр без полів (код і назва записів), якщо його немає; id опису. */
async function ensureRegistry(api: APIRequestContext, code: string): Promise<number> {
  if ((await api.get(`/api/v1/registries/${code}/definition`)).status() === 404) {
    await ok(await api.post('/api/v1/registries', { data: { code, nameL10n: { en: code }, isTemporal: false } }), `реєстр ${code}`);
  }
  return (await ok<{ id: number }>(await api.get(`/api/v1/registries/${code}/definition`), code)).id;
}

/** Запис реєстру з англійською назвою; наявний з тим самим кодом перевикористовується. */
async function ensureEntry(api: APIRequestContext, registry: string, code: string, name: string): Promise<number> {
  const defId = await ensureRegistry(api, registry);
  const created = await api.post(`/api/v1/registries/${registry}/entries`, {
    data: { registryDefId: defId, code, display: { values: { en: name } }, parentEntryId: null, levelCode: null, values: {} },
  });
  if (created.status() < 300) return ((await created.json()) as { id: number }).id;
  const rows = await ok<{ items: { id: number; code: string }[] }>(
    await api.get(`/api/v1/registries/${registry}/rows?asOf=2026-01-01&limit=500&q=${encodeURIComponent(code)}`),
    `пошук ${registry}/${code}`,
  );
  const found = rows.items.find((row) => row.code === code);
  expect(found, `запис ${registry}/${code}: ${String(created.status())} ${await created.text()}`).toBeDefined();
  return found?.id ?? 0;
}

function column(code: string, name: string, order: number, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    code,
    nameL10n: { en: name },
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

/** Тип із таблицею «Sources (NAE)»: NAE (джерело), Region (обов'язковий), Area, назва, висота; дозвіл-чернетка. */
async function seedPermit(api: APIRequestContext, stamp: string): Promise<{ id: number; registry: string }> {
  const code = `E2E_IM_${stamp}`;
  const created = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await api.post('/api/v1/permit-types', { data: { code, category: 'Air', nameL10n: { en: `E2E import map ${stamp}` } } }),
    'створення типу',
  );
  const lookup = (registry: string, required: boolean, key = false): Record<string, unknown> => ({
    dataType: 'Lookup',
    listScope: 'SharedRegistry',
    listRegistryCode: registry,
    required,
    key,
  });
  const draft = await ok<{ rowVersion: string }>(
    await api.put(`/api/v1/permit-types/${code}`, {
      data: {
        category: 'Air',
        nameL10n: created.nameL10n,
        rowVersion: created.rowVersion,
        rules: [],
        tables: [
          {
            code: 'SRC',
            nameL10n: { en: 'Sources (NAE)' },
            order: 1,
            columns: [
              column('NAE', 'NAE', 1, lookup(NaeTree, true, true)),
              column('REGION', 'Region', 2, lookup(RegionRegistry, true)),
              column('AREA', 'Area', 3, lookup(AreaRegistry, false)),
              column('SOURCE_NAME', 'Source name', 4, { required: true }),
              column('HEIGHT', 'Height, m', 5, { dataType: 'Decimal', precision: 18, scale: 3 }),
            ],
            rules: [],
          },
        ],
      },
    }),
    'заміна чернетки',
  );
  await ok(await api.post(`/api/v1/permit-types/${code}/publish`, { data: { reason: 'E2E import mapping type', rowVersion: draft.rowVersion } }), 'публікація');
  const permit = await ok<{ id: number }>(
    await api.post('/api/v1/permits', {
      data: {
        typeCode: code,
        number: `E2E-IM-${stamp}`,
        location: 'E2E',
        issueDate: '2026-01-05',
        validFrom: '2026-01-10',
        validTo: '2027-01-10',
        reportingFlags: { form300: false, form301: false, form302: false, tox: false },
      },
    }),
    'дозвіл',
  );
  return { id: permit.id, registry: `PRM_${code}_SRC` };
}

interface StoredRow {
  values: Record<string, { value: string | null } | undefined>;
}

async function storedRows(api: APIRequestContext, registry: string, permitId: number): Promise<StoredRow[]> {
  const today = new Date().toISOString().slice(0, 10);
  return (
    await ok<{ items: StoredRow[] }>(
      await api.get(`/api/v1/registries/${registry}/rows?asOf=${today}&parentEntryId=${String(permitId)}&limit=500`),
      'рядки таблиці',
    )
  ).items;
}

test.describe('Permit import mapping', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('вставка з переставленими стовпцями, значення за кодом і назвою, Region/Area з НАЕ; .xlsx з вибором аркуша', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    const api = page.request;
    const stamp = String(Date.now());

    // Дерево: регіон → район → майданчик → два джерела; записи регіону й району з назвами кодів дерева.
    const treeId = await ensureNaeTree(api);
    const regionCode = `IMR${stamp}`;
    const areaCode = `IMA${stamp}`;
    // AN-158 (31d85b473): код району вузла НАЕ ОБИРАЄТЬСЯ зі списку AIR_AREA (активний запис, збіг за кодом чи англ. назвою) —
    // тож запис району заводиться ДО вузлів, тим самим API записів списку, і з кодом = коду району дерева.
    const areaEntry = await ensureEntry(api, AreaRegistry, areaCode, areaCode);
    const regionNode = await addNode(api, treeId, `E2EIM_R_${stamp}`, `Import region ${stamp}`, null, 'REGION', { RegionCode: regionCode });
    const areaNode = await addNode(api, treeId, `E2EIM_A_${stamp}`, `Import area ${stamp}`, regionNode, 'AREA', { RegionCode: regionCode, AreaCode: areaCode });
    const site = await addNode(api, treeId, `E2EIM_S_${stamp}`, `Import site ${stamp}`, areaNode, 'SITE', { RegionCode: regionCode, AreaCode: areaCode });
    await addNode(api, treeId, `E2EIM_N1_${stamp}`, `Boiler ${stamp}`, site, 'SRC', { NaeCode: `IM1${stamp}`, RegionCode: regionCode, AreaCode: areaCode });
    await addNode(api, treeId, `E2EIM_N2_${stamp}`, `Heater ${stamp}`, site, 'SRC', { NaeCode: `IM2${stamp}`, RegionCode: regionCode, AreaCode: areaCode });
    const regionEntry = await ensureEntry(api, RegionRegistry, `E2EIMR_${stamp}`, regionCode);

    // Вузол книги-фікстури (фіксований, без району).
    const xlsxRegionNode = await ensureNode(api, treeId, `${XlsxRegion}_R`, `Xlsx region`, null, 'REGION', { RegionCode: XlsxRegion });
    const xlsxSite = await ensureNode(api, treeId, `${XlsxRegion}_S`, `Xlsx site`, xlsxRegionNode, 'SITE', { RegionCode: XlsxRegion });
    await ensureNode(api, treeId, `${XlsxRegion}_N`, `Xlsx boiler`, xlsxSite, 'SRC', { NaeCode: XlsxNae, RegionCode: XlsxRegion });
    const xlsxRegionEntry = await ensureEntry(api, RegionRegistry, `${XlsxRegion}_E`, XlsxRegion);

    const { id, registry } = await seedPermit(api, stamp);
    await page.goto(`/admin/permits/${String(id)}`);
    await page.getByRole('tab', { name: /^Sources \(NAE\)/ }).click();
    await expect(page.getByText('No rows in Sources (NAE) yet')).toBeVisible({ timeout: 30_000 });

    // 1. Вставка: стовпці в іншому порядку, із заголовком; NAE за номером і за назвою, Region за кодом, Area за назвою;
    //    третій рядок - невідоме НАЕ.
    await page.getByRole('button', { name: 'Paste from Excel…' }).click();
    const paste = page.getByRole('dialog', { name: 'Paste rows into Sources (NAE)' });
    await expect(paste).toBeVisible();
    const rows = paste.getByRole('textbox', { name: 'Rows' });
    const header = 'Height, m\tSource name\tNAE\tRegion\tArea';
    await rows.fill([header, `12,5\tStack 1\tIM1${stamp}\tE2EIMR_${stamp}\t${areaCode}`, `7\tStack 2\theater ${stamp}\t\t`, `3\tStack 3\tNOPE-${stamp}\t\t`].join('\n'));

    await expect(paste.getByRole('checkbox', { name: 'The first line is a header (skip it)' })).toBeChecked();
    for (const [file, target] of [['Height, m', 'HEIGHT'], ['Source name', 'SOURCE_NAME'], ['NAE', 'NAE'], ['Region', 'REGION'], ['Area', 'AREA']] as const) {
      await expect(paste.getByRole('combobox', { name: `Table column for «${file}»` })).toHaveValue(target);
    }

    // 2. Нерозв'язане значення названо до будь-якого запису; «все або нічого».
    const unresolved = paste.getByRole('list', { name: 'Values that could not be matched' });
    await expect(unresolved.getByRole('listitem')).toHaveText([`NAE: «NOPE-${stamp}» — no entry has this code or name (rows: 1)`], { timeout: 30_000 });
    await expect(paste.getByText('Nothing is added while there are problems. Fix them and check the rows again.')).toBeVisible();
    await expect(paste.getByRole('button', { name: 'Check rows' })).toBeDisabled();

    // 3. Виправлення: без невідомого рядка - Region/Area другого рядка виводяться з джерела НАЕ.
    await rows.fill([header, `12,5\tStack 1\tIM1${stamp}\tE2EIMR_${stamp}\t${areaCode}`, `7\tStack 2\theater ${stamp}\t\t`].join('\n'));
    await expect(paste.getByText('2 rows can be added to Sources (NAE); 0 need fixing before anything is added.')).toBeVisible({ timeout: 30_000 });
    const derived = paste.getByRole('list', { name: 'Filled from the NAE source: 2' });
    await expect(derived.getByRole('listitem')).toHaveText([`Row 2, Region: ${regionCode} (from NAE)`, `Row 2, Area: ${areaCode} (from NAE)`]);

    await paste.getByRole('button', { name: 'Check rows' }).click();
    await expect(paste.getByText('2 rows pass the check, 0 are rejected by the server')).toBeVisible({ timeout: 30_000 });
    await paste.getByRole('button', { name: 'Add 2 rows' }).click();
    await expect(paste).toBeHidden({ timeout: 30_000 });
    await expect(page.getByRole('row', { name: /Stack 2/ })).toBeVisible({ timeout: 30_000 });

    const written = await storedRows(api, registry, id);
    expect(written.map((row) => [row.values['SOURCE_NAME']?.value, row.values['REGION']?.value, row.values['AREA']?.value]).sort()).toEqual([
      ['Stack 1', String(regionEntry), String(areaEntry)],
      ['Stack 2', String(regionEntry), String(areaEntry)],
    ]);

    // 4. «Import file…» з .xlsx: сервер читає перший аркуш, людина обирає «Sources»; Region - з НАЕ, Area лишається порожнім.
    await page.getByRole('button', { name: 'Import file…' }).click();
    const importDialog = page.getByRole('dialog', { name: 'Paste rows into Sources (NAE)' });
    await expect(importDialog).toBeVisible();
    await importDialog.locator('input[type="file"]').setInputFiles(Workbook);
    // exact: рядок зіставлення для заголовка «…on the sheet «Sources».» з аркуша Readme теж містить «sheet».
    const sheet = importDialog.getByRole('combobox', { name: 'Sheet', exact: true });
    await expect(sheet).toHaveValue('Readme', { timeout: 30_000 });
    await sheet.selectOption('Sources');
    await expect(importDialog.getByRole('combobox', { name: 'Table column for «NAE number»' })).toHaveValue('', { timeout: 30_000 });
    // «NAE number» не впізнано за назвою - людина зіставляє вручну.
    await importDialog.getByRole('combobox', { name: 'Table column for «NAE number»' }).selectOption('NAE');
    await expect(importDialog.getByText('1 rows can be added to Sources (NAE); 0 need fixing before anything is added.')).toBeVisible({ timeout: 30_000 });
    await importDialog.getByRole('button', { name: 'Check rows' }).click();
    await expect(importDialog.getByText('1 rows pass the check, 0 are rejected by the server')).toBeVisible({ timeout: 30_000 });
    await importDialog.getByRole('button', { name: 'Add 1 rows' }).click();
    await expect(importDialog).toBeHidden({ timeout: 30_000 });
    await expect(page.getByRole('row', { name: /Xlsx stack/ })).toBeVisible({ timeout: 30_000 });

    const all = await storedRows(api, registry, id);
    const fromBook = all.find((row) => row.values['SOURCE_NAME']?.value === 'Xlsx stack');
    expect(fromBook?.values['REGION']?.value).toBe(String(xlsxRegionEntry));
    expect(Number(fromBook?.values['HEIGHT']?.value)).toBe(12.5);
  });
});
