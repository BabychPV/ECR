import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * Беклог P3 пермітів на живому стенді: PUI-4 (Remove значення, яке вже видалили деінде), PUI-5 (End early у день старту -
 * причина людськими датами В діалозі, діалог не закривається), PUI-10 (ru: «г..» після дати в діалозі Activate).
 *
 * Дані: типи/дозволи заводяться через API (`e2e-admin`) з префіксом `E2E_`; чинний дозвіл сервер не віддає на видалення
 * (`ECR-PRM-4096`), тож він лишається на одноразовому стенді. Мова ru вмикається налаштуванням профілю й знімається в `finally`.
 *
 * Запуск (стенд - `tools/e2e-stand.ps1`): `npx playwright test -g "Permits P3 backlog"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

function isoDate(date: Date): string {
  const pad = (value: number): string => String(value).padStart(2, '0');

  return `${String(date.getFullYear())}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

async function body<T>(response: APIResponse, what: string): Promise<T> {
  expect(response.status(), `${what}: ${await response.text()}`).toBeLessThan(300);

  return (await response.json()) as T;
}

const column = (code: string, over: Record<string, unknown>): Record<string, unknown> => ({
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
  order: 0,
  ...over,
});

/** Заводить і ПУБЛІКУЄ тип з таблицею `Items`: ключ `Name` і (за `method`) список «лише тип» Alpha/Beta. */
async function publishType(page: Page, tracked: CreatedPermitTypes, name: string, method?: string): Promise<string> {
  const code = tracked.track(`E2E_P3B_${String(Date.now())}${String(Math.floor(Math.random() * 1000))}`);
  const made = await body<{ rowVersion: string }>(
    await page.request.post('/api/v1/permit-types', { data: { code, category: 'Other', nameL10n: { en: name } } }),
    'створення типу',
  );
  const columns = [
    column('Name', { key: true, required: true, order: 1 }),
    ...(method === undefined
      ? []
      : [column(method, { dataType: 'Lookup', listScope: 'TypeOnly', choices: ['Alpha', 'Beta'], order: 2 })]),
  ];
  const saved = await body<{ rowVersion: string }>(
    await page.request.put(`/api/v1/permit-types/${code}`, {
      data: {
        category: 'Other',
        nameL10n: { en: name },
        rowVersion: made.rowVersion,
        rules: [],
        tables: [{ code: 'Items', nameL10n: { en: 'Items' }, order: 1, rules: [], columns }],
      },
    }),
    'заміна чернетки типу',
  );
  await body(
    await page.request.post(`/api/v1/permit-types/${code}/publish`, {
      data: { reason: 'E2E: P3 backlog permit type', rowVersion: saved.rowVersion },
    }),
    'публікація типу',
  );

  return code;
}

async function createPermit(page: Page, typeCode: string, number: string, validFrom: Date, validTo: Date): Promise<number> {
  const permit = await body<{ id: number }>(
    await page.request.post('/api/v1/permits', {
      data: {
        typeCode,
        number,
        location: 'E2E P3 backlog site',
        issueDate: null,
        validFrom: isoDate(validFrom),
        validTo: isoDate(validTo),
        reportingFlags: null,
      },
    }),
    'створення дозволу',
  );

  return permit.id;
}

