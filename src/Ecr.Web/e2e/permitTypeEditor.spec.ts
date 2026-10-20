import { expect, test, type Page } from '@playwright/test';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * Конструктор типу дозволу (Permits, PM-08b): на живому стенді.
 *
 * Що доводиться тут, а не у vitest: правка в інспекторі справді доїжджає на сервер відкладеним `PUT`
 * з токеном `rowVersion` («1 unsaved changes» → «Saved HH:MM»), а після перезавантаження сторінки
 * стовпець лишається з новою назвою. Лише e2e бачить, що контракт `PUT /api/v1/permit-types/{code}`
 * приймає саме те, що збирає клієнт.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test permitTypeEditor`. Локально агентом
 * PM-08b НЕ запускався (правило «Режим тестів»): прогін — на CI-стенді `e2e-stand`.
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

test.describe('Конструктор типу дозволу', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  const tracked = new CreatedPermitTypes();
  test.afterEach(async ({ page }) => {
    await tracked.cleanup(page);
  });

  test('правка стовпця в інспекторі зберігається і переживає перезавантаження', async ({ page }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    // Тип заводимо тим самим входом, що й людина (`page.request` ділить куки з вкладкою).
    const code = tracked.track(`E2E_PRM_${Date.now()}`);
    const created = await page.request.post('/api/v1/permit-types', {
      data: { code, category: 'Water', nameL10n: { en: 'E2E permit type' } },
    });
    expect(created.status(), `створення типу: ${await created.text()}`).toBeLessThan(300);
    const fresh = (await created.json()) as { rowVersion: string; nameL10n: Record<string, string> };

    const replaced = await page.request.put(`/api/v1/permit-types/${code}`, {
      data: {
        category: 'Water',
        nameL10n: fresh.nameL10n,
        rowVersion: fresh.rowVersion,
        rules: [],
        tables: [
          {
            code: 'Points',
            nameL10n: { en: 'Monitoring points' },
            order: 1,
            columns: [column('Name', 1, { key: true, required: true }), column('Note', 2)],
            rules: [],
          },
        ],
      },
    });
    expect(replaced.status(), `заміна чернетки: ${await replaced.text()}`).toBe(200);

    await page.goto(`/admin/permits/types/${code}?t=Points&panel=Note`);
    await expect(page.getByRole('heading', { level: 1, name: 'E2E permit type' })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText(/^Draft v\d+$/)).toBeVisible();

    const inspector = page.getByTestId('column-inspector');
    await expect(inspector).toBeVisible();
    const name = inspector.getByLabel(/Name \(English\)/);
    await name.fill('Remarks');

    // Правка відразу видна в таблиці, а збереження приходить після паузи.
    await expect(page.getByRole('row', { name: 'Remarks' })).toBeVisible();
    await expect(page.getByText(/1 unsaved changes/)).toBeVisible();
    await expect(page.getByText(/^Saved \d\d:\d\d$/)).toBeVisible({ timeout: 15_000 });

    // Сервер справді має нову назву (не лише кеш клієнта).
    await expect
      .poll(async () => {
        const body = (await (await page.request.get(`/api/v1/permit-types/${code}`)).json()) as {
          tables: { columns: { code: string; nameL10n: Record<string, string> }[] }[];
        };
        return body.tables[0]?.columns.find((c) => c.code === 'Note')?.nameL10n['en'];
      })
      .toBe('Remarks');

    await page.reload();
    await expect(page.getByRole('row', { name: 'Remarks' })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText(/unsaved changes/)).toHaveCount(0);
    // P2-N2: на 500 px підказка «Unique rows…» переноситься, а не прокручується (axe scrollable-region-focusable).
    await page.setViewportSize({ width: 500, height: 900 });
    const hint = page.locator('.pt-below .mantine-Code-root');
    await expect(hint).toBeVisible();
    const style = await hint.evaluate((el) => ({
      whiteSpace: getComputedStyle(el).whiteSpace,
      overflowX: getComputedStyle(el).overflowX,
      scrolls: el.scrollWidth > el.clientWidth + 1,
    }));
    expect(style).toEqual({ whiteSpace: 'pre-wrap', overflowX: 'visible', scrolls: false });
    // Тип ніколи не публікувався: його знімає `afterEach` (і тоді, коли тест упав раніше).
  });
});
