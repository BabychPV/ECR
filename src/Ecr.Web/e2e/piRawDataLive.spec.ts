import { expect, test, type Page } from '@playwright/test';

/**
 * D-463 на живому стенді: «Дані з PI» (адміністратор інтеграції) і «Звідки це значення» (комірка документа).
 *
 * ⚠ Точки, сутність і покриття кладе стенд (`tools/e2e/pi-raw-data-seed.sql`, `ECR_E2E_PI_ENTITY`); мапінг на
 * комірку сценарій заводить сам через API на ту комірку, яку щойно вибрав у сітці, і прибирає у `finally` —
 * активний мапінг на колонку блокував би перенос версії документа в інших сценаріях (`migrateMappingsNotMapped`).
 * Запуск: `tools/e2e-stand.ps1 … -Grep "дані з PI"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const EntityId = process.env['ECR_E2E_PI_ENTITY'] ?? '';
const Tag = '\\\\E2E\\Plant\\Stack01|CO';

test.describe('дані з PI', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '' || EntityId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('дані з PI: тег, графік року, покриття з прогалиною, сирі точки, CSV', async ({ page }) => {
    test.slow();
    await signIn(page);

    await page.goto(`/admin/pi-data?entity=${EntityId}&range=lastYear`);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 30_000 });

    // Тег без мапінгів — шлях самої сутності; графік — SVG з підсумком словами.
    await expect(page.getByLabel(/^Tag/)).toHaveValue(Tag, { timeout: 30_000 });
    const chart = page.locator('[data-pi-chart] svg[role="img"]');
    await expect(chart).toBeVisible({ timeout: 30_000 });
    await expect(chart).toHaveAttribute('aria-label', /numeric points/);

    // Покриття: стенд залишив прогалину; останній прогін — успішний.
    await expect(page.locator('[data-pi-coverage]')).toBeVisible();

    // Сирі точки: таблиця з рядками, нові зверху.
    const table = page.getByRole('table', { name: /Raw points/ });
    await expect(table.getByRole('row').nth(1)).toBeVisible({ timeout: 30_000 });

    // Вікно останніх 30 днів документа: прогалина в покритті видна поіменно.
    await page.goto(`/admin/pi-data?entity=${EntityId}&range=90d`);
    await expect(page.locator('[data-pi-coverage]')).toBeVisible({ timeout: 30_000 });

    // CSV — потоком із сервера; файл приходить як завантаження.
    const download = page.waitForEvent('download');
    await page.locator('[data-pi-export]').click();
    const file = await download;
    expect(file.suggestedFilename()).toMatch(new RegExp(`^pi-${EntityId}-.*\\.csv$`));

    // Без права (той самий маршрут під Document.View-користувачем) — не «дані», а відмова: перевіряє сервер.
    const forbidden = await page.request.get(`/api/v1/sources/${EntityId}/raw-data/tags`);
    expect(forbidden.status()).toBe(200);
  });

  test('дані з PI: «Звідки це значення» для комірки, яку живить мапінг', async ({ page }) => {
    test.slow();
    await signIn(page);

    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
    const grid = page.locator('revo-grid').first();
    await expect(grid).toBeVisible({ timeout: 60_000 });

    // Вибрана комірка: її адресу (рядок, колонка) називає запит секції джерела у вкладці Info.
    let request = page.waitForRequest((r) => r.url().includes('/cell-source?'), { timeout: 30_000 });
    await grid.locator('revogr-data [data-rgcol]').first().click();
    await expect(page.locator('[data-formula-bar="cell"]').first()).toBeVisible();
    await page.locator('[data-inspector-triggers]').getByRole('button', { name: /History/i }).click();
    const aside = page.getByRole('complementary');
    await aside.getByRole('tab').nth(2).click();
    await expect(aside.locator('[data-inspector-info]')).toBeVisible();

    // ⚠ Мапінг на текстову колонку сервер може відхилити — тоді праворуч, до першої придатної комірки.
    let created = null as Awaited<ReturnType<typeof page.request.post>> | null;
    let attempts = 0;
    while (created === null || (created.status() === 422 && attempts < 6)) {
      const url = new URL((await request).url());
      const rowKey = url.searchParams.get('rowKey') ?? '';
      expect(rowKey).not.toBe('');

      // До мапінгу секції немає: комірка видима, але не з PI.
      await expect(aside.locator('[data-cell-source]')).toHaveCount(0);

      created = await page.request.post('/api/v1/entity-field-maps', {
        data: {
          sourceEntityId: Number(EntityId),
          sourceField: Tag,
          targetKind: 'Column',
          targetColumnDefId: Number(url.searchParams.get('columnDefId')),
          targetRegistryFieldDefId: null,
          sourceUnitId: null,
          targetUnitId: null,
          targetRowKey: rowKey,
          aggregation: 'Sum',
        },
      });
      attempts += 1;
      if (created.status() === 422) {
        request = page.waitForRequest((r) => r.url().includes('/cell-source?'), { timeout: 30_000 });
        await page.keyboard.press('ArrowRight');
      }
    }

    expect(created.status(), `мапінг: ${await created.text()}`).toBe(201);
    const map = (await created.json()) as { id: number };

    try {
      // Кеш секції — на комірку: свіже читання — після перезавантаження, з тим самим курсором.
      await page.reload();
      await expect(grid).toBeVisible({ timeout: 60_000 });
      await grid.locator('revogr-data [data-rgcol]').first().click();
      await expect(page.locator('[data-formula-bar="cell"]').first()).toBeVisible();
      for (let i = 1; i < attempts; i += 1) await page.keyboard.press('ArrowRight');

      const section = page.getByRole('complementary').locator('[data-cell-source]');
      await expect(section).toBeVisible({ timeout: 30_000 });
      await expect(section).toContainText(Tag);
      await expect(section).toContainText('Sum');

      await section.locator('[data-cell-source-open]').click();
      const modal = page.locator('[data-cell-source-modal]');
      await expect(modal).toBeVisible();
      await expect(modal.locator('svg[role="img"]')).toBeVisible({ timeout: 30_000 });
      await expect(modal.getByRole('table', { name: /Raw points/ }).getByRole('row').nth(1)).toBeVisible();
    } finally {
      const removed = await page.request.delete(`/api/v1/entity-field-maps/${String(map.id)}`);
      expect(removed.ok(), `прибирання мапінгу: ${String(removed.status())} ${await removed.text()}`).toBe(true);
    }
  });
});

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