test.describe('Permits P3 backlog', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  const types = new CreatedPermitTypes();
  const drafts: number[] = [];

  test.afterEach(async ({ page }) => {
    for (const id of drafts.splice(0)) {
      const removed = await page.request.delete(`/api/v1/permits/${String(id)}`);
      expect([204, 404, 409], `прибирання дозволу ${String(id)}: ${removed.status()}`).toContain(removed.status());
    }
    await types.cleanup(page);
  });

  test('PUI-5: End early у день старту - причина людськими датами в діалозі, він не закривається', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const typeCode = await publishType(page, types, `E2E_ p3b end early ${String(Date.now())}`);
    const today = new Date();
    const nextYear = new Date(today.getFullYear() + 1, today.getMonth(), today.getDate());
    const id = await createPermit(page, typeCode, `E2E-P3B5-${String(Date.now())}`, today, nextYear);
    drafts.push(id);
    await body(await page.request.post(`/api/v1/permits/${String(id)}/activate`), 'активація');

    await page.goto(`/admin/permits/${String(id)}?dialog=end`);
    const dialog = page.getByRole('dialog', { name: /End permit .* early/ });
    await expect(dialog).toBeVisible({ timeout: 30_000 });
    const reason = dialog.getByRole('textbox', { name: /Reason/ });

    // KI-04: поріг довжини причини видно людині; N/5 перемикань 9 -> 10 символів (підказка в описі поля, кнопка слідом).
    // Саме ВІДОБРАЖЕНИЙ опис поля: після переходу 10 -> 9 той самий текст з'являється й у видимо-схованому живому регіоні (a11y).
    const hint = dialog.getByTestId('end-reason-hint');
    const confirm = dialog.getByRole('button', { name: 'End permit' });
    for (let run = 1; run <= 5; run += 1) {
      await reason.fill('123456789');
      await expect(hint, `прогін ${String(run)}: підказка при 9 символах`).toBeVisible();
      await expect(confirm).toBeDisabled();
      await reason.fill('1234567890');
      await expect(hint, `прогін ${String(run)}: підказка зникла при 10`).toBeHidden();
      await expect(confirm).toBeEnabled();
    }
    await expect(reason).toHaveAttribute('aria-describedby', /-description/);

    await reason.fill('UI pass P3 ended early');
    await confirm.click();

    // Причина - у діалозі, людськими датами (не сирі ISO-дати сервера); діалог не закрився, причина введена не втрачена.
    await expect(dialog.getByText(/must be after the start/)).toBeVisible({ timeout: 15_000 });
    expect(await dialog.innerText()).not.toMatch(/\d{4}-\d{2}-\d{2}/);
    await expect(dialog).toBeVisible();
    await expect(reason).toHaveValue('UI pass P3 ended early');
    await expect(reason).toHaveAttribute('aria-invalid', 'true');

    await reason.fill('UI pass P3 ended early!');
    await expect(dialog.getByText(/must be after the start/)).toBeHidden({ timeout: 10_000 });

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden({ timeout: 15_000 });
  });

  test('PUI-10: ru - діалог Activate без подвійної крапки після дати', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const typeCode = await publishType(page, types, `E2E_ p3b activate ${String(Date.now())}`);
    const from = new Date(new Date().getFullYear() + 1, 0, 10);
    const to = new Date(new Date().getFullYear() + 2, 0, 10);
    const id = await createPermit(page, typeCode, `E2E-P3B10-${String(Date.now())}`, from, to);
    drafts.push(id);

    const setLanguage = async (value: string | null): Promise<void> => {
      const response =
        value === null
          ? await page.request.delete('/api/v1/me/preferences/language')
          : await page.request.put('/api/v1/me/preferences/language', {
              data: JSON.stringify(value),
              headers: { 'Content-Type': 'application/json' },
            });
      expect([200, 204, 404], `мова профілю: ${response.status()}`).toContain(response.status());
    };

    await setLanguage('ru');
    try {
      await page.goto(`/admin/permits/${String(id)}?dialog=activate`);
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible({ timeout: 30_000 });
      await expect(dialog).toContainText(/Листы, привязанные к типу/, { timeout: 15_000 });
      const text = await dialog.innerText();
      expect(text, text).not.toContain('г..');
      expect(text, text).toMatch(/по [^\n]*\d{4} г\./);
    } finally {
      await setLanguage(null);
    }
  });

  test('PUI-4: Remove значення, яке вже видалили деінде - людське повідомлення й рядок зникає', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const stamp = String(Date.now());
    const method = `E2E_P3BMethod${stamp}`;
    const code = await publishType(page, types, `E2E_ p3b lists ${stamp}`, method);
    const key = `type:${code}:Items:${method}`;

    await page.goto('/admin/permits/lists');
    const row = page.locator('[data-list-table] table tbody tr', { hasText: method });
    await expect(row).toBeVisible({ timeout: 30_000 });
    await row.click();
    const drawer = page.getByRole('dialog');
    await expect(drawer).toContainText('Beta', { timeout: 15_000 });

    // «Друга вкладка»: Beta видалена через API, шторка цього екрана про це не знає.
    const detail = await body<{ items: { id: number; displayL10n: Record<string, string> }[] }>(
      await page.request.get(`/api/v1/permit-lists/${encodeURIComponent(key)}`),
      'читання списку',
    );
    const beta = detail.items.find((item) => item.displayL10n['en'] === 'Beta');
    expect(beta, 'Beta є в списку').toBeDefined();
    await body(
      await page.request.put(`/api/v1/permit-lists/${encodeURIComponent(key)}/items`, {
        data: { changes: [{ action: 'Remove', id: beta?.id ?? 0, displayL10n: null }] },
      }),
      'видалення Beta деінде',
    );

    await drawer.getByRole('button', { name: 'Remove Beta' }).click();

    await expect(page.getByText(/no longer in the list/).first()).toBeVisible({ timeout: 15_000 });
    await expect(drawer.getByRole('button', { name: 'Remove Beta' })).toHaveCount(0, { timeout: 15_000 });
    await expect(drawer).toContainText('Alpha');
    await expect(page.getByText(/was not found/)).toHaveCount(0);
  });
});
