import { expect, test, type Page } from '@playwright/test';

/**
 * `B179 L5`: екран «Граф залежностей» (`/admin/dependency-graph`) на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжній `GET /api/v1/dependency-graph` і `GET /document-source-bindings` (право
 * `Calculation.ManageSourceBinding` у ролі `E2EAdmin`, `tools/e2e-stand.ps1`) стають рівнями з шаблонами стенда;
 * пункт меню є в групі «Configure»; шаблон-джерело HSE400 (якщо його заведено на стенді) має перемикачі зв'язки, а
 * відхилення підтвердження нічого не змінює.
 *
 * ⛔ Перемикачі в e2e НЕ натискаються до кінця: підтвердження скасовується. Справжня зміна ставить перерахунок залежних
 * документів стенда і пише в журнал безпеки — це доводять `DependencyGraphEndpointTests` (HTTP, реальний SQL).
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test dependencyGraph.spec.ts`. ⚠ Файл написано, але в цій
 * гілці НЕ запускався (координатор проганяє пачкою).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

test.describe('Граф залежностей: рівні, докази, перемикачі зв’язки (B179 L5)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('екран відкривається з меню, показує рівні й відповідь сервера; підтвердження скасовується без змін', async ({
    page,
  }) => {
    test.setTimeout(120_000);
    await signIn(page);

    const graphResponse = page.waitForResponse(
      (r) => r.url().includes('/api/v1/dependency-graph') && r.request().method() === 'GET',
    );
    await page.goto('/admin/dependency-graph');
    const graph = await graphResponse;
    expect(graph.status(), 'GET /api/v1/dependency-graph').toBe(200);
    const body = (await graph.json()) as {
      revision: string;
      levels: { level: number; templateIds: number[] }[];
      templateDependencies: { bindingId: number }[];
    };
    expect(body.revision.length).toBeGreaterThan(0);

    // Заголовок екрана — h1; кожен рівень — h2 (порядок заголовків без пропусків).
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 30_000 });
    if (body.levels.length === 0) {
      // Порожній граф — пояснення, а не порожнеча.
      await expect(page.getByRole('heading', { level: 2 })).toBeVisible();

      return;
    }

    await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible();
    expect(await page.getByRole('heading', { level: 2 }).count()).toBeGreaterThanOrEqual(body.levels.length);

    // Перемикачі зв'язки є рівно тоді, коли сервер віддав хоч одну зв'язку джерела.
    const switches = page.getByRole('switch');
    if (body.templateDependencies.length === 0) {
      await expect(switches).toHaveCount(0);

      return;
    }

    await expect(switches.first()).toBeVisible();
    const before = await switches.first().isChecked();

    // Клік → діалог підтвердження з наслідком; «Скасувати» → стан не змінився, жодного PUT.
    const puts: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'PUT' && request.url().includes('/api/v1/document-source-bindings/')) {
        puts.push(request.url());
      }
    });
    await switches.first().click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('button').last()).toBeVisible();
    await dialog.getByRole('button', { name: /Cancel|Скасувати|Отмена|Бас тарту/ }).click();
    await expect(dialog).toBeHidden();
    await expect(switches.first()).toBeChecked({ checked: before });
    expect(puts, 'скасування підтвердження не має надсилати PUT').toHaveLength(0);
  });

  test('меню «Configure» має пункт «Граф залежностей», що веде на екран', async ({ page }) => {
    test.setTimeout(90_000);
    await signIn(page);
    await page.goto('/');

    const link = page.getByRole('link', { name: /Dependency graph|Граф зависимостей|Граф залежностей|Тәуелділіктер графы/ });
    await expect(link).toBeVisible({ timeout: 30_000 });
    await link.click();
    await expect(page).toHaveURL(/\/admin\/dependency-graph$/);
  });
});
