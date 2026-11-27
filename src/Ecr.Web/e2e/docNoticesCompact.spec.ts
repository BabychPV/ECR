import { mkdirSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';

/**
 * ✎ b14 doc-notices-compact (людина 25.11, doc 96: «ці повідомлення багато забирають місця»): проблеми розрахунку над
 * сіткою — ОДНА смуга в рядок, згорнута; Details — матриця «причина × місяць»; стан Details пам'ятається; a11y у
 * світлій і темній темі (axe). На 1440×900 перший рядок сітки видно без прокрутки.
 *
 * ⚠ Відповідь `calculation-issues` підміняється в браузері (рядки «бракує входів» і «неактивні»): у стенді таких проблем
 * немає, а перевіряється розкладка й доступність смуги. Причини й перехід «Show rows» тримають vitest
 * (`CalculationIssuesStrip.test.tsx`) і специфікації `calculationIssues`/`formulaErrors` (справжні проблеми стенда).
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const ReportDir = process.env['ECR_E2E_REPORT_DIR'] ?? '';
const Suffix = String(Date.now() % 100000);

test.describe('docNoticesCompact: смуга проблем розрахунку', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('docNoticesCompact: одна смуга в рядок, сітка на першому екрані; Details пам\'ятається; axe light/dark', async ({ page }) => {
    test.setTimeout(240_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, Operator.user, Operator.password);

    await page.route(/\/api\/v1\/documents\/\d+\/calculation-issues/, async (route) => {
      const response = await route.fetch();
      const body = (await response.json()) as Record<string, unknown>;
      const month = Number(PeriodKey) % 100;
      const row = (n: number, kind: string, extra: Record<string, unknown> = {}) => ({
        tableCode: `NOTICE-${Suffix}`, sheetCopyId: null, rowNumber: n, rowKey: `N-${Suffix}-${String(n)}`, kind,
        methodologyCode: 'E2E', detectedAt: '2026-04-03T08:00:00Z', monthNumber: month, methodologyId: null, ...extra,
      });
      body['items'] = [row(1, 'MissingInputs'), row(2, 'MissingInputs'), row(3, 'RowInactive', { affectedCount: 3 })];
      await route.fulfill({ response, json: body });
    });

    await page.goto(`/?periodKey=${PeriodKey}`);
    const link = page.locator('[data-documents-table] [data-document-row] a').first();
    await expect(link, 'у переліку стенда немає документа').toBeVisible({ timeout: 30_000 });
    await link.click();

    const strips = page.getByTestId('document-calc-strip');
    await expect(strips, 'смуги проблем розрахунку немає').toHaveCount(1, { timeout: 60_000 });
    // Колишніх панелей місяців над сіткою немає — лише смуга.
    await expect(page.getByTestId('document-calc-issues')).toHaveCount(0);

    const line = page.locator('.ecr-calc-strip-line');
    const box = await line.boundingBox();
    expect(box?.height ?? 999, 'смуга вища за один рядок').toBeLessThanOrEqual(40);

    // AC-4: смуга — region з назвою (текст назви — з каталогу стенда, не ключ).
    const name = (await strips.getAttribute('aria-label')) ?? '';
    expect(name, 'смуга без назви').not.toBe('');
    expect(name).not.toContain('document.calcIssues');
    await expect(page.getByRole('region', { name })).toHaveCount(1);
    const toggle = page.getByTestId('document-calc-strip-toggle');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');

    // AC-0: перший рядок сітки — у вікні 1440×900 без прокрутки.
    const firstRow = page.locator('revo-grid .rgRow').first();
    await expect(firstRow, 'сітки немає').toBeVisible({ timeout: 60_000 });
    const rowBox = await firstRow.boundingBox();
    expect(rowBox, 'рядок сітки не виміряно').not.toBeNull();
    expect((rowBox?.y ?? 9999) + (rowBox?.height ?? 0), 'перший рядок сітки нижче першого екрана').toBeLessThanOrEqual(900);
    await shot(page, 'docNoticesCompact-collapsed-1440');
    expect(await axe(page), 'axe light').toEqual([]);

    // Details — матриця. ✎ DOCUX L1 (принцип 8 плану): розгорнутість НЕ переживає перезавантаження й не переходить на
    // інші документи (доти кожен документ відкривався з розгорнутою матрицею — пів екрана).
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('document-calc-strip-matrix')).toBeVisible();
    await expect(page.locator('[data-testid="document-calc-strip-matrix"] tr[data-kind="missingInputs"] td').first()).toHaveAttribute('data-count', '2');
    await shot(page, 'docNoticesCompact-expanded-1440');
    await page.reload();
    await expect(page.getByTestId('document-calc-strip-toggle')).toHaveAttribute('aria-expanded', 'false', { timeout: 60_000 });

    // Темна тема (AC-4) — і повернути світлу: оператор спільний для всіх специфікацій.
    try {
      await preference(page, 'dark');
      await page.reload();
      await expect(page.getByTestId('document-calc-strip')).toBeVisible({ timeout: 60_000 });
      await shot(page, 'docNoticesCompact-expanded-dark-1440');
      expect(await axe(page), 'axe dark').toEqual([]);
    } finally {
      await preference(page, 'light');
    }
  });
});

async function axe(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(async () => {
    const run = (window as unknown as {
      axe: { run: (c: Element, o: object) => Promise<{ violations: { id: string; nodes: { target: unknown[] }[] }[] }> };
    }).axe;
    const scope = document.querySelector('[data-testid="document-calc-strip"]') ?? document.body;
    const result = await run.run(scope, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } });

    return result.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${String(n.target.join(' '))}`));
  });
}

async function preference(page: Page, value: 'light' | 'dark'): Promise<void> {
  const response = await page.request.put('/api/v1/me/preferences/theme', {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify(value),
  });
  expect(response.ok(), `тема ${value}: ${String(response.status())}`).toBe(true);
}

async function shot(page: Page, name: string): Promise<void> {
  // Знімок — вкладення звіту Playwright (артефакт CI e2e-stand) і, з ECR_E2E_REPORT_DIR, файл.
  await test.info().attach(name, { body: await page.screenshot(), contentType: 'image/png' });
  if (ReportDir === '') return;
  mkdirSync(ReportDir, { recursive: true });
  await page.screenshot({ path: `${ReportDir}\\${name}.png` });
}

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
