import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';

/**
 * ✎ 25.11: видалення АКТИВНОГО проєкту з даними через інтерфейс (людина: видалити HSE401_2026, TEST_COORD_2026,
 * HSE400_GOLD). Проєкт-жертва створюється тут же: клон проєкту стенда → активація → документ; далі Lifecycle →
 * «Delete project with all data…» → перелік документа з ключем, код, причина → проєкту немає, документа немає (404).
 *
 * Що доводиться тут, а не у vitest/xUnit: справжній сервер із правом SystemAdministrator (`e2e-admin`), справжня
 * транзакція видалення, навігація після успіху і контраст діалогу в обох темах.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test projectPurge`.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  expect(response.ok(), `${what}: ${String(response.status())} ${await response.text()}`).toBe(true);
  const text = await response.text();
  return (text === '' ? null : JSON.parse(text)) as T;
}

/** Активний проєкт з одним документом — клон першого проєкту стенда. */
async function victim(page: Page, code: string): Promise<{ projectId: number; documentId: number }> {
  const list = await ok<{ items: { id: number; code: string; status: string }[] }>(
    await page.request.get('/api/v1/projects?limit=50'),
    'перелік проєктів',
  );
  const source = list.items.find((item) => item.status === 'Active') ?? list.items[0];
  expect(source, 'у стенда немає проєкту').toBeDefined();

  const clone = await ok<{ projectId: number }>(
    await page.request.post(`/api/v1/projects/${String(source?.id)}/clone`, { data: { code } }),
    'клон проєкту',
  );
  await ok(await page.request.post(`/api/v1/projects/${String(clone.projectId)}/activate`), 'активація');
  const template = await ok<{ templateVersionId: number; sheets: { id: number }[] }>(
    await page.request.get(`/api/v1/projects/${String(clone.projectId)}/document-template`),
    'шаблон документа',
  );
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: {
        projectId: clone.projectId,
        templateVersionId: template.templateVersionId,
        sheetDefIds: template.sheets.map((sheet) => sheet.id),
      },
    }),
    'документ',
  );

  return { projectId: clone.projectId, documentId: document.documentId };
}

test.describe('Видалення активного проєкту з даними (Lifecycle)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  for (const scheme of ['light', 'dark'] as const) {
    test(`активний проєкт із документом видаляється після коду й причини · ${scheme}`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await page.setViewportSize({ width: 1280, height: 900 });
      await signIn(page);

      const code = `E2EPURGE${scheme.toUpperCase()}${String(Date.now()).slice(-6)}`;
      const { projectId, documentId } = await victim(page, code);

      await page.goto(`/projects/${String(projectId)}/lifecycle`);
      await page.getByRole('button', { name: /Delete project with all data/i }).click();

      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await expect(dialog.locator('[data-purge-documents] li')).toHaveCount(1, { timeout: 30_000 });
      await expect(dialog.locator('[data-purge-irreversible]')).toBeVisible();

      const confirm = dialog.getByRole('button', { name: /Delete permanently/i });
      await expect(confirm).toBeDisabled();
      await dialog.getByRole('textbox', { name: /Reason/i }).fill('e2e: тестовий проєкт');
      await dialog.getByRole('textbox', { name: /Type the project code/i }).fill(code.toLowerCase());
      await expect(confirm).toBeDisabled();
      await dialog.getByRole('textbox', { name: /Type the project code/i }).fill(code);
      await expect(confirm).toBeEnabled();

      expect(await axeViolations(page, 'color-contrast', '[role="dialog"]')).toEqual([]);
      await page.screenshot({ path: `test-results/project-purge-dialog-${scheme}.png` });

      await confirm.click();
      await page.waitForURL((url) => url.pathname === '/projects', { timeout: 30_000 });

      const list = await ok<{ items: { id: number }[] }>(await page.request.get('/api/v1/projects?limit=100'), 'перелік');
      expect(list.items.some((item) => item.id === projectId), 'проєкт лишився').toBe(false);
      expect((await page.request.get(`/api/v1/documents/${String(documentId)}`)).status(), 'документ лишився').toBe(404);
    });
  }
});
