import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';

/**
 * Сторінка методології на живому стенді — ✎ 24.11, рішення людини: «має бути тільки наш новий редактор».
 *   1. сторінка відкривається на ПОТОЧНІЙ версії-скрипті: картка з переходом у редактор скрипта; жодної панелі
 *      формульного рушія (формули, константи, виходи, правила, правило категорії, золотий набір, покриття правил);
 *   2. «New version» — без вибору рушія, джерело копії — лише скрипти; створена версія — скрипт, і сторінка веде в
 *      редактор скрипта;
 *   3. методологічні секції (прив'язки, трасування, журнал публікацій) згортаються мишею й клавіатурою, вибір
 *      переживає перезавантаження;
 *   4. справжній axe `color-contrast` у світлій і темній темі на 1440 і 500 px, без горизонтальної прокрутки на 500 px;
 *      знімки в `ECR_E2E_SHOTS_DIR`.
 *
 * ⚠ Дані — свої: методологія `E2E_MUI_<час>` і чернетки-скрипти. `afterAll` видаляє чернетки
 * (`DELETE …/versions/{id}`) і повертає тему `e2e-admin` до світлої; саму методологію API не видаляє — порожній
 * залишок на одноразовому стенді (як `resizableModal.spec.ts`).
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "Methodology page UI"`.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const ShotsDir = process.env['ECR_E2E_SHOTS_DIR'] ?? '';

/** Секції формульного рушія, яких на сторінці бути не може. */
const FormulaSections = ['formulas', 'constants', 'outputs', 'rules', 'categoryRule', 'requiredInputs', 'tests', 'ruleCoverage', 'outputCoverage'];

interface Seed {
  readonly methodologyId: number;
  readonly versionId: number;
}

let seed: Seed | null = null;

async function signedIn(browser: Browser): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', { data: Admin });
  expect(login.ok(), `вхід: ${String(login.status())} ${await login.text()}`).toBe(true);
  return context;
}

async function signIn(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  await page.goto('/login');
  await page.evaluate((colour: string) => localStorage.setItem('mantine-color-scheme-value', colour), scheme);
  await page.reload();
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.userName);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });

  // ⚠ Тема з сервера перемагає localStorage (`usePreferenceSync`) — пишемо налаштування і перевіряємо атрибут.
  const put = await page.request.put('/api/v1/me/preferences/theme', {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify(scheme),
  });
  expect(put.ok(), `тема: ${String(put.status())} ${await put.text()}`).toBe(true);
  await page.reload();
  await expect
    .poll(() => page.evaluate(() => document.documentElement.getAttribute('data-mantine-color-scheme')), { timeout: 15_000 })
    .toBe(scheme);
}

const pageUrl = (data: Seed): string => `/admin/methodologies/${String(data.methodologyId)}/versions`;
const scriptUrl = (methodologyId: number, versionId: number): string =>
  `/admin/methodologies/${String(methodologyId)}/versions/${String(versionId)}/script`;

async function openPage(page: Page, data: Seed): Promise<void> {
  await page.goto(pageUrl(data));
  await expect(page.getByTestId('current-script-version')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('methodology-section-toggle-bindings')).toBeVisible({ timeout: 30_000 });
}

async function shot(page: Page, testInfo: { attach: (n: string, o: { body: Buffer; contentType: string }) => Promise<void> }, name: string): Promise<void> {
  const body = await page.screenshot({ fullPage: false });
  await testInfo.attach(name, { body, contentType: 'image/png' });
  if (ShotsDir !== '') {
    mkdirSync(ShotsDir, { recursive: true });
    await page.screenshot({ path: path.join(ShotsDir, `${name}.png`) });
  }
}

