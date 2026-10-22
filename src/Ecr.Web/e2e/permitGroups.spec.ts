import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Permits (групи типів): екран «Groups» і Select цілей Lookup з групою типів у конструкторі шаблону - у реальному
 * браузері, на справжньому стенді (вхід, шаблон і версія - справжні).
 *
 * ⚠ Відповіді `permit-groups`, `permit-types` і перелік цілей Lookup ПІДМІНЕНО (`page.route`): сервер груп у lane
 * `permit-groups-server` дописується (сховище - заглушка до PRMG01), тож створення групи на стенді поки неможливе.
 * Тут доводиться саме клієнт: що він шле (тіло, токен), як реагує на 4097/4099, як малює Select з описом. Реальний e2e
 * створення групи (POST -> GET -> Select у шаблоні -> піклер у документі) - наступний крок, коли серверна lane
 * отримає PRMG01 і сервіс.
 *
 * Прогін кожного сценарію - N/5 (гонки випадного списку й шторки). Знімки (необов'язково): `ECR_PGU_SHOTS_DIR` -
 * екран, шторка і випадний список у Light/Dark на 1440 і 500 пікселів. Запуск: `-Grep "Permit groups"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

interface GroupFixture {
  code: string;
  descriptionL10n: Record<string, string>;
  isActive: boolean;
  nameL10n: Record<string, string>;
  registryCode: string;
  registryDefId: number;
  rowVersion: string;
  types: { code: string; category: string; headerRegistryCode: string; nameL10n: Record<string, string> }[];
  updatedAt: string;
  usedInColumns: number;
  usedInTemplates: number;
}

const TypeWater = { code: 'WaterIntake', category: 'Water', headerRegistryCode: 'PRM_WaterIntake', nameL10n: { en: 'Water intake' } };
const TypeDischarge = { code: 'Discharge', category: 'Water', headerRegistryCode: 'PRM_Discharge', nameL10n: { en: 'Discharge' } };

function group(over: Partial<GroupFixture> & { code: string }): GroupFixture {
  return {
    descriptionL10n: {},
    isActive: true,
    nameL10n: { en: over.code },
    registryCode: `PRMG_${over.code}`,
    registryDefId: 9000,
    rowVersion: 'v1',
    types: [],
    updatedAt: '2026-10-20T10:00:00Z',
    usedInColumns: 0,
    usedInTemplates: 0,
    ...over,
  };
}

interface Sent {
  method: string;
  path: string;
  body: unknown;
}

interface Backend {
  groups: GroupFixture[];
  sent: Sent[];
  /** Наступний запис відхилити цією відповіддю. */
  failNext: { status: number; body: Record<string, unknown> } | null;
}

/** Підміна `permit-groups`, `permit-types` і цілей Lookup пам'яттю тесту. */
async function installBackend(page: Page, groups: GroupFixture[]): Promise<Backend> {
  const backend: Backend = { groups, sent: [], failNext: null };

  await page.route(/\/api\/v1\/permit-groups(\/.*)?$/, async (route: Route) => {
    const request = route.request();
    const method = request.method();
    const path = new URL(request.url()).pathname;
    const parts = path.replace('/api/v1/permit-groups', '').split('/').filter((part) => part !== '');
    const code = parts[0] === undefined ? undefined : decodeURIComponent(parts[0]);

    if (method === 'GET') {
      if (code === undefined) return route.fulfill({ json: backend.groups });
      const found = backend.groups.find((item) => item.code === code);

      return found === undefined ? route.fulfill({ status: 404, json: { title: 'x', status: 404, errorCode: 'ECR-PRM-0405', messageKey: 'err.ECR-PRM-0405.group', code } }) : route.fulfill({ json: found });
    }

    const body = request.postDataJSON() as unknown;
    backend.sent.push({ method, path, body });
    if (backend.failNext !== null) {
      const failure = backend.failNext;
      backend.failNext = null;

      return route.fulfill({ status: failure.status, json: failure.body });
    }
    if (method === 'POST') {
      const create = body as { code: string; nameL10n: Record<string, string>; descriptionL10n: Record<string, string> | null; typeCodes: string[] | null };
      const all = [TypeWater, TypeDischarge];
      const created = group({
        code: create.code,
        nameL10n: create.nameL10n,
        descriptionL10n: create.descriptionL10n ?? {},
        types: (create.typeCodes ?? []).map((typeCode) => all.find((type) => type.code === typeCode) ?? TypeWater),
      });
      backend.groups = [...backend.groups, created];

      return route.fulfill({ status: 201, json: created });
    }
    if (method === 'DELETE') {
      backend.groups = backend.groups.filter((item) => item.code !== code);

      return route.fulfill({ status: 204 });
    }
    const current = backend.groups.find((item) => item.code === code) as GroupFixture;
    const all = [TypeWater, TypeDischarge];
    const next: GroupFixture =
      parts[1] === 'types'
        ? { ...current, types: (body as { typeCodes: string[] }).typeCodes.map((typeCode) => all.find((type) => type.code === typeCode) ?? TypeWater), rowVersion: `${current.rowVersion}+t` }
        : { ...current, ...(body as Partial<GroupFixture>), rowVersion: `${current.rowVersion}+m` };
    backend.groups = backend.groups.map((item) => (item.code === code ? next : item));

    return route.fulfill({ json: next });
  });

  await page.route(/\/api\/v1\/permit-types(\?.*)?$/, async (route: Route) => {
    const item = (type: typeof TypeWater) => ({
      ...type, status: 'Published', hasUnpublishedChanges: false, permitCount: 3, tables: [], updatedAt: null, updatedByDisplayName: null, version: 1,
    });

    return route.fulfill({ json: { items: [item(TypeWater), item(TypeDischarge)], nextCursor: null, totalCount: 2 } });
  });

  return backend;
}

async function shoot(page: Page, name: string): Promise<void> {
  const dir = process.env['ECR_PGU_SHOTS_DIR'];
  if (dir === undefined) return;
  mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: join(dir, `${name}.png`) });
}

const Water = group({
  code: 'WATER',
  nameL10n: { en: 'Water permits' },
  descriptionL10n: { en: 'All water permits of the site' },
  types: [TypeWater, TypeDischarge],
  rowVersion: 'w1',
});
const Used = group({ code: 'AIRUSED', nameL10n: { en: 'Air used' }, usedInColumns: 2, usedInTemplates: 1, isActive: false, rowVersion: 'a1' });

test.describe('Permit groups', () => {
  test('екран Groups: перелік, створення (тіло POST), Esc повертає фокус - N/5', async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page);

    for (let run = 1; run <= 5; run += 1) {
      const backend = await installBackend(page, [Water, Used]);
      await page.goto('/admin/permits/groups');
      await expect(page.getByRole('heading', { name: 'Groups' }).first(), `прогін ${run}: заголовок`).toBeVisible({ timeout: 30_000 });
      await expect(page.getByText('Water permits')).toBeVisible();
      await expect(page.getByText('Water intake · Discharge')).toBeVisible();
      if (run === 1) await shoot(page, 'groups-list');

      const opener = page.getByRole('button', { name: 'New group' }).first();
      await opener.focus();
      await opener.click();
      const drawer = page.getByRole('dialog');
      await expect(drawer).toBeVisible();
      await drawer.getByLabel(/^Code/).fill(`E2E_G${String(run)}`);
      await drawer.getByRole('textbox', { name: /^Name · English/ }).fill(`E2E group ${String(run)}`);
      await drawer.getByRole('textbox', { name: /^Description · English/ }).fill('Group for the e2e run');
      await drawer.getByRole('textbox', { name: 'Permit types in the group' }).click();
      await page.getByRole('option', { name: 'Water intake' }).click();
      if (run === 1) await shoot(page, 'groups-drawer-new');
      await drawer.getByRole('button', { name: 'Create group' }).click();

      await expect(drawer).toBeHidden({ timeout: 15_000 });
      expect(backend.sent, `прогін ${run}`).toHaveLength(1);
      expect(backend.sent[0]).toMatchObject({
        method: 'POST',
        body: { code: `E2E_G${String(run)}`, nameL10n: { en: `E2E group ${String(run)}` }, descriptionL10n: { en: 'Group for the e2e run' }, typeCodes: ['WaterIntake'] },
      });
      await expect(page.getByText(`E2E group ${String(run)}`)).toBeVisible();

      // Esc у шторці закриває лише її, фокус - на рядок, що її відкрив.
      const row = page.getByRole('row', { name: /Water permits/ });
      await row.focus();
      await row.press('Enter');
      await expect(page.getByRole('dialog')).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(page.getByRole('dialog')).toBeHidden();
      await expect(row, `прогін ${run}: фокус не повернувся на рядок`).toBeFocused();
    }
  });

  test('4097 біля поля коду; 4099 перечитує групу і повторна правка йде зі свіжим токеном - N/5', async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page);

    for (let run = 1; run <= 5; run += 1) {
      const backend = await installBackend(page, [{ ...Water }]);
      await page.goto('/admin/permits/groups?panel=_new');
      const drawer = page.getByRole('dialog');
      await expect(drawer).toBeVisible({ timeout: 30_000 });
      backend.failNext = { status: 409, body: { title: 'x', status: 409, errorCode: 'ECR-PRM-4097', messageKey: 'err.ECR-PRM-4097.code', code: 'WATER' } };
      await drawer.getByLabel(/^Code/).fill('WATER');
      await drawer.getByRole('textbox', { name: /^Name · English/ }).fill('Water again');
      await drawer.getByRole('button', { name: 'Create group' }).click();
      await expect(drawer.getByText('Permit group "WATER" already exists.'), `прогін ${run}: 4097 біля поля`).toBeVisible();
      await expect(drawer).toBeVisible();

      await page.goto('/admin/permits/groups?panel=WATER');
      await expect(page.getByRole('dialog')).toBeVisible({ timeout: 30_000 });
      backend.sent.length = 0;
      backend.failNext = { status: 409, body: { title: 'x', status: 409, errorCode: 'ECR-PRM-4099', messageKey: 'err.ECR-PRM-4099.changed', code: 'WATER' } };
      backend.groups = [{ ...Water, nameL10n: { en: 'Renamed elsewhere' }, rowVersion: 'w9' }];
      await page.getByRole('textbox', { name: /^Name · English/ }).fill('My edit');
      await page.getByRole('button', { name: 'Save' }).click();
      await expect(page.getByText('The group was changed by someone else.'), `прогін ${run}: банер конфлікту`).toBeVisible();
      await expect(page.getByRole('textbox', { name: /^Name · English/ })).toHaveValue('Renamed elsewhere');

      await page.getByRole('textbox', { name: /^Name · English/ }).fill('My edit');
      await page.getByRole('button', { name: 'Save' }).click();
      await expect(page.getByRole('dialog')).toBeHidden({ timeout: 15_000 });
      expect(backend.sent[1]?.body, `прогін ${run}`).toMatchObject({ rowVersion: 'w9', nameL10n: { en: 'My edit' } });
    }
  });

  test('конструктор шаблону: Select цілі Lookup - група типів з описом, пошук по опису, unlisted - N/5', async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page);
    const stamp = String(Date.now());
    const created = await page.request.post('/api/v1/templates', { data: { code: `E2EPgu${stamp}`, nameL10n: { en: `E2E groups ${stamp}` } } });
    expect(created.status(), await created.text()).toBeLessThan(300);
    const template = (await created.json()) as { templateId: number };
    const versionResponse = await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    });
    expect(versionResponse.status(), await versionResponse.text()).toBeLessThan(300);
    const version = (await versionResponse.json()) as { versionId: number };

    for (let run = 1; run <= 5; run += 1) {
      await page.route('**/api/v1/registries?includePermitHeaders=true', async (route) => {
        const real = (await (await route.fetch()).json()) as { id: number; code: string }[];
        const base = real[0] ?? { id: 1, code: 'X' };
        const entry = (over: Record<string, unknown>) => ({ ...base, fields: [], isHierarchical: false, isTemporal: true, sourceKind: 'Master', ...over });

        return route.fulfill({
          json: [
            entry({ id: 9001, code: 'PRMG_WATER', nameL10n: { values: { en: 'Water permits' } }, descriptionL10n: { values: { en: 'All water permits of the site' } }, kind: 'PermitGroup' }),
            entry({ id: 9002, code: 'PRM_Air', nameL10n: { values: { en: 'Air permit' } }, kind: 'PermitHeader' }),
            ...real.filter((registry) => !registry.code.startsWith('PRM')),
          ],
        });
      });
      await page.goto(`/admin/templates/${String(template.templateId)}/versions/${String(version.versionId)}`);
      await page.getByRole('button', { name: 'Add header field' }).first().click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible({ timeout: 30_000 });
      await dialog.getByLabel('Code', { exact: true }).fill('PermitNo');
      await dialog.getByLabel(/^Label/).first().fill('Permit number');
      await dialog.getByRole('textbox', { name: 'Data type' }).click();
      await page.getByRole('option', { name: 'Lookup' }).click();

      const registry = dialog.getByRole('textbox', { name: 'Registry' });
      await registry.click();
      const groupOption = page.getByRole('option', { name: /Water permits \(PRMG_WATER\)/ });
      await expect(groupOption, `прогін ${run}: група в списку`).toBeVisible({ timeout: 30_000 });
      await expect(groupOption.locator('[data-lookup-target-description]')).toHaveText('All water permits of the site');
      const labels = await page.evaluate(() => Array.from(document.querySelectorAll('[class*="groupLabel"]')).map((node) => node.textContent));
      expect(labels[0], `прогін ${run}: порядок груп ${JSON.stringify(labels)}`).toBe('Permits');
      if (run === 1) await shoot(page, 'select-open');

      // Пошук по опису; група без збігів зникає.
      await registry.fill('OF THE SITE');
      await expect(groupOption).toBeVisible();
      await expect(page.getByRole('option', { name: 'Air permit' })).toHaveCount(0);
      await groupOption.click();
      await expect(registry).toHaveValue(/Water permits \(PRMG_WATER\)/);
      await page.keyboard.press('Escape');
      await expect(dialog).toBeHidden();
    }
  });

  for (const scheme of ['light', 'dark'] as const) {
    for (const width of [1440, 500]) {
      test(`знімки екрана Groups ${scheme} ${String(width)}`, async ({ page }) => {
        test.skip(process.env['ECR_PGU_SHOTS_DIR'] === undefined, 'знімки лише за ECR_PGU_SHOTS_DIR');
        test.setTimeout(180_000);
        await page.emulateMedia({ colorScheme: scheme });
        await page.setViewportSize({ width, height: width === 500 ? 900 : 800 });
        await signIn(page);
        const html = page.locator('html');
        if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
          await page.getByRole('button', { name: new RegExp(Admin.user, 'i') }).first().click();
          await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
          await page.keyboard.press('Escape');
        }
        await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
        await installBackend(page, [Water, Used]);

        await page.goto('/admin/permits/groups');
        await expect(page.getByText('Water permits')).toBeVisible({ timeout: 30_000 });
        await shoot(page, `groups-list-${scheme}-${String(width)}`);

        await page.goto('/admin/permits/groups?panel=WATER');
        await expect(page.getByRole('dialog')).toBeVisible();
        await expect(page.getByRole('textbox', { name: /^Name · English/ })).toBeVisible();
        await shoot(page, `groups-drawer-edit-${scheme}-${String(width)}`);

        await page.goto('/admin/permits/groups?panel=AIRUSED');
        await expect(page.getByText(/Used in 1 templates/)).toBeVisible();
        await shoot(page, `groups-drawer-used-${scheme}-${String(width)}`);
      });
    }
  }
});
