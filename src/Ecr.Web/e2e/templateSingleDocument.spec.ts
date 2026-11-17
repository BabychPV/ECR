import { expect, test, type APIResponse } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * B151 на живому стенді: картка шаблону → «More» → «Allow one document per project» → рядок «Documents per
 * project» = «One», тост збереження; після перезавантаження значення те саме, і сервер віддає прапорець у картці.
 * Зворотна дія повертає «Several». Відмову створення другого документа (409) і відмову ввімкнення з дублікатами
 * тримають HTTP/Infra-тести (`TemplateCardTests`, `SingleDocumentPerProjectTests`).
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);
  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

test.describe('Шаблон «один документ на проєкт» (B151)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('картка шаблону: дозволити один документ на проєкт і назад', async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page);
    const stamp = String(Date.now());
    const template = await ok<{ templateId: number }>(
      await page.request.post('/api/v1/templates', { data: { code: `E2ESingle${stamp}`, nameL10n: { en: `E2E single ${stamp}` } } }),
      'шаблон',
    );

    await page.goto(`/admin/templates/${String(template.templateId)}`);
    await expect(page.getByText('Documents per project')).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText('Several', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'More' }).click();
    await page.getByRole('menuitem', { name: 'Allow one document per project' }).click();
    await expect(page.getByText('Template document rule saved')).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText('One', { exact: true })).toBeVisible();

    await page.reload();
    await expect(page.getByText('One', { exact: true })).toBeVisible({ timeout: 60_000 });
    const card = await ok<{ singleDocumentPerProject: boolean }>(
      await page.request.get(`/api/v1/templates/${String(template.templateId)}`),
      'картка',
    );
    expect(card.singleDocumentPerProject).toBe(true);

    await page.getByRole('button', { name: 'More' }).click();
    await page.getByRole('menuitem', { name: 'Allow several documents per project' }).click();
    await expect(page.getByText('Several', { exact: true })).toBeVisible({ timeout: 30_000 });
  });
});
