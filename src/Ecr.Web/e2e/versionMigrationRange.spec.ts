import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';
import { openDocumentMore } from './documentMore';

/**
 * Version migration range (RPT-STABLE Part B, lane integ-version-migration-ui): діалог «Перенести на нову версію шаблону»
 * з обсягом «Whole project» / «From month» на РЕАЛЬНОМУ стенді (сервер із `Documents:VersionMigration:FromMonthEnabled=true`,
 * `tools/e2e-stand.ps1` ставить прапор сам). Без підміни відповідей.
 *
 * Дані: власний шаблон `E2EVmr<штамп>` з версіями 1.0.0.0 і 2.0.0.0 (клон), два власні проєкти на 1.0.0.0 (один переносимо
 * «увесь проєкт», другий - «з місяця») і по документу. Шаблон/проєкт/документ API видалити не дає - залишок живе в одноразовій
 * базі стенда; тему `afterAll` повертає в `light`.
 *
 * Запуск: `-Grep "Version migration range"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = String(Date.now());
const V1 = '1.0.0.0';
const V2 = '2.0.0.0';

interface Arranged {
  readonly documentId: number;
  readonly projectId: number;
  readonly periodKey: number;
  /** Період X для «з місяця» (не перший і не останній місяць календаря проєкту). */
  readonly fromKey: number;
}

