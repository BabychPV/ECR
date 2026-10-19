import { expect, test, type Page } from '@playwright/test';

/**
 * Перелік і екран прив'язок аркушів до пермітів (`/admin/permits/bindings`, `/bindings/:id`) — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжні `GET /api/v1/templates`, `GET /api/v1/templates/versions` і
 * `GET /api/v1/template-versions/{id}/permit-bindings` під правом `Template.View`; кількість рядків переліку збігається з
 * тим, що віддає сервер для останньої опублікованої версії й чернетки перших 20 шаблонів (так екран і читає);
 * перехід по рядку веде на екран прив'язки з посиланням на шаблон. Прив'язок на стенді може не бути — тоді перевіряється
 * чесний порожній стан, а не вигадані рядки.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`; на CI — workflow `e2e-stand`): `npx playwright test -g "Permit bindings pages"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

/** Перша порція екрана: стільки шаблонів (за кодом) читається без «Check more». */
const FirstBatch = 20;

interface Template {
  readonly id: number;
  readonly code: string;
}

interface VersionEntry {
  readonly templateId: number;
  readonly versions: readonly { readonly id: number; readonly status: string }[];
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилась').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

function lastWithStatus(entry: VersionEntry | undefined, status: string): number | null {
  const found = [...(entry?.versions ?? [])].reverse().find((version) => version.status === status);

  return found?.id ?? null;
}

/** Скільки прив'язок віддає сервер для версій, які читає екран (остання опублікована і остання чернетка). */
async function expectedBindingCount(page: Page): Promise<number> {
  const templatesResponse = await page.request.get('/api/v1/templates?limit=100');
  expect(templatesResponse.status(), 'GET /api/v1/templates').toBe(200);
  const templates = ((await templatesResponse.json()) as { items: Template[] }).items
    .slice()
    .sort((a, b) => a.code.localeCompare(b.code))
    .slice(0, FirstBatch);
  if (templates.length === 0) return 0;

  const query = templates.map((template) => `ids=${String(template.id)}`).join('&');
  const versionsResponse = await page.request.get(`/api/v1/templates/versions?${query}`);
  expect(versionsResponse.status(), 'GET /api/v1/templates/versions').toBe(200);
  const entries = (await versionsResponse.json()) as VersionEntry[];

  let total = 0;
  for (const template of templates) {
    const entry = entries.find((candidate) => candidate.templateId === template.id);
    for (const status of ['Published', 'Draft']) {
      const versionId = lastWithStatus(entry, status);
      if (versionId === null) continue;

      const bindings = await page.request.get(`/api/v1/template-versions/${String(versionId)}/permit-bindings`);
      expect(bindings.status(), `GET permit-bindings версії ${String(versionId)}`).toBe(200);
      total += ((await bindings.json()) as { items: unknown[] }).items.length;
    }
  }

  return total;
}

test.describe('Permit bindings pages', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('перелік читає прив’язки версій шаблонів, збігається з сервером і веде на екран прив’язки', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const expected = await expectedBindingCount(page);

    await page.goto('/admin/permits/bindings');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(/Sheet bindings|Привязки листов|Парақ байланыстары/, {
      timeout: 30_000,
    });
    await expect(page.getByRole('navigation', { name: /Permits sections|Разделы|Бөлімдер/i })).toBeVisible();

    // Примітка про охоплення: спершу «Read X of Y … so far», по завершенні — «Checked N of M templates (…)».
    const note = page.locator('[data-scan-note]');
    await expect(note).toContainText(/Checked \d+ of \d+ templates \(\d+ published or draft versions\)/, {
      timeout: 120_000,
    });
    await expect(page.getByTestId('bindings-failed'), 'версії, прив’язки яких не прочиталися').toHaveCount(0);

    const rows = page.locator('[data-list-table] tr[data-row-key]');
    if (expected === 0) {
      await expect(page.getByText(/No bindings yet/)).toBeVisible();
      await expect(rows).toHaveCount(0);

      return;
    }

    await expect(rows.first()).toBeVisible();
    if (expected <= 40) {
      await expect(rows, 'рядків переліку стільки, скільки прив’язок віддав сервер').toHaveCount(expected);
    }

    await rows.first().click();
    await page.waitForURL(/\/admin\/permits\/bindings\/[^/?]+\?template=\d+&version=\d+/, { timeout: 15_000 });
    await expect(page.locator('[data-binding-detail]')).toBeVisible({ timeout: 30_000 });
    await expect(page.locator('[data-binding-arrow]')).toBeVisible();

    const openTemplate = page.getByRole('link', { name: /Open the template/ });
    await expect(openTemplate).toHaveAttribute('href', /\/admin\/templates\/\d+\/versions\/\d+/);
    await expect(page.getByRole('link', { name: /Sheet bindings|Привязк|байланыстар/ }).first()).toBeVisible();
  });

  test('посилання без версії й шаблону не вгадує версію, а каже, що воно неповне', async ({ page }) => {
    test.setTimeout(90_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const permitBindingRequests: string[] = [];
    page.on('request', (request) => {
      if (/permit-bindings/.test(request.url())) permitBindingRequests.push(request.url());
    });

    await page.goto('/admin/permits/bindings/B-NO-SUCH');
    await expect(page.getByText(/This link is incomplete/)).toBeVisible({ timeout: 30_000 });
    expect(permitBindingRequests, 'неповне посилання не мусить читати прив’язки').toHaveLength(0);
  });
});
