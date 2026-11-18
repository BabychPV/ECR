import { expect, test, type Browser, type BrowserContext, type Response } from '@playwright/test';

/**
 * ✎ B155 (варіант C) проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `-Grep approvalRoutesLive`): маршрут погодження ШАБЛОНУ в усіх проєктах.
 *
 *  - `e2e-admin` на картці шаблону документа стенда складає маршрут із двох ролей і зберігає: спершу
 *    `POST …/approval-route/preview`; є подані аркуші — діалог «почнуть з першого кроку» і `PUT` лише після
 *    підтвердження, немає — `PUT` одразу;
 *  - після перезавантаження сторінки маршрут — власний, два кроки;
 *  - налаштування проєкту → «Шаблони — погодження»: рядок шаблону з джерелом `template` (або `own`, якщо проєкт уже
 *    мав виняток для цього шаблону).
 *
 * ⛔ Без стенда файл ПАДАЄ (`beforeAll`), а не пропускається (honesty-guard, `globalSetup.ts`).
 *
 * ⚠ `afterAll` повертає маршрут шаблону, який був до сценарію (той самий `PUT`, порожній набір — «власного не було»).
 * ⚠ Припущення: `e2e-admin` має `Template.Edit` і `Project.Manage` + грант Manage на проєкт документа стенда.
 * ⚠ Написано без локального прогону (стенда в автора немає): перший живий прогін — на стенді.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

interface Step {
  ordinal: number;
  roleId: number;
}

interface ScopedRoute {
  hasRoute: boolean;
  steps: Step[];
  effectiveSource: string;
  canEdit: boolean;
}

interface TemplateRow {
  templateId: number;
  code: string;
  effectiveSource: string;
  workflowModeNone: boolean;
}

async function signedIn(browser: Browser): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: Admin.userName, password: Admin.password },
  });
  expect(login.ok(), `вхід ${Admin.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

async function getJson<T>(context: BrowserContext, path: string): Promise<T> {
  const response = await context.request.get(path);
  expect(response.ok(), `GET ${path}: ${String(response.status())} ${await response.text()}`).toBe(true);

  return (await response.json()) as T;
}

test.describe('approvalRoutes: маршрут шаблону на реальному стенді (B155, варіант C)', () => {
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  let templateId = 0;
  let originalRoleIds: number[] | null = null;

  test.afterAll(async ({ browser }) => {
    if (templateId === 0 || originalRoleIds === null) return;

    const admin = await signedIn(browser);
    try {
      const restore = await admin.request.put(`/api/v1/templates/${String(templateId)}/approval-route`, {
        data: { roleIds: originalRoleIds },
      });
      expect(restore.ok(), `повернення маршруту: ${String(restore.status())} ${await restore.text()}`).toBe(true);
    } finally {
      await admin.close();
    }
  });

  test('маршрут шаблону з двох ролей: preview → (підтвердження) → PUT; після перезавантаження — власний, два кроки; рядок у налаштуваннях проєкту', async ({
    browser,
  }) => {
    const admin = await signedIn(browser);

    try {
      // ── 0. Шаблон документа стенда і маршрут до сценарію ───────────────────────────
      const document = await getJson<{ projectId: number }>(admin, `/api/v1/documents/${DocumentId}?periodKey=${PeriodKey}`);
      const projectId = document.projectId;
      const rows = await getJson<TemplateRow[]>(admin, `/api/v1/projects/${String(projectId)}/template-routes`);
      const row = rows.find((candidate) => !candidate.workflowModeNone) ?? rows[0];
      if (row === undefined) throw new Error(`у проєкту ${String(projectId)} немає шаблонів`);
      templateId = row.templateId;

      const before = await getJson<ScopedRoute>(admin, `/api/v1/templates/${String(templateId)}/approval-route`);
      expect(before.canEdit, 'e2e-admin має право Template.Edit').toBe(true);
      originalRoleIds = before.hasRoute ? before.steps.map((step) => step.roleId) : [];

      const roles = await getJson<{ id: number; isGeneralApprover: boolean }[]>(admin, '/api/v1/roles/approvers');
      expect(roles.length, 'ролей для кроків щонайменше дві').toBeGreaterThanOrEqual(2);

      // ── 1. Картка шаблону → розділ «Погодження» → змінити маршрут ──────────────────
      const page = await admin.newPage();
      await page.goto(`/admin/templates/${String(templateId)}`);

      const section = page.getByTestId('approval-route-section-template');
      await expect(section).toBeVisible({ timeout: 60_000 });
      await section.getByRole('button', { name: /^(Edit route|Изменить маршрут|Маршрутты өзгерту)$/ }).click();

      const editor = section.getByTestId('approval-route-editor');
      await expect(editor).toBeVisible();

      // Власні кроки, якщо були, прибираються — далі рівно два нові.
      const removeStep = editor.getByRole('button', { name: /^(Remove step|Удалить шаг)|қадамды жою$/ });
      while ((await removeStep.count()) > 0) {
        await removeStep.first().click();
      }

      const addStep = editor.getByRole('textbox', { name: /^(Add a step|Добавить шаг|Қадам қосу)/ });
      for (const index of [0, 1]) {
        await addStep.click();
        await page.getByRole('option').nth(index).click();
      }
      await expect(editor.getByRole('listitem')).toHaveCount(2);

      // ── 2. Зберегти: preview → (діалог) → PUT ───────────────────────────────────────
      const routePath = `/api/v1/templates/${String(templateId)}/approval-route`;
      const isWrite = (method: string, path: string) => (response: Response) =>
        response.request().method() === method && new URL(response.url()).pathname === path;

      const previewDone = page.waitForResponse(isWrite('POST', `${routePath}/preview`), { timeout: 90_000 });
      const putDone = page.waitForResponse(isWrite('PUT', routePath), { timeout: 90_000 });
      await editor.getByTestId('approval-route-save').click();

      const preview = await previewDone;
      expect(preview.ok(), `preview: ${String(preview.status())}`).toBe(true);
      const impact = (await preview.json()) as { total: number; visible: number };

      if (impact.total > 0) {
        const dialog = page.getByRole('dialog');
        await expect(dialog).toBeVisible();
        await expect(dialog.getByTestId('confirm-consequences')).toBeVisible();
        await dialog.getByTestId('confirm-verb').click();
      } else {
        await expect(page.getByTestId('confirm-consequences')).toHaveCount(0);
      }

      const put = await putDone;
      expect(put.ok(), `PUT: ${String(put.status())} ${await put.text()}`).toBe(true);
      const change = (await put.json()) as { steps: number; restarted: { total: number } };
      expect(change.steps).toBe(2);
      expect(change.restarted.total).toBe(impact.total);

      // ── 3. Перезавантаження: маршрут власний, два кроки ─────────────────────────────
      await page.reload();
      const reloaded = page.getByTestId('approval-route-section-template');
      await expect(reloaded.getByTestId('scoped-approval-route')).toHaveAttribute('data-source', 'own', { timeout: 60_000 });
      await expect(reloaded.getByTestId('approval-route-steps').getByRole('listitem')).toHaveCount(2);

      // ── 4. Налаштування проєкту → «Шаблони — погодження» ────────────────────────────
      await page.goto(`/projects/${String(projectId)}/settings`);
      const templateRow = page.getByTestId('project-template-routes').locator(`[data-template-id="${String(templateId)}"]`);
      await expect(templateRow).toBeVisible({ timeout: 60_000 });
      await expect(templateRow).toHaveAttribute('data-source', row.effectiveSource === 'own' ? 'own' : 'template');
      await expect(templateRow.getByText(row.code, { exact: true })).toBeVisible();
    } finally {
      await admin.close();
    }
  });
});
