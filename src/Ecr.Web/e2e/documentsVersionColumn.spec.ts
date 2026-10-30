import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';

/**
 * Перелік документів, колонка «Version» (lane `integ-docs-version-col`) на живому стенді: версія шаблону за період у
 * таблиці («v<версія>» або «—», ніколи «v0»), сортування по кліку з `aria-sort` і адресою `vsort`, мультивибір-фільтр
 * (`versions`) із «Clear filters», кругообіг адреси після перезавантаження - і те, чого не бачить jsdom: контраст
 * (справжній axe), перенос на 500 px, відсутність горизонтальної прокрутки сторінки, видимість і клавіатура списку
 * мультивибору, знімки світлої й темної теми.
 *
 * ⚠ Другу версію шаблону дає власний шаблон `E2EVer<час>` з номером версії `2.7.0.0` і власним проєктом/документом
 * (як `multiSheet.ts`): стенд має одну версію. Шаблон, проєкт і документ API видалити не дає - залишок живе в одноразовій
 * базі стенда.
 *
 * ⚠ Тема з сервера перемагає localStorage (`usePreferenceSync`), тому тему пишемо налаштуванням `theme` і ПЕРЕВІРЯЄМО
 * атрибут `data-mantine-color-scheme`; `afterAll` повертає `light`.
 *
 * Запуск (стенд - `tools/e2e-stand.ps1`): `npx playwright test -g "Documents list: Version column"`.
 * Знімки пишуться в `ECR_E2E_SHOTS_DIR`, якщо він заданий (інакше - лише вкладення звіту).
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const ShotsDir = process.env['ECR_E2E_SHOTS_DIR'] ?? '';
const OwnVersion = '2.7.0.0';

interface Seed {
  projectCode: string;
  periodKey: number;
}

let seed: Seed | null = null;

async function ok<T>(response: Awaited<ReturnType<APIRequestContext['get']>>, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function signedIn(browser: Browser): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', { data: { userName: Admin.userName, password: Admin.password } });
  expect(login.ok(), `вхід: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

/** Шаблон із одним аркушем/стовпцем у версії `OwnVersion`, проєкт і один документ. */
async function createOwnDocument(request: APIRequestContext): Promise<Seed> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await request.post('/api/v1/templates', { data: { code: `E2EVer${stamp}`, nameL10n: { en: `E2E version ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: OwnVersion, cloneFromVersionId: null },
    }),
    'версія шаблону',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(
    await request.put(`${base}/sheets/S1`, {
      data: { nameL10n: { en: 'Sheet 1' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
    }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await request.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: 'Table 1' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );
  await ok(
    await request.put(`${base}/tables/${String(table.id)}/columns/C1`, {
      data: {
        headerL10n: { en: 'C1' },
        ordinal: 0,
        dataType: 'String',
        isRequired: false,
        isReadOnly: false,
        isHidden: false,
        precision: null,
        scale: null,
        defaultValue: null,
        displayFormat: null,
        lookupFilter: null,
        lookupRegistryDefId: null,
        styleId: null,
        unitId: null,
      },
    }),
    'стовпець',
  );
  await ok(await request.post(`${base}/publish`, { data: { reason: 'e2e: колонка Version у переліку' } }), 'публікація версії');

  const policies = await ok<{ id: number; code: string }[]>(await request.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  const projectCode = `E2EVerPrj${stamp}`;
  const project = await ok<{ projectId: number }>(
    await request.post('/api/v1/projects', {
      data: {
        code: projectCode,
        nameL10n: { en: `E2E version project ${stamp}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: version.versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');
  const structure = await ok<{ sheets: { id: number }[] }>(await request.get(`${base}/structure`), 'структура версії');
  await ok(
    await request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар проєкту',
  );
  const period = calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0];
  expect(period, 'у проєкту немає періодів').toBeDefined();

  return { projectCode, periodKey: period?.periodKey ?? 0 };
}

async function setTheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  // ⚠ Тема з сервера перемагає localStorage: пишемо налаштування, перезавантажуємо й ПЕРЕВІРЯЄМО атрибут.
  const put = await page.request.put('/api/v1/me/preferences/theme', {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify(scheme),
  });
  expect(put.ok(), `тема: ${String(put.status())} ${await put.text()}`).toBe(true);
}

async function signIn(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.userName);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
  await setTheme(page, scheme);
}

async function openList(page: Page, scheme: 'light' | 'dark', query = ''): Promise<void> {
  await page.goto(`/?periodKey=${PeriodKey}${query}`);
  await expect
    .poll(() => page.evaluate(() => document.documentElement.getAttribute('data-mantine-color-scheme')), { timeout: 15_000 })
    .toBe(scheme);
  await expect(page.locator('[data-documents-table]'), 'таблиця переліку не з’явилась').toBeVisible({ timeout: 30_000 });
}

