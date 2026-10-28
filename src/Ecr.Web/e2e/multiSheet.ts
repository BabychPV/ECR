import { expect, type APIResponse, type Page } from '@playwright/test';

/**
 * Спільні фікстури для e2e «нижній рядок під таблицею» (ui-sheet-bottom): вхід оператора і документ із кількома
 * аркушами та ШИРОКОЮ таблицею на першому — заводяться через API (як `permitBindings.spec.ts`).
 */
export const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

export async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  // ⚠ Роль, а не підпис: підпис «Password» має й кнопка-тумблер видимості (`LoginPage.tsx`).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(ordinal: number, header: string): Record<string, unknown> {
  return {
    headerL10n: { en: header },
    ordinal,
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
  };
}

export interface MultiSheetDocument {
  readonly documentId: number;
  readonly periodKey: number;
}

/** Документ із `sheetCount` аркушами (довгі назви — вкладки не вміщуються); перший аркуш має `wideColumns` стовпців. */
export async function createMultiSheetDocument(
  page: Page,
  sheetCount: number,
  wideColumns: number,
): Promise<MultiSheetDocument> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2EBottom${stamp}`, nameL10n: { en: `E2E bottom ${stamp}` } } }),
    'заведення шаблону',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'заведення версії шаблону',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;

  for (let n = 1; n <= sheetCount; n += 1) {
    const code = `S${String(n)}`;
    await ok(
      await page.request.put(`${base}/sheets/${code}`, {
        data: { nameL10n: { en: `Sheet ${String(n)} with a long descriptive name` }, ordinal: n, isMandatory: true, isVisible: true, sheetGroup: null },
      }),
      `аркуш ${code}`,
    );
    const table = await ok<{ id: number }>(
      await page.request.put(`${base}/sheets/${code}/tables/T${String(n)}`, {
        data: { nameL10n: { en: `Table ${String(n)}` }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
      }),
      `таблиця аркуша ${code}`,
    );
    const columns = n === 1 ? wideColumns : 1;
    for (let c = 1; c <= columns; c += 1) {
      await ok(
        await page.request.put(`${base}/tables/${String(table.id)}/columns/C${String(c)}`, { data: column(c - 1, `C${String(c)}`) }),
        `стовпець C${String(c)} аркуша ${code}`,
      );
    }
  }

  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: документ із кількома аркушами і широкою таблицею' } }), 'публікація версії');
  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2EBottomPrj${stamp}`,
        nameL10n: { en: `E2E bottom project ${stamp}` },
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
  expect(period, 'у проєкту немає періодів').toBeDefined();

  return { documentId: document.documentId, periodKey: period?.periodKey ?? 0 };
}
