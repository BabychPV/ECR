import { expect, type APIResponse, type Browser, type Page } from '@playwright/test';

/**
 * Фікстура e2e «колір рядка / колір тексту» (ui-rowcolor): документ із широкою (15 колонок) таблицею з багатьма
 * фіксованими рядками, як «3.00 Stationary Equipment» у Land. Усе через API, як `multiSheet.ts`; код проєкту має
 * префікс `E2EGroupsPrj`-сімейства (`E2ERowColorPrj`), який фільтрують інші специфікації (periodsOverview).
 */
export const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

export const RowColorColumns = 15;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

export interface RowColorDocument {
  readonly documentId: number;
  readonly periodKey: number;
  readonly rowKeys: readonly string[];
}

export async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Контекст браузера з обраною темою (вибір — у сховищі ДО входу і ще раз через меню користувача, як `cellColorGridLive`). */
export async function openAs(browser: Browser, scheme: 'light' | 'dark'): Promise<Page> {
  const context = await browser.newContext({ viewport: { width: 1600, height: 1400 } });
  const page = await context.newPage();

  await page.emulateMedia({ colorScheme: scheme });
  await page.goto('/login');
  await page.evaluate((value: string) => {
    localStorage.setItem('mantine-color-scheme-value', value);
  }, scheme);
  await signIn(page);
  await ensureScheme(page, scheme);
  await page.reload();
  await ensureScheme(page, scheme);

  return page;
}

async function ensureScheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  const html = page.locator('html');
  const user = page.getByRole('button', { name: new RegExp(Operator.user, 'i') }).first();

  await expect(user).toBeVisible({ timeout: 30_000 });

  if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
    await user.click();
    await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
    await page.keyboard.press('Escape');
  }

  await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

function column(ordinal: number, header: string): Record<string, unknown> {
  return {
    headerL10n: { en: header },
    ordinal,
    dataType: 'Decimal',
    isRequired: false,
    isReadOnly: false,
    isHidden: false,
    precision: 18,
    scale: 4,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId: null,
  };
}

export async function createRowColorDocument(page: Page, rowCount: number): Promise<RowColorDocument> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2ERowColor${stamp}`, nameL10n: { en: `E2E row colour ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'версія шаблону',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;

  await ok(
    await page.request.put(`${base}/sheets/S1`, {
      data: { nameL10n: { en: 'Sheet 1' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
    }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: '3.00 Stationary Equipment' }, ordinal: 0, layoutKind: 'PerPeriodInstance', rowMode: 'Fixed', maxDynamicRows: null },
    }),
    'таблиця',
  );

  for (let c = 1; c <= RowColorColumns; c += 1) {
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/columns/C${String(c)}`, { data: column(c - 1, `C${String(c)}`) }),
      `колонка C${String(c)}`,
    );
  }

  const rowKeys: string[] = [];
  for (let r = 1; r <= rowCount; r += 1) {
    const rowKey = `R${String(r)}`;
    rowKeys.push(rowKey);
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/rows/${rowKey}`, {
        data: { labelL10n: { en: `Unit ${String(r)}` }, ordinal: r, rowKind: 'Item', parentRowKey: null, isReadOnly: false },
      }),
      `рядок ${rowKey}`,
    );
  }

  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: колір рядка' } }), 'публікація версії');

  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2ERowColorPrj${stamp}`,
        nameL10n: { en: `E2E row colour project ${stamp}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: version.versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await page.request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');
  const structure = await ok<{ sheets: { id: number }[] }>(await page.request.get(`${base}/structure`), 'структура версії');
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар проєкту',
  );
  const period = calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0];
  expect(period, 'у проєкті немає періодів').toBeDefined();
  const periodKey = period?.periodKey ?? 0;
  
  return { documentId: document.documentId, periodKey, rowKeys };
}
