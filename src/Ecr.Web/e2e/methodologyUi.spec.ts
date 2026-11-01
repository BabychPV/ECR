import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type Browser, type BrowserContext, type Locator, type Page } from '@playwright/test';
import { ForbiddenExamples, SyntaxExamples } from '../src/features/methodologies/formulaSyntax';
import { axeViolations } from './axeRule';

/**
 * Сторінка версії методології (lane/integ-methodology-ui) на живому стенді:
 *   1. довідка синтаксису в діалозі формули відкривається кнопкою (і клавіатурою), не перекриває
 *      редактор і пояснює `@`, `CST.`, `!`, `HDR.`, `[Period]`; кожен приклад довідки СПРАВЖНІЙ
 *      розбір сервера приймає, кожен заборонений — відхиляє зі своєю причиною
 *      (`POST /api/v1/expressions/validate`, діалект `Methodology`);
 *   2. матриця покриття: межі — два PeriodPicker; нова межа йде в запит, перевернуте вікно й
 *      очищене поле — відмова без запиту;
 *   3. секції згортаються мишею й клавіатурою, `aria-expanded` і вибір переживають перезавантаження;
 *   4. справжній axe `color-contrast` у світлій і темній темі на 1440 і 500 px, без горизонтальної
 *      прокрутки на 500 px; знімки в `ECR_E2E_SHOTS_DIR`.
 *
 * ⚠ Дані — свої: методологія `E2E_MUI_<час>` і чернетка. `afterAll` видаляє чернетку
 * (`DELETE …/versions/{id}`) і повертає тему `e2e-admin` до світлої; саму методологію API не
 * видаляє — порожній залишок на одноразовому стенді (як `resizableModal.spec.ts`).
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "Methodology page UI"`.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const ShotsDir = process.env['ECR_E2E_SHOTS_DIR'] ?? '';

interface Seed {
  readonly methodologyId: number;
  readonly versionId: number;
}

let seed: Seed | null = null;

interface Diagnostic {
  readonly code: string;
  readonly message: string;
  readonly messageKey?: string | null;
}

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

async function openPage(page: Page, data: Seed): Promise<void> {
  await page.goto(pageUrl(data));
  await expect(page.getByTestId('methodology-section-toggle-formulas')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('methodology-section-toggle-ruleCoverage')).toBeVisible({ timeout: 30_000 });
}

async function openFormula(page: Page): Promise<Locator> {
  await page.getByRole('button', { name: /Add formula/i }).click();
  const dialog = page.getByRole('dialog', { name: /Formula/i });
  await expect(dialog).toBeVisible({ timeout: 10_000 });
  await expect(dialog.locator('.monaco-editor')).toBeVisible({ timeout: 60_000 });
  return dialog;
}

async function shot(page: Page, testInfo: { attach: (n: string, o: { body: Buffer; contentType: string }) => Promise<void> }, name: string): Promise<void> {
  const body = await page.screenshot({ fullPage: false });
  await testInfo.attach(name, { body, contentType: 'image/png' });
  if (ShotsDir !== '') {
    mkdirSync(ShotsDir, { recursive: true });
    await page.screenshot({ path: path.join(ShotsDir, `${name}.png`) });
  }
}

test.describe('Methodology page UI: syntax help, rule coverage periods, collapsible sections', () => {
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
        data: { versionNumber: '1.0', copyFromVersionId: null, level: 'Configuration' },
      });
      expect(draft.ok(), `чернетка: ${await draft.text()}`).toBe(true);
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
        const removed = await admin.request.delete(`/api/v1/methodologies/${String(seed.methodologyId)}/versions/${String(seed.versionId)}`);
        expect([200, 204, 404], `видалення чернетки: ${String(removed.status())}`).toContain(removed.status());
      }
    } finally {
      await admin.close();
    }
  });

  test('help examples: the real parser accepts every example and rejects every forbidden one', async ({ browser }) => {
    const admin = await signedIn(browser);
    try {
      const validate = async (expression: string): Promise<Diagnostic[]> => {
        const response = await admin.request.post('/api/v1/expressions/validate', {
          data: { expression, dialect: 'Methodology', templateVersionId: null, tableDefId: null, rowKey: null, columnDefId: null },
        });
        expect(response.ok(), `validate «${expression}»: ${String(response.status())} ${await response.text()}`).toBe(true);
        return ((await response.json()) as { diagnostics: Diagnostic[] }).diagnostics;
      };

      for (const example of SyntaxExamples) {
        const diagnostics = await validate(example.code);
        expect(diagnostics, `приклад ${example.id} «${example.code}»: ${JSON.stringify(diagnostics)}`).toEqual([]);
      }

      const expectedKey: Record<string, string> = {
        caret: 'expr.caretNotPower',
        ampersand: 'expr.ampersandNotConcat',
        bareName: 'expr.bareNameNeedsAt',
        cellRef: 'expr.cellReferencesForbiddenInMethodology',
        caseSensitive: 'expr.unknownFunctionCase',
      };
      for (const example of ForbiddenExamples) {
        const diagnostics = await validate(example.code);
        expect(
          diagnostics.map((d) => d.messageKey),
          `заборонений ${example.id} «${example.code}»: ${JSON.stringify(diagnostics)}`,
        ).toContain(expectedKey[example.id]);
      }
    } finally {
      await admin.close();
    }
  });

  test('syntax help opens with mouse and keyboard, shows the tokens and does not cover the editor', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openPage(page, data);
    const dialog = await openFormula(page);

    const toggle = dialog.getByTestId('syntax-help-toggle');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(dialog.getByTestId('syntax-help')).toBeHidden();

    await toggle.focus();
    await page.keyboard.press('Enter');
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const help = dialog.getByTestId('syntax-help');
    await expect(help).toBeVisible();
    for (const token of ['@Name', 'CST.Name', '!Name', 'HDR.Name', '[Period].Days']) {
      await expect(help.locator(`[data-syntax-token="${token}"]`), token).toBeVisible();
    }
    await expect(help.getByTestId('syntax-help-functions').locator('[data-function="Pow"]')).toBeVisible({ timeout: 15_000 });
    await expect(help.locator('[data-example]')).toHaveCount(SyntaxExamples.length);

    // Панель — під редактором, а не поверх нього: прямокутники не перетинаються.
    const editor = await dialog.locator('.monaco-editor').boundingBox();
    const panel = await help.boundingBox();
    expect(editor).not.toBeNull();
    expect(panel).not.toBeNull();
    expect(panel!.y, 'довідка нижче за редактор').toBeGreaterThanOrEqual(editor!.y + editor!.height - 1);

    await page.keyboard.press('Space');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(help).toBeHidden();
  });

  test('rule coverage: two period pickers; a new bound filters, reversed and empty windows are refused without a request', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openPage(page, data);

    const section = page.locator('[data-section="ruleCoverage"]');
    const filters = section.getByTestId('rule-coverage-filters');
    await expect(filters.locator('.ecr-period')).toHaveCount(2);

    const year = new Date().getFullYear();
    const narrowed = String(year * 100 + 3);
    const request = page.waitForRequest((r) => r.url().includes('/rule-coverage') && r.url().includes(`periodFrom=${narrowed}`));
    const from = section.getByLabel('Period from');
    await from.click();
    await from.fill(narrowed);
    await request;
    await page.keyboard.press('Enter');

    // Перевернуте вікно: нижня межа пізніше за верхню — відмова, запиту немає.
    let sent = 0;
    page.on('request', (r) => {
      if (r.url().includes('/rule-coverage')) sent += 1;
    });
    const to = section.getByLabel('Period to');
    await to.click();
    await to.fill(String((year - 2) * 100 + 12));
    await expect(section.getByTestId('rule-coverage-window')).toContainText(/empty/i);

    await to.fill('');
    await expect(section.getByTestId('rule-coverage-window')).toContainText(/both periods/i);
    expect(sent, 'жодного запиту на відхилене вікно').toBe(0);
  });

  test('sections collapse with mouse and keyboard; aria-expanded and the choice survive a reload', async ({ page }) => {
    const data = seed!;
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, 'light');
    await openPage(page, data);

    const constants = page.getByTestId('methodology-section-toggle-constants');
    await expect(constants).toHaveAttribute('aria-expanded', 'true');
    const bodyId = await constants.getAttribute('aria-controls');
    expect(bodyId).not.toBeNull();
    await expect(page.locator(`[id="${bodyId!}"]`)).toBeVisible();

    await constants.click();
    await expect(constants).toHaveAttribute('aria-expanded', 'false');
    await expect(page.locator(`[id="${bodyId!}"]`)).toBeHidden();

    // Клавіатура: Enter на кнопці в заголовку розгортає назад.
    await constants.focus();
    await page.keyboard.press('Enter');
    await expect(constants).toHaveAttribute('aria-expanded', 'true');

    // Згорнути «Rules» і перезавантажити — вибір пам'ятається.
    const rules = page.getByTestId('methodology-section-toggle-rules');
    await rules.focus();
    await page.keyboard.press('Space');
    await expect(rules).toHaveAttribute('aria-expanded', 'false');
    await page.reload();
    await expect(page.getByTestId('methodology-section-toggle-rules')).toHaveAttribute('aria-expanded', 'false', { timeout: 30_000 });
    await expect(page.getByTestId('methodology-section-toggle-constants')).toHaveAttribute('aria-expanded', 'true');
    // Журнал публікацій — згорнутий за замовчуванням.
    await expect(page.getByTestId('methodology-section-toggle-publications')).toHaveAttribute('aria-expanded', 'false');

    // Заголовки секцій — h2 з кнопкою всередині.
    // ⚠ Через кнопку, а не за назвою: заголовок секції — «Row selection rules» (сід), а /Rules/ чутливий до регістру.
    await expect(page.getByTestId('methodology-section-toggle-rules').locator('xpath=ancestor::h2')).toHaveCount(1);

    // Повернути стан, щоб наступні тести бачили розгорнуту сторінку.
    await page.getByTestId('methodology-section-toggle-rules').click();
    await expect(page.getByTestId('methodology-section-toggle-rules')).toHaveAttribute('aria-expanded', 'true');
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

      const dialog = await openFormula(page);
      await dialog.getByTestId('syntax-help-toggle').click();
      await expect(dialog.getByTestId('syntax-help-functions')).toBeVisible({ timeout: 15_000 });
      expect(await axeViolations(page, 'color-contrast'), `color-contrast help @1440 ${scheme}`).toEqual([]);
      await shot(page, testInfo, `methodology-syntax-help-${scheme}-1440`);
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
      await page.locator('[data-section="ruleCoverage"]').scrollIntoViewIfNeeded();
      await shot(page, testInfo, `methodology-page-${scheme}-500`);

      const small = await openFormula(page);
      await small.getByTestId('syntax-help-toggle').click();
      await expect(small.getByTestId('syntax-help-functions')).toBeVisible({ timeout: 15_000 });
      expect(await axeViolations(page, 'color-contrast'), `color-contrast help @500 ${scheme}`).toEqual([]);
      await shot(page, testInfo, `methodology-syntax-help-${scheme}-500`);
      await page.keyboard.press('Escape');
      await page.setViewportSize({ width: 1440, height: 900 });
    });
  }
});
