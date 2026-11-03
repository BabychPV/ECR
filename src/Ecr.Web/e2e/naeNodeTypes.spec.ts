import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

/**
 * Типи вузлів структури NAE на живому стенді (03.11): діалог «New node» за рівнем - Region > On/Offshore > Area > Unit >
 * Equipment; назви трьома мовами; номер NAE обов'язковий і нормалізується (`36_pod` -> `0036_pod`); дубль номера в тій самій
 * області - справжня відмова сервера на полі; регіон і район - текст, а не поле вводу; рівень поля в конструкторі довідника;
 * axe light+dark на 1440 і 500 px.
 *
 * ⚠ Довідник - власний `E2E_NAET_<час>` (сторінка приймає `?registry=<код>`): спільний `NAE_TREE` не чіпаємо. Вузли `afterAll`
 * видаляє (листки першими), тему адміністратора повертає на light; довідник API видалити не дає - лишається порожній.
 *
 * Запуск: `-Grep "NAE node types"`.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Registry = `E2E_NAET_${String(Date.now())}`;
// Номер без нулів: сервер доповнює його до чотирьох цифр. Регіон свій (свіжий), тож повторний прогін дубля не дає.
const NaeInput = '36_pod';
const NaeStored = '0036_pod';

interface TreeNode {
  id: number;
  display: string;
  levelCode: string | null;
  parentEntryId: number | null;
}

let admin: APIRequestContext;
let created = false;

async function signIn(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  await page.goto('/login');
  await page.evaluate((colour: string) => localStorage.setItem('mantine-color-scheme-value', colour), scheme);
  await page.reload();
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.userName);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
  // Тема з сервера перемагає localStorage: пишемо налаштування, перезавантажуємо й ПЕРЕВІРЯЄМО атрибут.
  const put = await page.request.put('/api/v1/me/preferences/theme', { headers: { 'Content-Type': 'application/json' }, data: JSON.stringify(scheme) });
  expect(put.ok(), `тема: ${String(put.status())}`).toBe(true);
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

async function readTree(): Promise<TreeNode[]> {
  const response = await admin.get(`/api/v1/registries/${Registry}/tree?includeRetired=true`);
  expect(response.ok(), `дерево: ${String(response.status())}`).toBe(true);

  return ((await response.json()) as { nodes: TreeNode[] }).nodes;
}

async function entryValues(id: number): Promise<Record<string, string | null>> {
  const response = await admin.get(`/api/v1/registries/${Registry}/entries/${String(id)}`);
  expect(response.ok(), `запис ${String(id)}`).toBe(true);

  return ((await response.json()) as { values: Record<string, string | null> }).values;
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

const url = (node?: number): string => `/admin/permits/nae?registry=${Registry}${node === undefined ? '' : `&node=${String(node)}`}`;
const dialogOf = (page: Page) => page.getByRole('dialog', { name: 'New node' });

/** Заповнює назви трьома мовами; ручний код (якщо довідник його вимагає) дописується. */
async function fillNames(page: Page, name: string, code: string): Promise<void> {
  const dialog = dialogOf(page);
  await expect(dialog).toBeVisible({ timeout: 15_000 });
  const codeField = dialog.getByRole('textbox', { name: 'Code', exact: true });
  if ((await codeField.count()) > 0) await codeField.fill(code);
  await dialog.getByLabel(/^Name · English/).fill(name);
  await dialog.getByLabel(/^Name · Русский/).fill(`${name} RU`);
  await dialog.getByLabel(/^Name · Қазақша/).fill(`${name} KZ`);
}

/** Зберігає й чекає, поки панель покаже НОВИЙ вузол: лише тоді «Add child» належить йому, а не попередньому. */
async function save(page: Page, name: string): Promise<void> {
  const dialog = dialogOf(page);
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toBeHidden({ timeout: 20_000 });
  await expect(page.getByTestId('tree-panel')).toContainText(name, { timeout: 20_000 });
}

