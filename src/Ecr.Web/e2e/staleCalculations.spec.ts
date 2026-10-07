import { expect, test, type Page } from '@playwright/test';

/**
 * Застарілі числа методологій (лінія B) на живому стенді: банер застарілості (StaleResultsBanner) над сіткою без бейджа в шапці і пункт
 * «Recalculate calculations» у «More». Запуск «Аудитом»: `tools/e2e-stand.ps1 … -Grep "методологій"`.
 *
 * ⚠ Числа, постановка перерахунку й стан задачі підмінені маршрутами: стенд не тримає документа зі
 * застарілими методологіями, а перевіряється тут проводка екрана (початковий стан → дія → кінцевий стан).
 */

const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('застарілі результати методологій', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('банер є, поки числа застарілі, бейджа в шапці немає; «Recalculate calculations» перечитує числа, і пункт повертає звичайну назву', async ({ page }) => {
    test.slow();

    let stale = true;
    let recalcPosts = 0;
    // Картка документа: resultsStale/resultsStaleSince керують банером; після перерахунку - свіжа.
    await page.route(new RegExp(`/api/v1/documents/${DocumentId}(\\?.*)?$`), async (route) => {
      const response = await route.fetch();
      const body = (await response.json()) as Record<string, unknown>;
      await route.fulfill({
        response,
        json: { ...body, resultsStale: stale, resultsStaleSince: stale ? '2026-10-07T10:00:00Z' : null },
      });
    });
    await page.route(`**/api/v1/documents/${DocumentId}/calculation-results**`, (route) =>
      route.fulfill({
        json: [{ rowKey: 'R1', outputCode: 'OUT', value: 1, unitId: 1, isStale: stale, changedRegistries: [] }],
      }),
    );
    await page.route(`**/api/v1/documents/${DocumentId}/recalculate`, (route) => {
      recalcPosts += 1;
      stale = false;
      return route.fulfill({ status: 202, json: { jobId: 'e2e-job', documentId: Number(DocumentId), periodKey: Number(PeriodKey) } });
    });
    await page.route('**/api/v1/jobs/e2e-job', (route) => route.fulfill({ json: { state: 'Succeeded' } }));

    await signIn(page);
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);

    // Початковий стан: банер видно, бейджа немає, у More - «Recalculate calculations».
    const banner = page.getByTestId('document-stale-results');
    await expect(banner).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId('document-methodology-stale')).toHaveCount(0);

    await page.getByTestId('document-more').click();
    await page.getByRole('menuitem', { name: /Recalculate calculations|Пересчитать расчёты|Есептеулерді қайта есептеу/ }).click();

    // Кінцевий стан: перерахунок поставлено, картку й числа перечитано - банера немає.
    await expect(banner).toHaveCount(0, { timeout: 30_000 });
    expect(recalcPosts).toBe(1);
  });
});

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
