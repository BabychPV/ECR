import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * PM-07: перелік дозволів `/admin/permits`, майстер «New permit» і дії життєвого циклу з меню рядка - на
 * живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжній маршрут з правом `Registry.EditData`, справжні `POST /api/v1/permits`,
 * `DELETE`, `POST …/activate`, `POST …/end`, фільтр стану в адресі й поява/зникнення рядка в переліку.
 *
 * Дані: тип дозволу з таблицею заводиться й публікується через API (`e2e-admin`), бо майстер пропонує лише
 * опубліковані типи. ⚠ Опублікований тип і закінчений дозвіл сервер не віддає на видалення (`ECR-PRM-4094`,
 * `ECR-PRM-4096`), тож вони лишаються на одноразовому стенді з префіксом `E2E_`; чернетки прибираються в `afterEach`.
 *
 * Запуск (стенд - `tools/e2e-stand.ps1`; на CI - workflow `e2e-stand`): `npx playwright test -g "Permits list"`.
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

function column(code: string, order: number, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    code,
    nameL10n: { en: code },
    dataType: 'String',
    unitId: null,
    required: false,
    key: false,
    group: null,
    precision: null,
    scale: null,
    listScope: null,
    listRegistryCode: null,
    siblingTableCode: null,
    choices: null,
    order,
    ...over,
  };
}

