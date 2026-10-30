import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Locator, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';

/**
 * Структура NAE (`/admin/permits/nae`, NAE plan N2) на живому стенді: дерево з справжніх обробників N1
 * (`hierarchy`, `tree`, `move`, `retire`, `entries`), пошук із підсвічуванням, «Move to…» і Alt+M, Alt+Up/Down,
 * виведення з каскадом, додавання дочірнього, дубль ділового ключа, користувач без права редагування, а також те, чого
 * не бачить jsdom: контраст (справжній axe), перенос на 500 px, кільце фокуса, висота рядка й прокрутка, знімки.
 *
 * ⚠ Довідник - власний `E2E_NAE_<час>` (сторінка приймає `?registry=<код>`), а не спільний `NAE_TREE`: той на стенді
 * створює лише завантаження з AF (його обробник - скелет), і порожній залишок із цього файлу заважав би справжньому
 * завантаженню. Видалення довідника API не має, тож залишок - порожній `E2E_NAE_*` (як у `registryEntryDrawer.spec.ts`);
 * вузли `afterAll` видаляє, користувача-глядача блокує, роль знімає.
 *
 * ⚠ Завантаження з AF (`POST /nae-tree/af-import`) і «де використовується» (`GET …/references`) тут не ганяються:
 * обробники на лінії контракту - скелети (`NotImplementedException`). Їх покриває vitest на підміненому API.
 *
 * Запуск (стенд - `tools/e2e-stand.ps1`): `npx playwright test -g "Структура NAE"`.
 * Знімки пишуться в `ECR_E2E_SHOTS_DIR`, якщо він заданий (інакше - лише вкладення звіту).
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const ShotsDir = process.env['ECR_E2E_SHOTS_DIR'] ?? '';

interface TreeNodeDto {
  id: number;
  display: string;
  levelCode: string | null;
  parentEntryId: number | null;
  ordinal: number;
  isActive: boolean;
  validTo: string | null;
}

interface Seed {
  registry: string;
  registryDefId: number;
  auto: boolean;
  region: number;
  siteA: number;
  siteB: number;
  srcA1: number;
  srcA2: number;
  viewer: { id: number; userName: string; password: string; roleId: number } | null;
}

const Names = { region: 'Atyrau E2E', siteA: 'Barge Alpha', siteB: 'Barge Beta', srcA1: 'Pipe One', srcA2: 'Pipe Two' } as const;
let seed: Seed | null = null;

async function signedIn(browser: Browser, userName = Admin.userName, password = Admin.password): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', { data: { userName, password } });
  expect(login.ok(), `вхід ${userName}: ${String(login.status())} ${await login.text()}`).toBe(true);
  return context;
}

async function signIn(page: Page, scheme: 'light' | 'dark', who = Admin): Promise<void> {
  await page.goto('/login');
  await page.evaluate((colour: string) => localStorage.setItem('mantine-color-scheme-value', colour), scheme);
  await page.reload();
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(who.userName);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(who.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });

  // ⚠ Тема з сервера перемагає localStorage (`usePreferenceSync`): без запису налаштування `theme` «dark» мовчки
  // лишався світлим (перші знімки dark були світлі). Пишемо налаштування, перезавантажуємо й ПЕРЕВІРЯЄМО атрибут.
  if (scheme === 'dark') {
    const put = await page.request.put('/api/v1/me/preferences/theme', {
      headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify(scheme),
    });
    expect(put.ok(), `тема: ${String(put.status())} ${await put.text()}`).toBe(true);
    await page.reload();
  }
  await expect
    .poll(() => page.evaluate(() => document.documentElement.getAttribute('data-mantine-color-scheme')), { timeout: 15_000 })
    .toBe(scheme);
}

async function readTree(request: APIRequestContext, registry: string): Promise<TreeNodeDto[]> {
  const response = await request.get(`/api/v1/registries/${registry}/tree?includeRetired=true`);
  expect(response.ok(), `дерево: ${String(response.status())} ${await response.text()}`).toBe(true);

  return ((await response.json()) as { nodes: TreeNodeDto[] }).nodes;
}

const url = (registry: string, node?: number): string =>
  `/admin/permits/nae?registry=${registry}${node === undefined ? '' : `&node=${String(node)}`}`;

const item = (page: Page, name: string): Locator => page.getByRole('treeitem', { name: new RegExp(name) });

/** Розгортає всі згорнуті гілки (клік по трикутнику замінено клавішею Right на рядку). */
async function expandAll(page: Page): Promise<void> {
  for (let pass = 0; pass < 6; pass += 1) {
    const collapsed = page.locator('[role="treeitem"][aria-expanded="false"]');
    if ((await collapsed.count()) === 0) return;
    await collapsed.first().click();
    await page.keyboard.press('ArrowRight');
  }
}