let whole: Arranged | null = null;
let partial: Arranged | null = null;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function arrangeProject(api: APIRequestContext, versionId: number, tag: string): Promise<Arranged> {
  const policies = await ok<{ id: number; code: string }[]>(await api.get('/api/v1/projects/period-policies'), 'політики');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  const project = await ok<{ projectId: number }>(
    await api.post('/api/v1/projects', {
      data: {
        code: `E2EVmr${tag}${Stamp}`,
        nameL10n: { en: `E2E migration range ${tag} ${Stamp}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await api.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація');
  const structure = await ok<{ sheets: { id: number }[] }>(await api.get(`/api/v1/template-versions/${String(versionId)}/structure`), 'структура');
  const document = await ok<{ documentId: number }>(
    await api.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await api.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар',
  );
  const keys = calendar.periods.map((p) => p.periodKey).sort((a, b) => a - b);
  const open = calendar.periods.find((p) => p.state === 'Open') ?? calendar.periods[0];
  expect(keys.length, 'у проєкту замало періодів').toBeGreaterThan(2);

  return {
    documentId: document.documentId,
    projectId: project.projectId,
    periodKey: open?.periodKey ?? 0,
    fromKey: keys[Math.min(2, keys.length - 1)] ?? 0,
  };
}

async function seedAll(api: APIRequestContext): Promise<void> {
  const template = await ok<{ templateId: number }>(
    await api.post('/api/v1/templates', { data: { code: `E2EVmr${Stamp}`, nameL10n: { en: `E2E migration range ${Stamp}` } } }),
    'шаблон',
  );
  const v1 = await ok<{ versionId: number }>(
    await api.post(`/api/v1/templates/${String(template.templateId)}/versions`, { data: { versionNumber: V1, cloneFromVersionId: null } }),
    'версія 1',
  );
  const base = `/api/v1/template-versions/${String(v1.versionId)}`;
  await ok(await api.put(`${base}/sheets/S1`, { data: { nameL10n: { en: 'Sheet 1' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null } }), 'аркуш');
  const table = await ok<{ id: number }>(
    await api.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: 'Table 1' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );
  await ok(
    await api.put(`${base}/tables/${String(table.id)}/columns/C1`, {
      data: {
        headerL10n: { en: 'C1' }, ordinal: 0, dataType: 'String', isRequired: false, isReadOnly: false, isHidden: false,
        precision: null, scale: null, defaultValue: null, displayFormat: null, lookupFilter: null, lookupRegistryDefId: null, styleId: null, unitId: null,
      },
    }),
    'стовпець',
  );
  await ok(await api.post(`${base}/publish`, { data: { reason: 'e2e: діапазон переносу, v1' } }), 'публікація v1');
  const v2 = await ok<{ versionId: number }>(
    await api.post(`/api/v1/templates/${String(template.templateId)}/versions`, { data: { versionNumber: V2, cloneFromVersionId: v1.versionId } }),
    'версія 2',
  );
  await ok(await api.post(`/api/v1/template-versions/${String(v2.versionId)}/publish`, { data: { reason: 'e2e: діапазон переносу, v2' } }), 'публікація v2');

  whole = await arrangeProject(api, v1.versionId, 'W');
  partial = await arrangeProject(api, v1.versionId, 'P');
}

async function setTheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  const put = await page.request.put('/api/v1/me/preferences/theme', {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify(scheme),
  });
  expect(put.ok(), `тема: ${String(put.status())}`).toBe(true);
}

async function signIn(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
  await setTheme(page, scheme);
}

async function openDialog(page: Page, a: Arranged, scheme: 'light' | 'dark'): Promise<void> {
  await page.goto(`/documents/${String(a.documentId)}?periodKey=${String(a.periodKey)}`);
  await expect
    .poll(() => page.evaluate(() => document.documentElement.getAttribute('data-mantine-color-scheme')), { timeout: 15_000 })
    .toBe(scheme);
  await openDocumentMore(page);
  await page.locator('[data-migrate-version]').click();
  await expect(page.getByRole('dialog')).toBeVisible();
}

async function pickTarget(page: Page): Promise<void> {
  await page.getByTestId('migrate-target').click();
  await page.getByRole('option', { name: V2 }).click();
}

async function currentVersion(page: Page, a: Arranged): Promise<string> {
  const targets = await ok<{ currentVersion: string }>(await page.request.get(`/api/v1/documents/${String(a.documentId)}/migrate-version`), 'цілі переносу');

  return targets.currentVersion;
}

test.describe('Version migration range', () => {
  test.describe.configure({ timeout: 240_000, mode: 'serial' });

  test.beforeAll(async ({ browser }) => {
    const context = await browser.newContext();
    try {
      const login = await context.request.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } });
      expect(login.ok(), `вхід: ${String(login.status())}`).toBe(true);
      await seedAll(context.request);
    } finally {
      await context.close();
    }
  });

  test.afterAll(async ({ browser }) => {
    // Дані (шаблон/проєкти/документи) API не видаляє - лишаються в одноразовій базі стенда; тема повертається.
    const context = await browser.newContext();
    try {
      await context.request.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } });
      await context.request.put('/api/v1/me/preferences/theme', {
        headers: { 'Content-Type': 'application/json' },
        data: JSON.stringify('light'),
      });
    } finally {
      await context.close();
    }
  });

  for (const scheme of ['light', 'dark'] as const) {
    for (const width of [1440, 500] as const) {
      test(`Version migration range: діалог, ${scheme}, ${String(width)} px - обсяг і період видно, axe чистий`, async ({ page }) => {
        const a = partial;
        expect(a).not.toBeNull();
        await page.setViewportSize({ width, height: 900 });
        await signIn(page, scheme);
        await openDialog(page, a as Arranged, scheme);

        const range = page.getByTestId('migrate-range');
        await expect(range, 'прапор FromMonthEnabled не ввімкнено на стенді - немає вибору обсягу').toBeVisible();
        await expect(range.getByRole('radio', { name: /Whole project|Весь проект|Бүкіл жоба/ })).toBeChecked();
        await range.getByRole('radio', { name: /From month|С месяца|Айдан бастап/ }).check();
        await expect(page.locator('[data-migrate-range-block] .ecr-period-field input')).toBeVisible();

        expect(await axeViolations(page, 'color-contrast'), `контраст (${scheme}, ${String(width)})`).toEqual([]);
        expect(await axeViolations(page, 'label'), `мітки (${scheme}, ${String(width)})`).toEqual([]);
      });
    }
  }

  test('Version migration range: «увесь проєкт» - сухий прогін без періоду, перенос на 2.0.0.0', async ({ page }) => {
    const a = whole as Arranged;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openDialog(page, a, 'light');

    await pickTarget(page);
    await page.getByTestId('migrate-dry-run').click();
    await expect(page.locator('[data-migrate-can-apply]')).toBeVisible({ timeout: 60_000 });
    await expect(page.locator('[data-migrate-effective]'), 'для «увесь проєкт» ефективний період не показуємо').toHaveCount(0);

    await page.getByTestId('migrate-apply').click();
    await expect.poll(() => currentVersion(page, a), { timeout: 120_000, intervals: [1_000, 2_000, 5_000] }).toBe(V2);
  });

  test('Version migration range: «з місяця X» - сухий прогін показує ефективний період, перенос застосовано', async ({ page }) => {
    const a = partial as Arranged;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openDialog(page, a, 'light');

    await pickTarget(page);
    await page.getByTestId('migrate-range').getByRole('radio', { name: /From month|С месяца|Айдан бастап/ }).check();
    const dryRun = page.getByTestId('migrate-dry-run');
    await expect(dryRun, 'без періоду X сухий прогін недоступний').toBeDisabled();

    const field = page.locator('[data-migrate-range-block] .ecr-period-field input');
    await field.click();
    await field.fill(String(a.fromKey));
    await field.blur();
    await expect(dryRun).toBeEnabled();

    await dryRun.click();
    await expect(page.locator('[data-migrate-can-apply]')).toBeVisible({ timeout: 60_000 });
    // Звичайний шаблон (не «рік-сімейство Q-17»): ефективний період збігається з X, попередження про січень немає.
    await expect(page.locator('[data-migrate-effective]')).toHaveAttribute('data-migrate-effective', String(a.fromKey));
    await expect(page.locator('[data-migrate-warn-january]')).toHaveCount(0);

    await page.getByTestId('migrate-apply').click();
    await expect.poll(() => currentVersion(page, a), { timeout: 120_000, intervals: [1_000, 2_000, 5_000] }).toBe(V2);
  });
});