test.describe('NAE node types', () => {
  test.describe.configure({ timeout: 300_000, mode: 'serial' });

  test.beforeAll(async ({ playwright }, info) => {
    test.setTimeout(180_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    const login = await admin.post('/api/v1/login/local', { data: { userName: Admin.userName, password: Admin.password } });
    expect(login.ok()).toBe(true);

    const make = await admin.post('/api/v1/registries', { data: { code: Registry, nameL10n: { en: `E2E NAE node types ${Stamp}` }, isTemporal: false } });
    expect(make.status(), `довідник: ${await make.text()}`).toBe(201);
    created = true;
    const level = (code: string, en: string, over: Record<string, unknown> = {}) => ({ code, isOptional: false, name: { values: { en } }, ...over });
    const levels = await admin.put(`/api/v1/registries/${Registry}/hierarchy`, {
      data: {
        isHierarchical: true,
        levels: [
          level('REGION', 'Region'),
          level('AREA', 'On/Offshore'),
          level('SITE', 'Area', { allowsSelfNesting: true }),
          level('EQUIP', 'Unit'),
          level('SRC', 'Equipment'),
        ],
      },
    });
    expect(levels.status(), `рівні: ${await levels.text()}`).toBe(200);

    const definition = (await (await admin.get(`/api/v1/registries/${Registry}/definition`)).json()) as { definitionVersion: number };
    const field = (code: string, ordinal: number, over: Record<string, unknown>) => ({
      id: null, code, nameL10n: { values: { en: code } }, dataType: 'String', ordinal, isRequired: false, isKey: false, lookupRegistryDefId: null, unitId: null, ...over,
    });
    const saved = await admin.put(`/api/v1/registries/${Registry}/definition`, {
      headers: { 'If-Match': `"${String(definition.definitionVersion)}"` },
      data: {
        fields: [
          field('NaeCode', 1, { isRequired: true, isKey: true, levelCode: 'SRC' }),
          field('RegionCode', 2, { levelCode: 'SRC' }),
          field('AreaCode', 3, { levelCode: 'SRC' }),
          field('AfPath', 4, {}),
          field('SourceType', 5, { levelCode: 'SRC' }),
          field('PointName', 6, { levelCode: 'SRC' }),
        ],
        rules: [],
        reason: 'e2e: naeNodeTypes - поля за рівнями',
      },
    });
    expect(saved.status(), `поля: ${await saved.text()}`).toBe(200);
  });

  test.afterAll(async () => {
    test.setTimeout(180_000);
    if (!created) return;
    const nodes = await readTree();
    const depth = (node: TreeNode): number => {
      let d = 0;
      for (let at = node.parentEntryId; at !== null; at = nodes.find((n) => n.id === at)?.parentEntryId ?? null) d += 1;

      return d;
    };
    for (const node of [...nodes].sort((a, b) => depth(b) - depth(a))) {
      const removed = await admin.delete(`/api/v1/registries/${Registry}/entries/${String(node.id)}`);
      expect([204, 404, 409, 422], `видалення ${String(node.id)}: ${String(removed.status())}`).toContain(removed.status());
    }
    // Тема адміністратора - назад на light (її міняє dark-прогін).
    await admin.put('/api/v1/me/preferences/theme', { headers: { 'Content-Type': 'application/json' }, data: JSON.stringify('light') });
    await admin.dispose();
  });

  test('NAE node types: create Region, On/Offshore, Area, Unit and Equipment with three-language names', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await page.goto(url());

    // Регіон: тип без полів номера NAE; назви трьома мовами; регіон і район - не поле вводу.
    await page.getByRole('button', { name: 'Add top-level node' }).click();
    await fillNames(page, `E2E Region ${Stamp}`, `E2ER${Stamp}`);
    await expect(dialogOf(page).getByLabel(/^NaeCode/)).toHaveCount(0);
    await save(page, `E2E Region ${Stamp}`);

    const add = async (level: string, name: string): Promise<void> => {
      await page.getByTestId('tree-panel-add').click();
      const item = page.getByRole('menuitem', { name: level, exact: true });
      if ((await item.count()) > 0) await item.click();
      await fillNames(page, name, `E2E${level.replace(/\W/g, '')}${Stamp}`);
    };

    await add('On/Offshore', `E2E Offshore ${Stamp}`);
    await save(page, `E2E Offshore ${Stamp}`);
    await add('Area', `E2E Barges ${Stamp}`);
    await save(page, `E2E Barges ${Stamp}`);
    // Район може стояти під районом (вкладення в себе) - діалог пропонує обидва рівні.
    await page.getByTestId('tree-panel-add').click();
    await expect(page.getByRole('menuitem', { name: 'Area', exact: true })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: 'Unit', exact: true })).toBeVisible();
    await page.keyboard.press('Escape');
    await add('Unit', `E2E Generator ${Stamp}`);
    await save(page, `E2E Generator ${Stamp}`);

    // Обладнання: номер NAE обов'язковий; регіон, район і шлях AF показані текстом.
    await page.getByTestId('tree-panel-add').click();
    const dialog = dialogOf(page);
    await expect(dialog).toBeVisible({ timeout: 15_000 });
    await expect(dialog.getByLabel(/^RegionCode/)).toHaveCount(0);
    await expect(dialog.locator('[data-nae-derived-field="RegionCode"]')).toBeVisible();
    await expect(dialog.locator('[data-nae-derived-field="AreaCode"]')).toBeVisible();
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(dialog.getByText('Enter the English name.')).toBeVisible();
    await expect(dialog.getByText('Fill in this field.')).toBeVisible();
    await expect(dialog).toBeVisible();
    await fillNames(page, `E2E Exhaust pipe ${Stamp}`, `E2EEquip${Stamp}`);
    await dialog.getByLabel(/^NaeCode/).fill(NaeInput);
    await save(page, `E2E Exhaust pipe ${Stamp}`);

    const nodes = await readTree();
    const byLevel = (code: string): TreeNode | undefined => nodes.find((n) => n.levelCode === code && n.display.includes(Stamp));
    for (const code of ['REGION', 'AREA', 'SITE', 'EQUIP', 'SRC']) expect(byLevel(code), `вузол ${code}`).toBeDefined();
    const src = byLevel('SRC')!;
    const detail = await (await admin.get(`/api/v1/registries/${Registry}/entries/${String(src.id)}`)).json() as { displayL10n: { values: Record<string, string> } };
    expect(Object.keys(detail.displayL10n.values).sort()).toEqual(['en', 'kz', 'ru']);
    // Сервер нормалізує номер: `36_pod` -> `0036_pod`; регіон і район виведені з предків.
    const values = await entryValues(src.id);
    expect(values['NaeCode']).toBe(NaeStored);
    expect(values['RegionCode']).toBeTruthy();
    expect(values['AreaCode']).toBeTruthy();

    // Панель вибраного вузла показує нормалізований номер і регіон/район текстом.
    const form = page.getByRole('form', { name: 'Node attributes' });
    await expect(form.getByLabel(/^NaeCode/)).toHaveValue(NaeStored, { timeout: 30_000 });
    await expect(form.locator('[data-nae-derived-field="RegionCode"]')).toBeVisible();
  });

  test('NAE node types: duplicate NAE number in the same region and area is refused on the field', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    const unit = (await readTree()).find((n) => n.levelCode === 'EQUIP' && n.display.includes(Stamp))!;
    await page.goto(url(unit.id));
    await page.getByTestId('tree-panel-add').click();
    await fillNames(page, `E2E Duplicate ${Stamp}`, `E2EDup${Stamp}`);
    const dialog = dialogOf(page);
    await dialog.getByLabel(/^NaeCode/).fill(NaeInput);
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();

    await expect(dialog.locator('[data-nae-code-taken]')).toContainText(NaeStored, { timeout: 15_000 });
    await expect(dialog.getByRole('button', { name: 'Open that node' })).toBeVisible();
    await expect(dialog).toBeVisible();
    expect((await readTree()).filter((n) => n.display.includes('E2E Duplicate'))).toHaveLength(0);
  });

  test('NAE node types: the field editor assigns a field to a node type', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await page.goto(`/admin/registries/${Registry}/definition`);
    const select = page.getByRole('combobox', { name: 'Level of field NaeCode' });
    await expect(select).toBeVisible({ timeout: 30_000 });
    await expect(select).toHaveValue('SRC');
    await expect(page.getByRole('combobox', { name: 'Level of field AfPath' })).toHaveValue('');
    await page.getByRole('button', { name: 'Add field' }).click();
    const newLevel = page.getByRole('combobox', { name: /^Node type \(level\)/ });
    await expect(newLevel).toBeVisible();
    await expect(newLevel.locator('option')).toHaveText(['All levels', 'Region', 'On/Offshore', 'Area', 'Unit', 'Equipment']);
  });

  for (const scheme of ['light', 'dark'] as const) {
    for (const width of [1440, 500]) {
      test(`NAE node types: axe ${scheme} ${String(width)}`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await signIn(page, scheme);
        if (scheme === 'dark') await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'dark');
        const unit = (await readTree()).find((n) => n.levelCode === 'EQUIP' && n.display.includes(Stamp))!;
        await page.goto(url(unit.id));
        await expect(page.getByTestId('tree-panel')).toContainText('Generator', { timeout: 30_000 });
        expect(await axeAll(page), `axe ${scheme} ${String(width)}: сторінка`).toEqual([]);

        await page.getByTestId('tree-panel-add').click();
        await expect(dialogOf(page)).toBeVisible({ timeout: 15_000 });
        await expect(dialogOf(page).getByLabel(/^NaeCode/)).toBeVisible();
        expect(await axeAll(page), `axe ${scheme} ${String(width)}: New node`).toEqual([]);
        await page.keyboard.press('Escape');

        const src = (await readTree()).find((n) => n.levelCode === 'SRC' && n.display.includes(Stamp))!;
        await page.goto(url(src.id));
        await expect(page.getByRole('form', { name: 'Node attributes' })).toBeVisible({ timeout: 30_000 });
        expect(await axeAll(page), `axe ${scheme} ${String(width)}: Node attributes`).toEqual([]);
      });
    }
  }
});