/** Тексти комірок колонки «Version» усіх рядків (індекс колонки - за заголовком, не зашитий). */
async function versionTexts(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const table = document.querySelector('[data-documents-table]');
    const headers = Array.from(table?.querySelectorAll('thead th') ?? []);
    const index = headers.findIndex((th) => th.getAttribute('data-column') === 'version');
    return Array.from(table?.querySelectorAll('tbody tr') ?? []).map((row) => {
      const cell = row.querySelectorAll('td')[index];
      return (cell?.textContent ?? '').replace(/\s+/g, ' ').trim();
    });
  });
}

/** `v2.7.0.0` -> `2.7.0.0` або `null` для «—». */
const bare = (text: string): string | null => (text.startsWith('—') ? null : text.replace(/^v/, '').split(' ')[0] ?? null);

function compare(a: string, b: string): number {
  const x = a.split('.').map(Number);
  const y = b.split('.').map(Number);
  for (let i = 0; i < Math.max(x.length, y.length); i += 1) {
    const d = (x[i] ?? 0) - (y[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}

async function shot(page: Page, testInfo: { attach: (n: string, o: { body: Buffer; contentType: string }) => Promise<void> }, name: string): Promise<void> {
  const body = await page.screenshot({ fullPage: false });
  await testInfo.attach(name, { body, contentType: 'image/png' });
  if (ShotsDir !== '') {
    mkdirSync(ShotsDir, { recursive: true });
    await page.screenshot({ path: path.join(ShotsDir, `${name}.png`) });
  }
}

test.describe('Documents list: Version column', () => {
  test.describe.configure({ timeout: 180_000, mode: 'serial' });

  test.beforeAll(async ({ browser }) => {
    if (PeriodKey === '') throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD (tools/e2e-stand.ps1)');
    const admin = await signedIn(browser);
    try {
      seed = await createOwnDocument(admin.request);
    } finally {
      await admin.close();
    }
  });

  test.afterAll(async ({ browser }) => {
    const admin = await signedIn(browser);
    try {
      await admin.request.put('/api/v1/me/preferences/theme', {
        headers: { 'Content-Type': 'application/json' },
        data: JSON.stringify('light'),
      });
    } finally {
      await admin.close();
    }
  });

  test('таблиця: колонка Version показує «v<версія>» або «—», ніколи «v0»; є рядок власної версії', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openList(page, 'light');

    await expect(page.locator('[data-documents-table] thead th[data-column="version"]')).toContainText('Version');
    await expect(page.locator('[data-documents-table] tbody tr').first()).toBeVisible({ timeout: 30_000 });

    const texts = await versionTexts(page);
    expect(texts.length, 'у переліку немає рядків').toBeGreaterThan(0);
    for (const text of texts) {
      expect(text, `комірка Version «${text}»`).toMatch(/^(v\d+(\.\d+)*|—)(\s.*)?$/);
      expect(text, 'вигадане «v0»').not.toMatch(/^v0(\.0)*(\s|$)/);
    }

    // Власний документ (шаблон версії 2.7.0.0) видно в переліку періоду стенда.
    const own = page.locator('[data-documents-table] tbody tr', { hasText: seed?.projectCode ?? '' });
    await expect(own.first(), 'власного документа (версія 2.7.0.0) немає в переліку періоду стенда').toBeVisible();
    await expect(own.first().locator('[data-document-version]')).toHaveText(new RegExp(`^v${OwnVersion.replace(/\./g, '\\.')}`));
    await shot(page, test.info(), 'docs-version-table-1440-light');
  });

  test('сортування: клік дає aria-sort ascending → descending → none, адреса vsort, перезавантаження зберігає', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openList(page, 'light');

    const th = page.locator('[data-documents-table] thead th[data-column="version"]');
    const button = th.locator('[data-version-sort]');
    await expect(th).toHaveAttribute('aria-sort', 'none');

    await button.click();
    await expect(th).toHaveAttribute('aria-sort', 'ascending');
    await expect(page).toHaveURL(/[?&]vsort=asc(&|$)/);
    const asc = (await versionTexts(page)).map(bare);
    const known = asc.filter((v): v is string => v !== null);
    for (let i = 1; i < known.length; i += 1) {
      expect(compare(known[i - 1] ?? '0', known[i] ?? '0'), `зростання: ${known.join(', ')}`).toBeLessThanOrEqual(0);
    }
    // Рядки без версії - в кінці.
    const firstNone = asc.indexOf(null);
    if (firstNone >= 0) expect(asc.slice(firstNone).every((v) => v === null), 'рядки «—» не в кінці').toBe(true);

    await page.reload();
    await expect(th).toHaveAttribute('aria-sort', 'ascending');

    await button.click();
    await expect(th).toHaveAttribute('aria-sort', 'descending');
    await expect(page).toHaveURL(/[?&]vsort=desc(&|$)/);
    const desc = (await versionTexts(page)).map(bare).filter((v): v is string => v !== null);
    for (let i = 1; i < desc.length; i += 1) {
      expect(compare(desc[i - 1] ?? '0', desc[i] ?? '0'), `спадання: ${desc.join(', ')}`).toBeGreaterThanOrEqual(0);
    }

    await button.click();
    await expect(th).toHaveAttribute('aria-sort', 'none');
    await expect(page).not.toHaveURL(/[?&]vsort=/);
  });

  test('фільтр: мультивибір звужує рядки до обраної версії, адреса versions, «Clear filters» знімає', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openList(page, 'light');

    const filter = page.locator('[data-version-filter]');
    await expect(filter, 'у фільтрі немає колонки Version').toBeVisible({ timeout: 30_000 });
    const before = await versionTexts(page);
    const options = [...new Set(before.map(bare).filter((v): v is string => v !== null))];
    expect(options.length, 'на сторінці немає жодної версії').toBeGreaterThan(0);
    const pick = options[0] ?? '';

    await filter.click();
    await page.getByRole('option', { name: new RegExp(`^v${pick.replace(/\./g, '\\.')}$`) }).click();
    await page.keyboard.press('Escape');
    await expect(page).toHaveURL(/[?&]versions=/);
    await expect.poll(async () => (await versionTexts(page)).every((t) => bare(t) === pick)).toBe(true);
    await expect(page.locator('[data-clear-filters]')).toBeVisible();

    // Кругообіг адреси.
    await page.reload();
    await expect(page.locator('[data-documents-table]')).toBeVisible({ timeout: 30_000 });
    await expect.poll(async () => (await versionTexts(page)).every((t) => bare(t) === pick)).toBe(true);

    await page.locator('[data-clear-filters]').click();
    await expect(page).not.toHaveURL(/[?&]versions=/);
    await expect.poll(async () => (await versionTexts(page)).length).toBe(before.length);
  });

  for (const scheme of ['light', 'dark'] as const) {
    test(`500 px, ${scheme}: колонка й список мультивибору читабельні, без горизонтальної прокрутки сторінки, клавіатура працює`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width: 500, height: 900 });
      await signIn(page, scheme);
      await openList(page, scheme);

      const th = page.locator('[data-documents-table] thead th[data-column="version"]');
      await expect(th, 'колонка Version зникла на 500 px').toBeVisible();
      await expect(page.locator('[data-documents-table] tbody tr').first()).toBeVisible({ timeout: 30_000 });

      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, 'горизонтальна прокрутка сторінки').toBeLessThanOrEqual(1);
      expect(await axeViolations(page, 'color-contrast'), `контраст (${scheme}), таблиця`).toEqual([]);
      await shot(page, testInfo, `docs-version-table-500-${scheme}`);

      // Мультивибір: відкриття клавіатурою, список повністю у вікні, вибір Enter, закриття Escape.
      const filter = page.locator('[data-version-filter]');
      await expect(filter).toBeVisible();
      await filter.scrollIntoViewIfNeeded();
      await filter.focus();
      await page.keyboard.press('ArrowDown');
      const listbox = page.getByRole('listbox');
      await expect(listbox, 'список мультивибору не відкрився з клавіатури').toBeVisible();
      const box = await listbox.boundingBox();
      expect(box, 'список без розмірів').not.toBeNull();
      expect(box?.x ?? -1, 'список виходить ліворуч').toBeGreaterThanOrEqual(0);
      expect((box?.x ?? 0) + (box?.width ?? 0), 'список виходить праворуч').toBeLessThanOrEqual(500 + 1);
      expect(box?.y ?? -1, 'список виходить угору').toBeGreaterThanOrEqual(0);
      expect((box?.y ?? 0) + (box?.height ?? 0), 'список виходить униз').toBeLessThanOrEqual(900 + 1);
      expect(await axeViolations(page, 'color-contrast'), `контраст (${scheme}), відкритий список`).toEqual([]);
      await shot(page, testInfo, `docs-version-popup-500-${scheme}`);

      await page.keyboard.press('Enter');
      await expect(page).toHaveURL(/[?&]versions=/);
      await page.keyboard.press('Escape');
      await expect(listbox).toBeHidden();
      const after = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(after, 'горизонтальна прокрутка після вибору').toBeLessThanOrEqual(1);

      // Знімок 1440 тієї ж теми.
      await page.setViewportSize({ width: 1440, height: 900 });
      await openList(page, scheme);
      await expect(page.locator('[data-documents-table] tbody tr').first()).toBeVisible({ timeout: 30_000 });
      await shot(page, testInfo, `docs-version-table-1440-${scheme}`);
    });
  }
});