async function shot(page: Page, testInfo: { attach: (n: string, o: { body: Buffer; contentType: string }) => Promise<void> }, name: string): Promise<void> {
  const body = await page.screenshot({ fullPage: false });
  await testInfo.attach(name, { body, contentType: 'image/png' });
  if (ShotsDir !== '') {
    mkdirSync(ShotsDir, { recursive: true });
    await page.screenshot({ path: path.join(ShotsDir, `${name}.png`) });
  }
}

test.describe('Структура NAE: дерево, дії, права, якість у браузері', () => {
  test.describe.configure({ timeout: 240_000, mode: 'serial' });

  test.beforeAll(async ({ browser }) => {
    if (PeriodKey === '') throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD (tools/e2e-stand.ps1)');

    const admin = await signedIn(browser);
    try {
      const stamp = String(Date.now());
      const registry = `E2E_NAE_${stamp}`;
      const created = await admin.request.post('/api/v1/registries', {
        data: { code: registry, nameL10n: { en: `E2E NAE structure ${stamp}` }, isTemporal: false },
      });
      expect(created.status(), `довідник: ${await created.text()}`).toBe(201);

      const definition = (await (await admin.request.get(`/api/v1/registries/${registry}/definition`)).json()) as {
        id: number;
        definitionVersion: number;
        codeMode?: string | null;
      };
      const field = (code: string, ordinal: number, over: Record<string, unknown>) => ({
        id: null,
        code,
        nameL10n: { values: { en: code } },
        dataType: 'String',
        ordinal,
        isRequired: false,
        isKey: false,
        lookupRegistryDefId: null,
        unitId: null,
        ...over,
      });
      const saved = await admin.request.put(`/api/v1/registries/${registry}/definition`, {
        headers: { 'If-Match': `"${String(definition.definitionVersion)}"` },
        data: {
          fields: [
            field('NaeCode', 1, { isRequired: true, isKey: true }),
            field('RegionCode', 2, { isRequired: true }),
            field('AreaCode', 3, { isRequired: true }),
            field('PointName', 4, {}),
          ],
          rules: [],
          reason: 'e2e: naeStructure - поля структури',
        },
      });
      expect(saved.status(), `поля: ${await saved.text()}`).toBe(200);

      // Усі три нові рівні одним PUT (на 4af0a0112 це давало 500; виправлено в integ-nae-a2) - заодно сторож цього.
      const wanted = [
        { code: 'REGION', isOptional: false, name: { values: { en: 'Region' } } },
        { code: 'SITE', isOptional: false, name: { values: { en: 'Site' } } },
        { code: 'SRC', isOptional: false, name: { values: { en: 'NAE' } } },
      ];
      const levels = await admin.request.put(`/api/v1/registries/${registry}/hierarchy`, {
        data: { isHierarchical: true, levels: wanted },
      });
      expect(levels.status(), `рівні: ${await levels.text()}`).toBe(200);

      const auto = definition.codeMode === 'Auto';
      let counter = 0;
      const make = async (name: string, level: string, parent: number | null, naeCode: string): Promise<number> => {
        counter += 1;
        const response = await admin.request.post(`/api/v1/registries/${registry}/entries`, {
          data: {
            id: null,
            registryDefId: definition.id,
            code: auto ? '' : `N${String(counter)}`,
            display: { values: { en: name } },
            parentEntryId: parent,
            levelCode: level,
            values: { NaeCode: naeCode, RegionCode: 'ATYRAU', AreaCode: '-' },
          },
        });
        expect(response.status(), `вузол ${name}: ${await response.text()}`).toBe(201);

        return ((await response.json()) as { id: number }).id;
      };
      const region = await make(Names.region, 'REGION', null, 'REG1');
      const siteA = await make(Names.siteA, 'SITE', region, 'SIT1');
      const siteB = await make(Names.siteB, 'SITE', region, 'SIT2');
      const srcA1 = await make(Names.srcA1, 'SRC', siteA, '1085');
      const srcA2 = await make(Names.srcA2, 'SRC', siteA, '1086');

      // Глядач: роль лише з Registry.View + користувач (разовий пароль міняється тут, а не в тесті).
      const viewerName = `e2e-nae-view-${stamp}`;
      const issued = 'E2E-V1ewer-2026!';
      const work = 'E2E-V1ewer-Work-2026!';
      const roleCode = `E2ENaeView${stamp}`;
      const role = await admin.request.post('/api/v1/roles', {
        data: { code: roleCode, nameL10n: { en: 'E2E NAE viewer' }, permissionCodes: ['Registry.View'] },
      });
      expect(role.ok(), `роль глядача: ${await role.text()}`).toBe(true);
      const user = await admin.request.post('/api/v1/users', {
        data: { userName: viewerName, provider: 'Local', sid: null, displayName: 'E2E NAE viewer', initialPassword: issued, roleCodes: [roleCode] },
      });
      expect(user.ok(), `глядач: ${await user.text()}`).toBe(true);
      const once = await browser.newContext();
      try {
        expect((await once.request.post('/api/v1/login/local', { data: { userName: viewerName, password: issued } })).ok()).toBe(true);
        const changed = await once.request.post('/api/v1/auth/change-password', { data: { currentPassword: issued, newPassword: work } });
        expect(changed.ok(), `пароль глядача: ${await changed.text()}`).toBe(true);
      } finally {
        await once.close();
      }
      const roles = (await (await admin.request.get('/api/v1/roles')).json()) as { id: number; code: string }[];
      const userBody = (await user.json()) as { id: number };

      seed = {
        registry,
        registryDefId: definition.id,
        auto,
        region,
        siteA,
        siteB,
        srcA1,
        srcA2,
        viewer: { id: userBody.id, userName: viewerName, password: work, roleId: roles.find((r) => r.code === roleCode)?.id ?? 0 },
      };
    } finally {
      await admin.close();
    }
  });

  test.afterAll(async ({ browser }) => {
    if (seed === null) return;
    const admin = await signedIn(browser);
    try {
      // Тема e2e-admin повертається до світлої (її міняє dark-прогін), інакше далі стенд іде з темною.
      await admin.request.put('/api/v1/me/preferences/theme', { headers: { 'Content-Type': 'application/json' }, data: JSON.stringify('light') });
      // Вузли (включно з тими, що створили тести) - м'яке видалення листків, потім предків; збій не маскує тести.
      const nodes = await readTree(admin.request, seed.registry);
      const depth = (node: TreeNodeDto): number => {
        let level = 0;
        let parent = node.parentEntryId;
        while (parent !== null) {
          level += 1;
          parent = nodes.find((n) => n.id === parent)?.parentEntryId ?? null;
        }

        return level;
      };
      for (const node of [...nodes].sort((a, b) => depth(b) - depth(a))) {
        const removed = await admin.request.delete(`/api/v1/registries/${seed.registry}/entries/${String(node.id)}`);
        expect([204, 404, 409, 422], `видалення ${String(node.id)}: ${String(removed.status())}`).toContain(removed.status());
      }
      if (seed.viewer !== null) {
        await admin.request.post(`/api/v1/users/${String(seed.viewer.id)}/lock`, { data: {} });
        if (seed.viewer.roleId !== 0) await admin.request.delete(`/api/v1/roles/${String(seed.viewer.roleId)}`);
      }
    } finally {
      await admin.close();
    }
  });

  test('дерево читається справжніми обробниками: регіон > майданчики > джерела, підпис «NaeCode · назва»', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await page.goto(url(data.registry));

    await expect(page.getByRole('tree')).toBeVisible({ timeout: 30_000 });
    await expect(item(page, Names.region)).toBeVisible();
    await expandAll(page);
    for (const name of [Names.siteA, Names.siteB, Names.srcA1, Names.srcA2]) await expect(item(page, name)).toBeVisible();
    // Підпис джерела: «1085 · Pipe One» (контракт 4af0a0112); якщо сервер ще не віддає naeCode - лишається назва.
    const label = (await item(page, Names.srcA1).innerText()).trim();
    expect(label).toMatch(/Pipe One/);
    const nodes = await readTree(page.request, data.registry);
    expect(nodes.filter((n) => n.isActive)).toHaveLength(5);
    expect(nodes.find((n) => n.id === data.srcA1)?.parentEntryId).toBe(data.siteA);
  });

  test('?node= вибирає вузол: панель з атрибутами (NaeCode) і вузлом у шляху', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await page.goto(url(data.registry, data.srcA1));

    const form = page.getByRole('form', { name: 'Node attributes' });
    await expect(form).toBeVisible({ timeout: 30_000 });
    await expect(form.getByLabel(/^NaeCode/)).toHaveValue('1085');
    await expect(form.getByLabel(/^RegionCode/)).toHaveValue('ATYRAU');
    await expect(page.getByTestId('tree-panel')).toContainText(Names.siteA);
  });

  test('пошук підсвічує збіги й розгортає предків; Esc очищає', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await page.goto(url(data.registry));
    await expect(item(page, Names.region)).toBeVisible({ timeout: 30_000 });

    const search = page.getByTestId('tree-search');
    await search.fill('Pipe Two');
    await expect(item(page, Names.srcA2)).toBeVisible({ timeout: 15_000 });
    await expect(item(page, Names.siteA), 'предок розгорнутий').toBeVisible();
    await expect(item(page, Names.siteB), 'не збіг і не предок - сховано').toHaveCount(0);
    await expect(page.locator('[role="treeitem"] mark').first()).toBeVisible();

    await search.press('Escape');
    await expect(search).toHaveValue('');
    await expect(item(page, Names.region)).toBeVisible();
  });

  test('Alt+Down / Alt+Up міняє порядок серед сусідів (перевірка на сервері)', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await page.goto(url(data.registry, data.srcA1));
    await expect(page.getByRole('form', { name: 'Node attributes' })).toBeVisible({ timeout: 30_000 });
    await expandAll(page);

    // Порядок сусідів - за (ordinal, id): вузли, створені POST-ом, можуть мати однаковий ordinal.
    const order = async (): Promise<number[]> =>
      (await readTree(page.request, data.registry))
        .filter((n) => n.parentEntryId === data.siteA && n.isActive)
        .sort((a, b) => a.ordinal - b.ordinal || a.id - b.id)
        .map((n) => n.id);
    expect(await order()).toEqual([data.srcA1, data.srcA2]);

    await item(page, Names.srcA1).focus();
    await page.keyboard.press('Alt+ArrowDown');
    await expect.poll(order, { timeout: 15_000 }).toEqual([data.srcA2, data.srcA1]);

    await item(page, Names.srcA1).focus();
    await page.keyboard.press('Alt+ArrowUp');
    await expect.poll(order, { timeout: 15_000 }).toEqual([data.srcA1, data.srcA2]);
  });

  test('Alt+M відкриває «Move to…», лише допустимі місця; перенос на іншого батька', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await page.goto(url(data.registry, data.srcA2));
    await expect(page.getByRole('form', { name: 'Node attributes' })).toBeVisible({ timeout: 30_000 });
    await expandAll(page);

    await item(page, Names.srcA2).focus();
    await page.keyboard.press('Alt+m');
    const dialog = page.getByRole('dialog', { name: /Move/ });
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    // Джерело (SRC) лишається джерелом: у списку лише майданчики, не регіон і не інші джерела.
    await expect(dialog.getByRole('radio', { name: new RegExp(Names.siteB) })).toBeVisible();
    await expect(dialog.getByRole('radio', { name: new RegExp(`${Names.srcA1}`) })).toHaveCount(0);

    await dialog.getByRole('radio', { name: new RegExp(Names.siteB) }).check();
    await dialog.getByRole('button', { name: 'Move', exact: true }).click();
    await expect(dialog).toBeHidden({ timeout: 15_000 });

    const now = await readTree(page.request, data.registry);
    expect(now.find((n) => n.id === data.srcA2)!.parentEntryId).toBe(data.siteB);
  });

  test('додати дочірнє на допустимому рівні; дубль ділового ключа відхиляється у формі', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await page.goto(url(data.registry, data.siteB));
    await expect(page.getByTestId('tree-panel')).toContainText(Names.siteB, { timeout: 30_000 });

    const fill = async (name: string, nae: string): Promise<Locator> => {
      await page.getByRole('button', { name: 'Add child' }).first().click();
      const dialog = page.getByRole('dialog', { name: 'New node' });
      await expect(dialog).toBeVisible({ timeout: 15_000 });
      // Автокод: поля коду немає; ручний код (довідник створено з CodeMode = Manual): поле є й вимагається.
      const codeField = dialog.getByRole('textbox', { name: 'Code', exact: true });
      if (data.auto) await expect(codeField).toHaveCount(0);
      else await codeField.fill(`E2E_${nae}_${name.replace(/\s/g, '')}`);
      await dialog.getByLabel(/^Name · English/).fill(name);
      await dialog.getByLabel(/^NaeCode/).fill(nae);
      // Регіон успадковано від батька (підказка), район порожній -> «-».
      await expect(dialog.getByLabel(/^RegionCode/)).toHaveValue('ATYRAU');

      return dialog;
    };

    const dialog = await fill('Pipe Three', '1090');
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(dialog).toBeHidden({ timeout: 15_000 });
    const created = (await readTree(page.request, data.registry)).find((n) => n.display === 'Pipe Three');
    expect(created, 'новий вузол є на сервері').toBeDefined();
    expect(created!.levelCode).toBe('SRC');
    expect(created!.parentEntryId).toBe(data.siteB);

    // Відмова ділового ключа (409 ECR-REG-4092) - ПІДМІНЕНА відповідь сервера (page.route), а не справжня: довідник цього
    // спеку має простий ключ по NaeCode без складеного ключа NAE_TREE, і сервер приймає дубль (201) - див. звіт. Доводиться
    // саме клієнтське оброблення тіла відмови: форма лишається відкритою й називає тримача ключа.
    await page.route('**/api/v1/registries/*/entries', async (route) => {
      if (route.request().method() !== 'POST') return route.fallback();

      return route.fulfill({
        status: 409,
        contentType: 'application/json',
        body: JSON.stringify({
          type: 'https://ecr.ncoc.kz/errors/ECR-REG-4092',
          title: 'Key window overlap',
          status: 409,
          errorCode: 'ECR-REG-4092',
          messageKey: 'err.ECR-REG-4092.keyWindowOverlap',
          entryCode: 'HOLDER_1',
        }),
      });
    });
    // Після створення вибрано НОВИЙ вузол (лист, «Add child» нема): повертаємось на майданчик.
    await page.goto(url(data.registry, data.siteB));
    await expect(page.getByTestId('tree-panel')).toContainText(Names.siteB, { timeout: 30_000 });
    await expect(page.getByRole('button', { name: 'Add child' }).first()).toBeEnabled();
    const again = await fill('Pipe Dup', '1090');
    await again.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(again.locator('[data-nae-key-error]')).toContainText('HOLDER_1', { timeout: 15_000 });
    await expect(again).toBeVisible();
    expect((await readTree(page.request, data.registry)).filter((n) => n.display === 'Pipe Dup')).toHaveLength(0);
  });

  test('виведення з каскадом: підтвердження називає дітей, фокус на «Cancel»; вузол і діти виведені', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await page.goto(url(data.registry, data.siteB));
    await expect(page.getByTestId('tree-panel')).toContainText(Names.siteB, { timeout: 30_000 });
    await expandAll(page);

    await item(page, Names.siteB).focus();
    await page.keyboard.press('Delete');
    const dialog = page.getByRole('dialog', { name: /Retire/ });
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expect(dialog).toContainText(/direct children/);
    await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();

    await dialog.getByRole('button', { name: 'Retire', exact: true }).click();
    await expect(dialog).toBeHidden({ timeout: 15_000 });
    const now = await readTree(page.request, data.registry);
    // М'яке виведення: вікно дії закривається (validTo - перший недійсний день), вузол не видаляється.
    const today = new Date().toISOString().slice(0, 10);
    const retired = (node: TreeNodeDto): boolean => !node.isActive || (node.validTo !== null && node.validTo <= today);
    expect(retired(now.find((n) => n.id === data.siteB)!), 'майданчик виведено').toBe(true);
    const children = now.filter((n) => n.parentEntryId === data.siteB);
    expect(children.length, 'діти лишилися у дереві').toBeGreaterThan(0);
    for (const child of children) expect(retired(child), child.display).toBe(true);
  });

  test('користувач лише з Registry.View: дерево й панель є, дій зміни немає', async ({ page }) => {
    const data = seed!;
    const viewer = data.viewer!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light', { userName: viewer.userName, password: viewer.password });
    await page.goto(url(data.registry, data.srcA1));

    await expect(page.getByTestId('tree-panel')).toContainText(Names.siteA, { timeout: 30_000 });
    await expect(page.locator('[data-nae-readonly]')).toBeVisible();
    await expect(page.getByTestId('tree-panel-move')).toHaveCount(0);
    // ⚠ exact: інакше «Retire» збігається з перемикачем «Retired» (показ виведених), який глядачу доступний.
    await expect(page.getByRole('button', { name: 'Retire', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add child', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Load from PI AF' })).toHaveCount(0);
    // Клавіші зміни теж не діють: Delete не відкриває підтвердження.
    await item(page, Names.srcA1).focus();
    await page.keyboard.press('Delete');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  for (const scheme of ['light', 'dark'] as const) {
    test(`${scheme}: справжній axe (контраст), 500 px без горизонтальної прокрутки, кільце фокуса, висота рядка, знімки`, async ({ page }, testInfo) => {
      const data = seed!;
      await page.setViewportSize({ width: 1440, height: 900 });
      await signIn(page, scheme);
      await page.goto(url(data.registry, data.srcA1));
      await expect(page.getByRole('form', { name: 'Node attributes' })).toBeVisible({ timeout: 30_000 });
      await expandAll(page);
      await expect(item(page, Names.srcA1)).toBeVisible();

      // 1. Контраст: найважливіше правило, якого jsdom не рахує (приглушені токени: код, шлях, «Valid», підказки).
      expect(await axeViolations(page, 'color-contrast'), `color-contrast @1440 ${scheme}`).toEqual([]);

      // 2. Висота рядка дерева = токен `--ecr-row-height` (а не залежить від вмісту), і дерево прокручується окремо.
      const rows = await page.evaluate(() => {
        const token = getComputedStyle(document.documentElement).getPropertyValue('--ecr-row-height').trim();
        const heights = Array.from(document.querySelectorAll<HTMLElement>('[role="treeitem"]')).map((el) => el.getBoundingClientRect().height);
        return { token, heights };
      });
      expect(rows.heights.length).toBeGreaterThanOrEqual(3);
      expect(new Set(rows.heights.map((h) => Math.round(h))).size, `рядки однієї висоти: ${rows.heights.join(',')}`).toBe(1);
      const viewport = page.getByTestId('tree-viewport');
      const scroll = await viewport.evaluate((el) => ({ client: el.clientHeight, overflowY: getComputedStyle(el).overflowY }));
      expect(['auto', 'scroll']).toContain(scroll.overflowY);
      expect(scroll.client).toBeGreaterThan(80);

      // 3. Кільце фокуса на рядку дерева видно (різниця пікселів із не сфокусованим станом).
      // Рядок майданчика: не вибраний і не виведений (srcA2 до цього перенесено під виведений майданчик).
      const target = item(page, Names.siteA);
      await page.locator('body').click({ position: { x: 5, y: 5 } });
      const plain = await target.screenshot();
      await target.focus();
      await page.keyboard.press('ArrowUp');
      await page.keyboard.press('ArrowDown');
      const focused = await item(page, Names.siteA).screenshot();
      expect(Buffer.compare(plain, focused), 'кільце фокуса змінює пікселі рядка').not.toBe(0);
      const outline = await item(page, Names.siteA).evaluate((el) => {
        const style = getComputedStyle(el);
        return { width: parseFloat(style.outlineWidth), style: style.outlineStyle, shadow: style.boxShadow };
      });
      expect(outline.style !== 'none' || outline.shadow !== 'none', `фокус: ${JSON.stringify(outline)}`).toBe(true);

      await shot(page, testInfo, `nae-structure-${scheme}-1440`);

      // 4. 500 px: усе переноситься, сторінка не гортається вбік, права кнопка форми досяжна.
      await page.setViewportSize({ width: 500, height: 900 });
      await page.waitForTimeout(300);
      const overflow = await page.evaluate(() => ({
        doc: document.documentElement.scrollWidth - document.documentElement.clientWidth,
        body: document.body.scrollWidth - document.body.clientWidth,
      }));
      expect(overflow.doc, `горизонтальна прокрутка сторінки @500 ${scheme}`).toBeLessThanOrEqual(1);
      expect(overflow.body).toBeLessThanOrEqual(1);
      expect(await axeViolations(page, 'color-contrast'), `color-contrast @500 ${scheme}`).toEqual([]);
      await shot(page, testInfo, `nae-structure-${scheme}-500`);
      await page.setViewportSize({ width: 1440, height: 900 });
    });
  }
});