function isoDate(date: Date): string {
  const pad = (value: number): string => String(value).padStart(2, '0');

  return `${String(date.getFullYear())}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

async function body<T>(response: APIResponse, what: string): Promise<T> {
  expect(response.status(), `${what}: ${await response.text()}`).toBeLessThan(300);

  return (await response.json()) as T;
}

/** Заводить тип з однією таблицею й публікує його: майстер «New permit» пропонує лише опубліковані. */
async function publishedType(page: Page, tracked: CreatedPermitTypes, name: string): Promise<string> {
  const code = tracked.track(`E2E_PERMITS_${String(Date.now())}`);
  const fresh = await body<{ rowVersion: string }>(
    await page.request.post('/api/v1/permit-types', { data: { code, category: 'Air', nameL10n: { en: name } } }),
    'створення типу',
  );
  const replaced = await body<{ rowVersion: string }>(
    await page.request.put(`/api/v1/permit-types/${code}`, {
      data: {
        category: 'Air',
        nameL10n: { en: name },
        rowVersion: fresh.rowVersion,
        rules: [],
        tables: [
          {
            code: 'Points',
            nameL10n: { en: 'Points' },
            order: 1,
            columns: [column('Name', 1, { key: true, required: true }), column('Note', 2)],
            rules: [],
          },
        ],
      },
    }),
    'заміна чернетки типу',
  );
  await body(
    await page.request.post(`/api/v1/permit-types/${code}/publish`, {
      data: { reason: 'E2E: first version of the permits list type', rowVersion: replaced.rowVersion },
    }),
    'публікація типу',
  );

  return code;
}

test.describe('Permits list', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  const types = new CreatedPermitTypes();
  const drafts: number[] = [];

  test.afterEach(async ({ page }) => {
    // Чернетки видаляються; чинний чи закінчений дозвіл сервер не віддасть (409) - це не помилка прибирання.
    for (const id of drafts.splice(0)) {
      const removed = await page.request.delete(`/api/v1/permits/${String(id)}`);
      expect([204, 404, 409], `прибирання дозволу ${String(id)}: ${removed.status()}`).toContain(removed.status());
    }
    await types.cleanup(page);
  });

  const row = (page: Page, number: string) =>
    page.locator('[data-list-table] table tbody tr', { hasText: number });

  test('майстер створює чернетку, вона видна в переліку й видаляється з меню рядка', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const stamp = String(Date.now());
    const typeName = `E2E_ air permit ${stamp}`;
    await publishedType(page, types, typeName);
    const number = `E2E-${stamp}`;

    await page.goto('/admin/permits');
    await expect(page.getByRole('heading', { level: 1, name: 'Permits' })).toBeVisible({ timeout: 30_000 });

    await page.getByRole('button', { name: /^New permit$/ }).first().click();
    const dialog = page.getByRole('dialog', { name: /^New permit/ });
    await expect(dialog).toBeVisible({ timeout: 10_000 });

    // Крок «Type»: наш опублікований тип вибираємо за назвою.
    await dialog.getByRole('radio', { name: new RegExp(typeName) }).check();
    await dialog.getByTestId('wizard-next').click();

    // Крок «Details»: без номера далі не пускає.
    await dialog.getByTestId('wizard-next').click();
    await expect(dialog.getByTestId('wizard-step-error')).toBeVisible();

    await dialog.getByRole('textbox', { name: /^Permit number/ }).fill(number);
    await dialog.getByRole('textbox', { name: /^Location/ }).fill('E2E site');
    await dialog.getByTestId('wizard-next').click();
    await expect(dialog.getByTestId('wizard-summary')).toContainText(number);

    const created = page.waitForResponse(
      (response) => response.request().method() === 'POST' && response.url().endsWith('/api/v1/permits'),
    );
    await dialog.getByTestId('wizard-apply').click();
    const createdResponse = await created;
    expect(createdResponse.status(), `POST /api/v1/permits: ${await createdResponse.text()}`).toBe(201);
    const permit = (await createdResponse.json()) as { id: number; status: string };
    drafts.push(permit.id);
    expect(permit.status).toBe('Draft');
    await expect(page).toHaveURL(new RegExp(`/admin/permits/${String(permit.id)}$`), { timeout: 30_000 });

    // Перелік: чернетка з номером, станом Draft і розташуванням; пошук знаходить її.
    await page.goto(`/admin/permits?q=${encodeURIComponent(number)}`);
    const line = row(page, number);
    await expect(line).toBeVisible({ timeout: 30_000 });
    await expect(line).toContainText('E2E site');
    await expect(line).toContainText('Draft');

    // Видалення чернетки з меню рядка.
    await line.getByRole('button', { name: new RegExp(`Actions for permit ${number}`) }).click();
    await page.getByRole('menuitem', { name: /^Delete draft/ }).click();
    const confirm = page.getByRole('dialog', { name: new RegExp(`Delete draft ${number}`) });
    await expect(confirm).toBeVisible();
    await confirm.getByRole('button', { name: 'Delete draft' }).click();
    await expect(line).toHaveCount(0, { timeout: 30_000 });
    expect((await page.request.get(`/api/v1/permits/${String(permit.id)}`)).status()).toBe(404);
  });

  test('дозвіл із API: активація й дострокове завершення з меню, фільтр стану в адресі', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const stamp = String(Date.now());
    const typeCode = await publishedType(page, types, `E2E_ lifecycle permit ${stamp}`);
    const number = `E2E-LC-${stamp}`;
    const today = new Date();
    const nextYear = new Date(today.getFullYear() + 1, today.getMonth(), today.getDate());
    // ⛔ Початок - у минулому: «End early» без дати завершує сьогодні, а сервер вимагає дату строго ПІСЛЯ початку
    // (`422 ECR-PRM-0422 endDateOutOfRange`) - дозвіл, що стартує сьогодні, достроково завершити не можна (стенд 19.10).
    const monthAgo = new Date(today.getFullYear(), today.getMonth() - 1, today.getDate());
    const permit = await body<{ id: number; status: string }>(
      await page.request.post('/api/v1/permits', {
        data: {
          typeCode,
          number,
          location: 'E2E lifecycle site',
          issueDate: null,
          validFrom: isoDate(monthAgo),
          validTo: isoDate(nextYear),
          reportingFlags: null,
        },
      }),
      'створення дозволу',
    );
    drafts.push(permit.id);
    expect(permit.status).toBe('Draft');

    // Фільтр стану в адресі: чернетка видна, у «Active» її ще немає.
    await page.goto(`/admin/permits?state=Active&q=${encodeURIComponent(number)}`);
    await expect(page.getByRole('heading', { level: 1, name: 'Permits' })).toBeVisible({ timeout: 30_000 });
    await expect(row(page, number)).toHaveCount(0);

    await page.goto(`/admin/permits?state=Draft&q=${encodeURIComponent(number)}`);
    const line = row(page, number);
    await expect(line).toBeVisible({ timeout: 30_000 });

    // Активація: діалог з наслідками, після підтвердження стан Active.
    await line.getByRole('button', { name: new RegExp(`Actions for permit ${number}`) }).click();
    await page.getByRole('menuitem', { name: /^Activate/ }).click();
    const activate = page.getByRole('dialog', { name: new RegExp(`Activate permit ${number}`) });
    await expect(activate).toBeVisible();
    await activate.getByRole('button', { name: 'Activate' }).click();
    await expect(activate).toBeHidden({ timeout: 30_000 });
    await expect
      .poll(async () => ((await (await page.request.get(`/api/v1/permits/${String(permit.id)}`)).json()) as { status: string }).status, {
        message: 'дозвіл не став Active',
        timeout: 30_000,
      })
      .toBe('Active');

    // Дострокове завершення: коротка причина не пускає, довга - пускає.
    await page.goto(`/admin/permits?state=Active&q=${encodeURIComponent(number)}`);
    const active = row(page, number);
    await expect(active).toBeVisible({ timeout: 30_000 });
    await expect(active).toContainText('Active');
    await active.getByRole('button', { name: new RegExp(`Actions for permit ${number}`) }).click();
    await page.getByRole('menuitem', { name: /^End early/ }).click();
    const end = page.getByRole('dialog', { name: new RegExp(`End permit ${number} early`) });
    await expect(end).toBeVisible();
    await end.getByLabel('Reason').fill('too short');
    await expect(end.getByRole('button', { name: 'End permit' })).toBeDisabled();
    await end.getByLabel('Reason').fill('E2E: permit surrendered by the operator');
    await end.getByRole('button', { name: 'End permit' }).click();
    await expect(end).toBeHidden({ timeout: 30_000 });
    await expect
      .poll(async () => ((await (await page.request.get(`/api/v1/permits/${String(permit.id)}`)).json()) as { status: string }).status, {
        message: 'дозвіл не став Expired',
        timeout: 30_000,
      })
      .toBe('Expired');

    // Закінчений лише поновлюють: у меню немає ні «End early…», ні «Activate…».
    await page.goto(`/admin/permits?state=Expired&q=${encodeURIComponent(number)}`);
    const expired = row(page, number);
    await expect(expired).toBeVisible({ timeout: 30_000 });
    await expired.getByRole('button', { name: new RegExp(`Actions for permit ${number}`) }).click();
    await expect(page.getByRole('menuitem')).toHaveText(['Renew…']);
  });
});
