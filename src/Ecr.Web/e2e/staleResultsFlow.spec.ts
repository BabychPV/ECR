import { expect, test } from '@playwright/test';
import { DocumentId, mockDocumentCard, PeriodKey, signIn, StaleSince } from './staleContractMocks';

/**
 * RC15-E: застарілі результати методологій на живому стенді (RC14-D/E, RC15-C):
 * позначка й чіп у переліку Documents, блок «Потребують перерахунку» в «My tasks», банер застарілості, що з'являється
 * БЕЗ перезавантаження після правки комірки, кнопка «Recalculate» у банері.
 * Запуск: `tools/e2e-stand.ps1 … -Grep "застарі"`; модальні перевірки — також під `-Grep "модальний"`.
 *
 * ⚠ Стенд не тримає застарілих документів: ознака `resultsStale` підмінена над справжніми відповідями
 * (перелік, зведення, картка), постановка перерахунку й задача — маршрутами. Вхід, сітка, правка комірки,
 * шухляда й навігація — справжні.
 */
test.describe('застарілі результати: перелік, My tasks, банер', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  /** Перелік документів і зведення: документ стенда застарілий; фільтр `resultsStale` лишає лише його. */
  async function mockStaleList(page: import('@playwright/test').Page): Promise<void> {
    await page.route(/\/api\/v1\/documents\?/, async (route) => {
      if (route.request().method() !== 'GET') return route.fallback();
      const url = new URL(route.request().url());
      const filtered = url.searchParams.get('resultsStale') === 'true';
      // Справжній запит без застарілих параметрів: сервер стенда застарілих не знає.
      url.searchParams.delete('resultsStale');
      url.searchParams.delete('staleBy');
      const response = await route.fetch({ url: url.toString() });
      if (!response.ok()) return route.fulfill({ response });
      const body = (await response.json()) as { items: Record<string, unknown>[] };
      const items = body.items.map((item) =>
        String(item['id']) === DocumentId ? { ...item, resultsStale: true, resultsStaleSince: StaleSince } : item,
      );
      return route.fulfill({
        json: { ...body, items: filtered ? items.filter((item) => item['resultsStale'] === true) : items },
      });
    });
    await page.route(/\/api\/v1\/documents\/summary/, async (route) => {
      const response = await route.fetch();
      if (!response.ok()) return route.fulfill({ response });
      const summary = (await response.json()) as Record<string, unknown>;
      return route.fulfill({ json: { ...summary, staleResultsCount: 1, staleResultsMineCount: 1 } });
    });
  }

  test('Documents: позначка «Results stale» у рядку й чіп-фільтр «Needs recalculation (1)»', async ({ page }) => {
    test.slow();
    await mockStaleList(page);
    await signIn(page);
    await page.goto(`/?periodKey=${PeriodKey}`);

    // Початок: позначка у рядку стендового документа.
    const mark = page.locator('[data-results-stale="true"]').first();
    await expect(mark).toBeVisible({ timeout: 60_000 });

    // Сам input чіпа Mantine схований поза в'юпортом: клік іде по його підпису.
    const chip = page.getByRole('checkbox', { name: /\(1\)/ });
    const chipLabel = page.locator('label.mantine-Chip-label').filter({ hasText: /\(1\)/ });
    await expect(chipLabel).toBeVisible();
    await expect(chip).not.toBeChecked();

    // Дія: чіп вмикає фільтр на сервері (resultsStale=true у запиті).
    const filtered = page.waitForRequest(
      (request) => request.url().includes('/api/v1/documents?') && request.url().includes('resultsStale=true'),
    );
    await chipLabel.click();
    await filtered;

    // Кінець: фільтр у силі, позначений документ лишається в переліку.
    await expect(chip).toBeChecked();
    await expect(page.locator('[data-results-stale="true"]').first()).toBeVisible();
  });

  test('модальний діалог My tasks: блок «Потребують перерахунку» веде до документа, Esc вертає фокус', async ({ page }) => {
    test.slow();
    await mockStaleList(page);
    await signIn(page);
    await page.goto(`/?periodKey=${PeriodKey}`);

    const opener = page.getByRole('button', { name: /^(My tasks|Мои задачи|Менің тапсырмаларым)/ });
    await expect(opener).toBeVisible({ timeout: 60_000 });
    await opener.click();

    const drawer = page.getByRole('dialog');
    await expect(drawer).toBeVisible({ timeout: 15_000 });
    const section = drawer.getByTestId('my-tasks-stale');
    await expect(section).toBeVisible({ timeout: 30_000 });
    // Документ стенда може потрапити в кілька відкритих періодів - карток кілька, перевіряється наявність.
    await expect(section.locator(`[data-stale-document="${DocumentId}"]`).first()).toBeVisible();

    // Закриття: Esc ховає шухляду й повертає фокус на кнопку.
    await page.keyboard.press('Escape');
    await expect(drawer).toBeHidden({ timeout: 10_000 });
    await expect(opener).toBeFocused();

    // Повторне відкриття (open + close + open): блок на місці, перехід за посиланням веде в документ.
    await opener.click();
    const again = page.getByRole('dialog');
    await expect(again.getByTestId('my-tasks-stale')).toBeVisible({ timeout: 30_000 });
    await again
      .getByTestId('my-tasks-stale')
      .locator(`[data-stale-document="${DocumentId}"]`)
      .first()
      .getByRole('link')
      .click();
    await expect(page).toHaveURL(new RegExp(`/documents/${DocumentId}\\?.*periodKey=${PeriodKey}`));
  });

  test('банер застарілості з\'являється без перезавантаження після правки, «Recalculate» його прибирає', async ({ page }) => {
    test.slow();

    const state = { stale: false };
    let recalcPosts = 0;
    await mockDocumentCard(page, state);
    // Правка комірки доходить до справжнього сервера НЕ перехопленою; після успіху «сервер» вважає результати застарілими.
    page.on('response', (response) => {
      if (
        response.request().method() === 'PATCH' &&
        response.url().includes(`/documents/${DocumentId}/cells`) &&
        response.ok()
      ) {
        state.stale = true;
      }
    });
    await page.route(`**/api/v1/documents/${DocumentId}/recalculate`, (route) => {
      recalcPosts += 1;
      state.stale = false;
      return route.fulfill({
        status: 202,
        json: { jobId: 'e2e-job-rc15', documentId: Number(DocumentId), periodKey: Number(PeriodKey) },
      });
    });
    await page.route('**/api/v1/jobs/e2e-job-rc15', (route) => route.fulfill({ json: { state: 'Succeeded' } }));

    await signIn(page);
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
    const cell = page.locator('revo-grid').first().locator('revogr-data[type="rgRow"] .rgCell').first();
    await expect(cell).toBeVisible({ timeout: 60_000 });

    // Початок: банера немає. Маркер у window доводить, що сторінку далі не перезавантажували.
    const banner = page.getByTestId('document-stale-results');
    await expect(banner).toHaveCount(0);
    await page.evaluate(() => {
      (window as unknown as Record<string, unknown>)['__e2eNoReload'] = true;
    });

    // Дія: правка комірки (нове значення щоразу — повтор того самого PATCH не дає).
    await cell.click();
    const editor = page.locator('revo-grid').first().locator('.edit-input-wrapper input');
    await page.keyboard.press('Enter');
    await expect(editor).toBeFocused();
    await page.keyboard.type(String(100 + Math.floor(Math.random() * 800)));
    const patched = page.waitForResponse(
      (response) => response.request().method() === 'PATCH' && response.url().includes(`/documents/${DocumentId}/cells`),
      { timeout: 20_000 },
    );
    await page.keyboard.press('Enter');
    await expect(page.locator('.edit-input-wrapper')).toHaveCount(0);
    const patchResponse = await patched;
    expect(patchResponse.ok(), `PATCH cells: ${String(patchResponse.status())} ${await patchResponse.text()}`).toBe(true);

    // Кінець 1: банер з'явився сам, сторінка не перезавантажувалась.
    await expect(banner).toBeVisible({ timeout: 20_000 });
    expect(await page.evaluate(() => (window as unknown as Record<string, unknown>)['__e2eNoReload'])).toBe(true);

    // Дія 2: «Recalculate» у банері.
    await banner.getByTestId('document-stale-results-recalculate').click();

    // Кінець 2: перерахунок поставлено один раз, картку перечитано — банера немає.
    await expect(banner).toHaveCount(0, { timeout: 30_000 });
    expect(recalcPosts).toBe(1);
  });
});
