import { expect, test, type Page } from '@playwright/test';

/**
 * Застарілі числа методологій (лінія B) на живому стенді: бейдж у шапці документа і пункт
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

  test('бейдж є, поки числа застарілі; «Recalculate calculations» перечитує числа, і бейдж зникає', async ({ page }) => {
    test.slow();

    let stale = true;
    let recalcPosts = 0;
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

    // Початковий стан: бейдж видно.
    const badge = page.getByTestId('document-methodology-stale');
    await expect(badge).toBeVisible({ timeout: 60_000 });

    // Дія: пункт з новою назвою в «More».
    await page.getByTestId('document-more').click();
    await page.getByRole('menuitem', { name: /Recalculate calculations|Пересчитать расчёты|Есептеулерді қайта есептеу/ }).click();

    // Кінцевий стан: перерахунок поставлено, числа перечитано — бейджа немає.
    await expect(badge).toHaveCount(0, { timeout: 30_000 });
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
