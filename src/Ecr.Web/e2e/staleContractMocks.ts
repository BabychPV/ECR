import type { Page, Route } from '@playwright/test';

/**
 * Спільні підміни для `contractSection.spec.ts` і `staleResultsFlow.spec.ts` (RC15-E).
 *
 * ⚠ Стенд не тримає документа зі шапкою «Contract» і зі застарілими результатами, тому ці дві ознаки
 * підмінюються на рівні мережі НАД справжньою відповіддю (`route.fetch()` + правка JSON): решта картки,
 * список і зведення лишаються стендовими, перевіряється проводка екрана (початок → дія → кінець).
 * Модуль не імпортує нічого, крім типів Playwright.
 */
export const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
export const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
export const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

export const StaleSince = '2026-10-08T09:30:00Z';

export async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Справжня відповідь стенда як JSON; не-2xx віддається як є (`null` — підміна не потрібна). */
async function realJson(route: Route): Promise<Record<string, unknown> | null> {
  const response = await route.fetch();
  if (!response.ok()) {
    await route.fulfill({ response });
    return null;
  }
  return (await response.json()) as Record<string, unknown>;
}

/**
 * Картка документа `GET /api/v1/documents/{id}?periodKey=…`: `templateVersion` добивається (сторінка показує
 * «Version» лише за ним), `resultsStale` береться з `state.stale`.
 */
export async function mockDocumentCard(page: Page, state: { stale: boolean }): Promise<void> {
  await page.route(new RegExp(`/api/v1/documents/${DocumentId}\\?`), async (route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const card = await realJson(route);
    if (card === null) return;
    card['templateVersion'] ??= '1.0.4.0';
    card['resultsStale'] = state.stale;
    card['resultsStaleSince'] = state.stale ? StaleSince : null;
    return route.fulfill({ json: card });
  });
}
