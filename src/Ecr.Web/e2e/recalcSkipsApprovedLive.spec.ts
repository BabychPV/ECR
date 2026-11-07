import { expect, test, type APIResponse } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * ✎ UAT-recalc-ux (UAT 07.11; слово людини: «тільки погоджені не мають перераховуватися … це також має логуватися»)
 * проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep recalcSkipsApproved`):
 *  1. документ на трьох аркушах власного проєкту: аркуш A поданий, B затверджений, C чернетка;
 *  2. Periods → «Recalculate» проєкту: замість відмови «Period … has submitted sheets» — перелік «1 затверджений аркуш
 *     пропущено» з посиланням, яке відкриває документ на аркуші B у його періоді;
 *  3. поданий A перераховано — на картці документа позначка «Recalculated after submission» для погоджувача;
 *  4. Documents → фільтр «Partly submitted» знаходить документ.
 *
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 * ⚠ Проєкт і документ — власні (`createMultiSheetDocument`), базову лінію стенда файл не чіпає.
 * ⚠ Написано без локального прогону (правило «e2e — на CI»): перший живий прогін — CI.
 */

interface DocumentTable {
  sheetDefId: number;
  sheetCode: string;
}

interface DocumentCard {
  projectId: number;
  businessKey: string;
  sheets?: { code: string; recalculatedAfterSubmission?: boolean }[] | null;
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

test.describe('Recalculation skips approved sheets and marks recalculated submitted sheets', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('project recalculation lists the approved sheet with a link, marks the submitted sheet and the documents filter finds a partly submitted document', async ({
    page,
  }) => {
    await signIn(page);
    const { documentId, periodKey } = await createMultiSheetDocument(page, 3, 2);
    const id = String(documentId);
    const period = String(periodKey);

    // ── Аркуші документа: по таблиці на аркуш (`createMultiSheetDocument`) ──────────────────────
    const tables = await ok<DocumentTable[]>(
      await page.request.get(`/api/v1/documents/${id}/tables?periodKey=${period}`),
      'GET tables',
    );
    const sheets = [...new Map(tables.map((table) => [table.sheetDefId, table])).values()];
    expect(sheets.length, 'документ без трьох аркушів').toBeGreaterThanOrEqual(3);
    const [submitted, approved] = sheets as [DocumentTable, DocumentTable];

    const submit = async (sheet: DocumentTable): Promise<void> => {
      await ok(
        await page.request.post(`/api/v1/documents/${id}/submit`, {
          data: { sheetDefId: sheet.sheetDefId, periodKey: periodKey, acknowledgeWarnings: true },
        }),
        `submit ${sheet.sheetCode}`,
      );
    };

    await submit(submitted);
    await submit(approved);
    await ok(
      await page.request.post(`/api/v1/documents/${id}/approve`, {
        data: { sheetDefId: approved.sheetDefId, periodKey: periodKey, approved: true, reason: null },
      }),
      `approve ${approved.sheetCode}`,
    );

    const card = await ok<DocumentCard>(
      await page.request.get(`/api/v1/documents/${id}?periodKey=${period}`),
      'GET document',
    );

    // ── Periods → Recalculate: перелік пропущених замість відмови ───────────────────────────────
    await page.goto(`/admin/periods?projectId=${String(card.projectId)}`);
    await page.getByRole('button', { name: /^(Recalculate|Пересчитать|Қайта есептеу)$/ }).first().click();
    await page
      .getByRole('dialog')
      .getByRole('button', { name: /^(Recalculate|Пересчитать|Қайта есептеу)$/ })
      .click();

    const skipped = page.locator('[data-recalc-skipped]');
    await expect(skipped, 'перелік пропущених аркушів не з’явився').toBeVisible({ timeout: 180_000 });
    await expect(skipped.locator('summary')).toContainText('1');
    await skipped.locator('summary').click();

    const link = skipped.locator('[data-recalc-skipped-link]');
    await expect(link).toHaveCount(1);
    await expect(link).toHaveAttribute(
      'href',
      `/documents/${id}?periodKey=${period}&sheet=${encodeURIComponent(approved.sheetCode)}`,
    );

    // ── Поданий аркуш перераховано й позначено (журнал → картка) ────────────────────────────────
    await expect
      .poll(
        async () => {
          const fresh = await ok<DocumentCard>(
            await page.request.get(`/api/v1/documents/${id}?periodKey=${period}`),
            'GET document',
          );
          return fresh.sheets?.find((sheet) => sheet.code === submitted.sheetCode)?.recalculatedAfterSubmission ?? false;
        },
        { message: 'поданий аркуш без позначки «перераховано після подання»', timeout: 180_000 },
      )
      .toBe(true);

    // Посилання з переліку відкриває документ на затвердженому аркуші в його періоді.
    await link.click();
    await expect(page).toHaveURL(new RegExp(`/documents/${id}\\?periodKey=${period}&sheet=`));

    await page.goto(`/documents/${id}?periodKey=${period}&sheet=${encodeURIComponent(submitted.sheetCode)}`);
    await expect(page.locator('[data-recalculated-after-submission]')).toBeVisible({ timeout: 60_000 });

    // ── Documents: фільтр «Partly submitted» ───────────────────────────────────────────────────
    // Перелік документів живе на `/` (routes.ts: home), не на `/documents`.
    await page.goto(`/?periodKey=${period}&projectId=${String(card.projectId)}`);
    await page.getByRole('combobox', { name: /^(State|Состояние|Күйі)$/ }).selectOption('PartlySubmitted');
    await expect(page).toHaveURL(/state=PartlySubmitted/);
    await expect(page.locator(`[data-document-row="${card.businessKey}"]`)).toBeVisible({ timeout: 60_000 });
  });
});
