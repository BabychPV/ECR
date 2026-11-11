import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * ✎ D-474, п. 2 на живому стенді: чернетка версії шаблону → «Layout» = «Year, months in columns» → після
 * перезавантаження сторінки поле показує збережене значення (`PUT …/layout`, `TemplateStructureDto.layout`).
 *
 * Фікстура — через API, як `columnsBulkGroup.spec.ts`: новий шаблон і порожня чернетка (документів немає —
 * інакше сервер відмовив би `409 ECR-SCHM-0409`). Після публікації поле стає значком лише для читання.
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);
  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function arrange(page: Page): Promise<{ templateId: number; versionId: number }> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2ELayout${stamp}`, nameL10n: { en: `E2E layout ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'версія',
  );

  return { templateId: template.templateId, versionId: version.versionId };
}

test.describe('Розкладка версії шаблону (D-474)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('чернетка: Layout → «Year, months in columns» зберігається й видно після перезавантаження', async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page);
    const fx = await arrange(page);

    await page.goto(`/admin/templates/${String(fx.templateId)}/versions/${String(fx.versionId)}`);
    const layout = page.getByRole('textbox', { name: 'Layout' });
    await expect(layout).toHaveValue('A copy per month', { timeout: 60_000 });

    await layout.click();
    await page.getByRole('option', { name: 'Year, months in columns', exact: true }).click();
    await expect(page.getByText('Layout saved')).toBeVisible({ timeout: 30_000 });

    await page.reload();
    await expect(page.getByRole('textbox', { name: 'Layout' })).toHaveValue('Year, months in columns', { timeout: 60_000 });

    // Сервер теж бачить саме це значення.
    const structure = await ok<{ layout: string }>(
      await page.request.get(`/api/v1/template-versions/${String(fx.versionId)}/structure`),
      'структура',
    );
    expect(structure.layout).toBe('YearWithMonthColumns');
  });
});
