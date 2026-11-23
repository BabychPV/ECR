import { expect, test, type Page } from '@playwright/test';

/**
 * ✎ F5-R (D-570, рішення людини P-1122-4: «так познач щоб користувач бачив це»): на справжньому стенді й у справжньому
 * Chromium — (а) рядок переліку документів, чий затверджений місяць має змінене джерело, показує позначку «Source changed»,
 * а підказка (фокус клавіатурою) називає місяць і дію; (б) панель «Проблеми розрахунку» документа показує причину «дані
 * HSE400 змінено після затвердження» з датою зміни й підказкою дії. Тексти — з сіду стенда (каталог), не ключі.
 *
 * ⚠ Відповіді переліку й `calculation-issues` підміняються в браузері (поле `sourceChangedPeriods` / рядок `sourceIssues`):
 * зв'язки HSE400 → Land у стенді немає, а перевіряється рендер, каталог і доступність позначки. Правило сервера тримають
 * інтеграційні тести `DocumentSourceIssuesAnyTests`.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

test.describe('Джерело змінено після затвердження (F5-R)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('перелік: позначка «Source changed» з підказкою; документ: причина з датою зміни й дією', async ({ page }) => {
    let firstId: number | null = null;
    await page.route(/\/api\/v1\/documents\?/, async (route) => {
      const response = await route.fetch();
      const body = (await response.json()) as { items?: Array<Record<string, unknown>> };
      const first = body.items?.[0];
      if (first !== undefined) {
        firstId = Number(first['id']);
        first['sourceChangedPeriods'] = [Number(PeriodKey)];
      }
      await route.fulfill({ response, json: body });
    });

    await signIn(page, Operator.user, Operator.password);
    await page.goto(`/?periodKey=${PeriodKey}`);

    const mark = page.locator('[data-source-changed]').first();
    await expect(mark, 'позначки «Source changed» у рядку немає').toBeVisible({ timeout: 30_000 });
    await expect(mark).toHaveText(/Source changed|Источник изменён|Дереккөз өзгерді/);
    await expect(mark).toHaveAttribute('data-periods', PeriodKey);

    // Підказка досяжна з клавіатури (`<Hint focusable>`), текст — з каталогу стенда, не ключ.
    await mark.focus();
    await expect(page.getByRole('tooltip')).toContainText(/HSE400/);
    await expect(page.getByRole('tooltip')).not.toContainText('documents.sourceChangedHint');

    expect(firstId, 'у переліку стенда немає документа').not.toBeNull();
    await page.route(/\/api\/v1\/documents\/\d+\/calculation-issues/, async (route) => {
      const response = await route.fetch();
      const body = (await response.json()) as Record<string, unknown>;
      body['sourceIssues'] = [
        {
          kind: 'SourceChangedAfterApproval',
          monthNumber: Number(PeriodKey) % 100,
          sourcePeriodKey: Number(PeriodKey),
          sourceDocumentId: null,
          sourceProjectCode: null,
          sourceDocumentKey: null,
          sourceApprovedAt: null,
          approvedAt: '2026-04-02T08:00:00Z',
          sourceChangedAt: '2026-04-05T11:00:00Z',
        },
      ];
      await route.fulfill({ response, json: body });
    });

    await page.goto(`/documents/${String(firstId)}?periodKey=${PeriodKey}`);
    const cause = page.getByTestId('document-calc-issue').filter({ has: page.getByTestId('document-calc-issue-source-hint') });
    await expect(cause, 'причини «джерело змінено» в панелі немає').toBeVisible({ timeout: 30_000 });
    await expect(cause).toHaveAttribute('data-kind', 'sourceChanged');
    await expect(cause.getByTestId('document-calc-issue-title')).toContainText(/HSE400/);
    await expect(cause.getByTestId('document-calc-issue-source-dates').locator('time')).toHaveCount(2);
    await expect(cause.getByTestId('document-calc-issue-source-hint')).not.toContainText('document.calcIssues');
    await expect(page.getByTestId('document-calc-issues')).toHaveAttribute('data-blocking', 'false');
  });
});

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
