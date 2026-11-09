import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * Приймальна 08.11 (i) проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "Doc reopen:"`):
 * закритий місяць відкривається для ОДНОГО документа (слово людини 08.11: «якщо мені потрібно відкрити тільки для
 * одного документу?»).
 *  - i1: у банері закритого періоду — «Reopen … for this document…», діалог із причиною й «Open until»;
 *  - i2: після підтвердження документ редагується («Reopened for this document until …»), інший документ того самого
 *    місяця лишається закритим;
 *  - i5: журнал структурних змін містить відкриття (документ, місяць, причина, строк).
 * i3 (закриття за строком) і i7 (перерахунок) доводять серверні тести (`DocumentPeriodReopenTests`,
 * `RecalculationJobClosedPeriodTests`): чекати строку на стенді — хвилини простою. i4 (рядок на Periods) — інша lane.
 *
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 * ⚠ Проєкт і документи — власні (`createMultiSheetDocument` + другий документ того самого проєкту); проєкт поточного
 * року за `ECR-Standard`, тож місяці, що вже минули, закриті. Базову лінію стенда файл не чіпає.
 * ⚠ Написано без локального прогону (правило «e2e — на CI»): перший живий прогін — CI.
 */

interface DocumentTable {
  sheetDefId: number;
}

interface CalendarPeriod {
  periodKey: number;
  state: string;
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

/** Два документи одного проєкту і закритий місяць цього проєкту. */
async function twoDocumentsInClosedMonth(page: Page): Promise<{ first: number; second: number; periodKey: number }> {
  const { documentId, periodKey: openKey } = await createMultiSheetDocument(page, 1, 1);
  const card = await ok<{ projectId: number }>(
    await page.request.get(`/api/v1/documents/${String(documentId)}?periodKey=${String(openKey)}`),
    'GET document',
  );
  const calendar = await ok<{ periods: CalendarPeriod[] }>(
    await page.request.get(`/api/v1/projects/${String(card.projectId)}/periods`),
    'календар проєкту',
  );
  const closed = calendar.periods.find((period) => period.state === 'Closed');
  expect(closed, 'у проєкту поточного року немає закритого місяця (січень мав бути закритий)').toBeDefined();

  const tables = await ok<DocumentTable[]>(
    await page.request.get(`/api/v1/documents/${String(documentId)}/tables?periodKey=${String(openKey)}`),
    'GET tables',
  );
  const second = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: card.projectId, sheetDefIds: [...new Set(tables.map((table) => table.sheetDefId))] },
    }),
    'другий документ',
  );

  return { first: documentId, second: second.documentId, periodKey: closed?.periodKey ?? 0 };
}

test.describe('Doc reopen', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('Doc reopen: a locked month opens for one document only, the other stays locked, and the audit has it', async ({ page }) => {
    await signIn(page);
    const { first, second, periodKey } = await twoDocumentsInClosedMonth(page);
    const reason = `e2e doc reopen ${String(Date.now())}`;

    // i1: банер закритого періоду з дією відкриття для документа.
    await page.goto(`/documents/${String(first)}?periodKey=${String(periodKey)}`);
    const lockBanner = page.locator('[data-document-lock="periodClosed"]');
    await expect(lockBanner).toBeVisible({ timeout: 60_000 });
    const action = lockBanner.getByTestId('document-period-reopen');
    await expect(action).toBeEnabled();
    await expect(action).toContainText(/for this document/i);
    await action.click();

    const dialog = page.getByRole('dialog');
    await expect(dialog.getByTestId('document-period-reopen-scope')).toContainText(/Only this document/i);
    const confirm = dialog.getByTestId('document-period-reopen-confirm');
    // Причина обов'язкова.
    await expect(confirm).toBeDisabled();
    await dialog.getByTestId('document-period-reopen-reason').fill(reason);
    await expect(confirm).toBeEnabled();
    await confirm.click();

    // i2: документ відкрито — банер «Reopened for this document until …», банера закритого періоду немає.
    await expect(page.getByTestId('document-period-reopened')).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('document-period-reopened')).toContainText(reason);
    await expect(lockBanner).toHaveCount(0);

    // Інший документ того самого місяця — закритий, як і був.
    await page.goto(`/documents/${String(second)}?periodKey=${String(periodKey)}`);
    await expect(page.locator('[data-document-lock="periodClosed"]')).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId('document-period-reopened')).toHaveCount(0);

    // Те саме — з боку сервера.
    const otherState = await ok<{ active: unknown }>(
      await page.request.get(`/api/v1/documents/${String(second)}/period-reopen?periodKey=${String(periodKey)}`),
      'стан другого документа',
    );
    expect(otherState.active).toBeNull();

    // i5: журнал структурних змін — відкриття з документом, місяцем і причиною.
    const today = new Date().toISOString().slice(0, 10);
    const audit = await ok<{ items: { operation: string; changeReason: string | null; newJson: string | null }[] }>(
      await page.request.get(`/api/v1/audit/structure?from=${today}T00:00:00Z&to=${today}T23:59:59Z&entityType=DocumentPeriodReopen&limit=100`),
      'журнал структурних змін',
    );
    // The reason is stored as a message key with params ({"k":"document.reopen.audit.reopened","p":{"reason":…,"until":…}}).
    const reopenedWith = (changeReason: string | null): boolean => {
      try {
        const parsed = JSON.parse(changeReason ?? '') as { k?: string; p?: Record<string, string> };
        return parsed.k === 'document.reopen.audit.reopened' && parsed.p?.['reason'] === reason && (parsed.p['until'] ?? '') !== '';
      } catch {
        return false;
      }
    };
    const entry = audit.items.find((item) => reopenedWith(item.changeReason));
    expect(entry, 'у журналі немає відкриття з цією причиною').toBeDefined();
    expect(entry?.operation).toBe('Reopen');
    expect(entry?.newJson ?? '').toContain(`"documentId":${String(first)}`);
    expect(entry?.newJson ?? '').toContain(`"periodKey":${String(periodKey)}`);
    expect(entry?.newJson ?? '').toContain('openUntil');
  });
});