test.describe('Methodology page UI: script versions only, new version is a script, collapsible sections', () => {
  test.describe.configure({ timeout: 240_000, mode: 'serial' });

  test.beforeAll(async ({ browser }) => {
    if (PeriodKey === '') throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD (tools/e2e-stand.ps1)');

    const admin = await signedIn(browser);
    try {
      const stamp = String(Date.now());
      const created = await admin.request.post('/api/v1/methodologies', {
        data: { code: `E2E_MUI_${stamp}`, group: null, kind: 'DataDriven', nameL10n: { en: `E2E methodology UI ${stamp}` } },
      });
      expect(created.ok(), `методологія: ${await created.text()}`).toBe(true);
      const methodologyId = ((await created.json()) as { id: number }).id;

      const draft = await admin.request.post(`/api/v1/methodologies/${String(methodologyId)}/versions`, {
        data: { versionNumber: '1.0', copyFromVersionId: null, level: 'Configuration', engine: 'Script' },
      });
      expect(draft.ok(), `чернетка-скрипт: ${await draft.text()}`).toBe(true);
      const versionId = ((await draft.json()) as { id: number }).id;

      seed = { methodologyId, versionId };
    } finally {
      await admin.close();
    }
  });

  test.afterAll(async ({ browser }) => {
    const admin = await signedIn(browser);
    try {
      await admin.request.put('/api/v1/me/preferences/theme', { headers: { 'Content-Type': 'application/json' }, data: JSON.stringify('light') });
      if (seed !== null) {
        const listed = await admin.request.get(`/api/v1/methodologies/${String(seed.methodologyId)}/versions`);
        const drafts = listed.ok()
          ? ((await listed.json()) as { id: number; status: string }[]).filter((item) => item.status === 'Draft')
          : [{ id: seed.versionId, status: 'Draft' }];
        for (const item of drafts) {
          const removed = await admin.request.delete(`/api/v1/methodologies/${String(seed.methodologyId)}/versions/${String(item.id)}`);
          expect([200, 204, 404], `видалення чернетки ${String(item.id)}: ${String(removed.status())}`).toContain(removed.status());
        }
      }
    } finally {
      await admin.close();
    }
  });

  test('opens on the current script version; no formula-engine panels', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openPage(page, data);

    const card = page.getByTestId('current-script-version');
    await expect(card).toContainText('1.0');
    await expect(card.getByRole('link', { name: 'Open script editor' })).toHaveAttribute(
      'href',
      scriptUrl(data.methodologyId, data.versionId),
    );

    // Робоча таблиця — лише скрипти, «Open» — посилання в редактор скрипта.
    const table = page.getByTestId('script-versions');
    await expect(table.getByRole('link', { name: 'Open', exact: true })).toHaveAttribute(
      'href',
      scriptUrl(data.methodologyId, data.versionId),
    );

    // Режими поточної версії — на сторінці (скрипт їх читає).
    await expect(page.getByTestId('methodology-section-toggle-modes')).toBeVisible();

    for (const section of FormulaSections) {
      await expect(page.getByTestId(`methodology-section-toggle-${section}`), `секція ${section}`).toHaveCount(0);
    }
    await expect(page.getByRole('button', { name: /Add formula|Add constant|Add rule|Add output/i })).toHaveCount(0);
    await expect(page.locator('.monaco-editor')).toHaveCount(0);

    // Перехід із картки — у редактор скрипта.
    await card.getByRole('link', { name: 'Open script editor' }).click();
    await page.waitForURL((url) => url.pathname === scriptUrl(data.methodologyId, data.versionId), { timeout: 30_000 });
    await expect(page.getByTestId('script-editor')).toBeVisible({ timeout: 60_000 });
  });

  test('"New version": no engine choice, copy from lists only script versions, the created version opens in the script editor', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openPage(page, data);

    await page.getByRole('button', { name: 'New version', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'New draft version' });
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expect(dialog.getByRole('radiogroup')).toHaveCount(0);
    await expect(dialog.getByText(/^Formulas$/)).toHaveCount(0);

    await dialog.getByRole('textbox', { name: 'Copy from' }).click();
    const options = page.getByRole('option');
    await expect(options).toHaveCount(1);
    await expect(options.first()).toContainText('1.0');
    await page.keyboard.press('Escape');

    await dialog.getByLabel('Version number').fill('2.0');
    const created = page.waitForResponse(
      (r) => r.request().method() === 'POST' && r.url().endsWith(`/api/v1/methodologies/${String(data.methodologyId)}/versions`),
    );
    await dialog.getByRole('button', { name: 'Create draft' }).click();
    const response = await created;
    expect(response.ok(), `створення версії: ${String(response.status())}`).toBe(true);
    const body = JSON.parse(response.request().postData() ?? '{}') as { engine?: string };
    expect(body.engine).toBe('Script');
    const version = (await response.json()) as { id: number; engine?: string };
    expect(version.engine).toBe('Script');

    await page.waitForURL((url) => url.pathname === scriptUrl(data.methodologyId, version.id), { timeout: 30_000 });
    await expect(page.getByTestId('script-editor')).toBeVisible({ timeout: 60_000 });
  });

  test('a methodology with only a formula-engine version shows "being moved", no editor and no version row', async ({ page, browser }) => {
    // ⚠ Формульну версію заводить API лише з увімкненим формульним авторингом — стенд e2e його вмикає
    // (`tools/e2e-stand.ps1`, P-1124-1); у продукті — вимкнено, і така методологія буває лише до вікна P-1124-2.
    const admin = await signedIn(browser);
    let methodologyId = 0;
    let versionId = 0;
    try {
      const stamp = String(Date.now());
      const created = await admin.request.post('/api/v1/methodologies', {
        data: { code: `E2E_MUF_${stamp}`, group: null, kind: 'DataDriven', nameL10n: { en: `E2E formulas only ${stamp}` } },
      });
      expect(created.ok(), `методологія: ${await created.text()}`).toBe(true);
      methodologyId = ((await created.json()) as { id: number }).id;
      const draft = await admin.request.post(`/api/v1/methodologies/${String(methodologyId)}/versions`, {
        data: { versionNumber: 'F1', copyFromVersionId: null, level: 'Configuration', engine: 'Formulas' },
      });
      expect(draft.ok(), `формульна чернетка: ${await draft.text()}`).toBe(true);
      versionId = ((await draft.json()) as { id: number }).id;

      await page.setViewportSize({ width: 1440, height: 900 });
      await signIn(page, 'light');
      await page.goto(pageUrl({ methodologyId, versionId }));
      await expect(page.getByTestId('methodology-converting')).toBeVisible({ timeout: 30_000 });
      await expect(page.getByTestId('current-script-version')).toHaveCount(0);
      await expect(page.getByTestId('script-versions')).toHaveCount(0);
      await expect(page.getByText('F1', { exact: true })).toHaveCount(0);
      await expect(page.locator('.monaco-editor')).toHaveCount(0);
    } finally {
      if (versionId !== 0) await admin.request.delete(`/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}`);
      await admin.close();
    }
  });

  test('sections collapse with mouse and keyboard; aria-expanded and the choice survive a reload', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openPage(page, data);

    const bindings = page.getByTestId('methodology-section-toggle-bindings');
    await expect(bindings).toHaveAttribute('aria-expanded', 'true');
    const bodyId = await bindings.getAttribute('aria-controls');
    expect(bodyId).not.toBeNull();
    await expect(page.locator(`[id="${bodyId!}"]`)).toBeVisible();

    await bindings.click();
    await expect(bindings).toHaveAttribute('aria-expanded', 'false');
    await expect(page.locator(`[id="${bodyId!}"]`)).toBeHidden();

    // Клавіатура: Enter на кнопці в заголовку розгортає назад.
    await bindings.focus();
    await page.keyboard.press('Enter');
    await expect(bindings).toHaveAttribute('aria-expanded', 'true');

    // Згорнути «трасування» і перезавантажити — вибір пам'ятається.
    const trace = page.getByTestId('methodology-section-toggle-traceSteps');
    await trace.focus();
    await page.keyboard.press('Space');
    await expect(trace).toHaveAttribute('aria-expanded', 'false');
    await page.reload();
    await expect(page.getByTestId('methodology-section-toggle-traceSteps')).toHaveAttribute('aria-expanded', 'false', { timeout: 30_000 });
    await expect(page.getByTestId('methodology-section-toggle-bindings')).toHaveAttribute('aria-expanded', 'true');
    // Журнал публікацій — згорнутий за замовчуванням.
    await expect(page.getByTestId('methodology-section-toggle-publications')).toHaveAttribute('aria-expanded', 'false');

    // Заголовки секцій — h2 з кнопкою всередині.
    await expect(page.getByTestId('methodology-section-toggle-traceSteps').locator('xpath=ancestor::h2')).toHaveCount(1);

    // Повернути стан, щоб наступні тести бачили розгорнуту сторінку.
    await page.getByTestId('methodology-section-toggle-traceSteps').click();
    await expect(page.getByTestId('methodology-section-toggle-traceSteps')).toHaveAttribute('aria-expanded', 'true');
  });

  for (const scheme of ['light', 'dark'] as const) {
    test(`${scheme}: real axe color-contrast at 1440 and 500 px, no horizontal scroll, screenshots`, async ({ page }, testInfo) => {
      const data = seed!;
      await page.setViewportSize({ width: 1440, height: 900 });
      await signIn(page, scheme);
      await openPage(page, data);
      // Журнал розгортається, щоб контраст перевірявся й на ньому.
      await page.getByTestId('methodology-section-toggle-publications').click();

      expect(await axeViolations(page, 'color-contrast'), `color-contrast page @1440 ${scheme}`).toEqual([]);
      await shot(page, testInfo, `methodology-page-${scheme}-1440`);

      await page.getByRole('button', { name: 'New version', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'New draft version' });
      await expect(dialog).toBeVisible({ timeout: 10_000 });
      expect(await axeViolations(page, 'color-contrast'), `color-contrast new version @1440 ${scheme}`).toEqual([]);
      await shot(page, testInfo, `methodology-new-version-${scheme}-1440`);
      await page.keyboard.press('Escape');
      await expect(dialog).toBeHidden({ timeout: 10_000 });

      await page.setViewportSize({ width: 500, height: 900 });
      await page.waitForTimeout(300);
      const overflow = await page.evaluate(() => ({
        doc: document.documentElement.scrollWidth - document.documentElement.clientWidth,
        body: document.body.scrollWidth - document.body.clientWidth,
      }));
      expect(overflow.doc, `горизонтальна прокрутка сторінки @500 ${scheme}`).toBeLessThanOrEqual(1);
      expect(overflow.body).toBeLessThanOrEqual(1);
      expect(await axeViolations(page, 'color-contrast'), `color-contrast page @500 ${scheme}`).toEqual([]);
      await shot(page, testInfo, `methodology-page-${scheme}-500`);
      await page.setViewportSize({ width: 1440, height: 900 });
    });
  }
});
