import { expect, test, type APIResponse } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * ✎ UAT recalc-progress (08.11; людина: «як мені зрозуміти що перерахунок завершен?», «якщо я залишу сторінку з
 * документами і повернуся я не бачу прогресу») проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, workflow
 * `e2e-stand`, `-Grep recalcProgress`). Рядки сценарію `.sync-local/ready/ACCEPTANCE-1108-post15.md`:
 *  - a1: діалог називає область (рік, усі документи, пропущені місяці); після підтвердження — «Queued…», ніколи
 *        «finished» (тост і банер);
 *  - a2: перехід зі сторінки й повернення, а також повне перезавантаження — банер прогресу на місці;
 *  - a3: коли всі документи завершились — «Finished: N recalculated, m skipped (approved), f failed» з посиланням;
 *  - a4: «My tasks» — ОДИН запис «Project recalculation …» з тими самими лічильниками, без рядків документів;
 *  - a5: перелік задач за проєктом/видом/батьком (API) і фільтр проєкту на `/admin/jobs`;
 *  - e2: сторінка Periods ніколи не каже «closes today» для минулої межі.
 * Не автоматизовано тут: a6 (провал дитини стенд не вміє викликати детерміновано — покрито vitest
 * `ProjectRecalculateAction.fanOut` і real-SQL `ProjectRecalcProgressStoreTests`), e1/e4 (зміна спільної політики
 * зсунула б базову лінію стенда — покрито `PeriodPolicyRecomputeTests`).
 *
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 * ⚠ Проєкт і документ — власні (`createMultiSheetDocument`), базову лінію стенда файл не чіпає.
 * ⚠ Банер бачить ініціатор (`mine=true`, рішення координатора 08.11) — тут ініціатор той самий користувач.
 * ⚠ Написано без локального прогону (живе вікно W15l): перший живий прогін — CI.
 */

interface DocumentCard {
  projectId: number;
}

interface JobRow {
  jobId: string;
  documentId?: number | null;
  projectId?: number | null;
  fanOutParentJobId?: string | null;
  effectiveState?: string | null;
  fanOut?: { total: number } | null;
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

const Recalculate = /^(Recalculate|Пересчитать|Қайта есептеу)$/;

test.describe('recalcProgress: прогрес перерахунку проєкту видно до кінця і після переходу', () => {
  test.describe.configure({ mode: 'serial', timeout: 600_000 });

  test('recalcProgress: a1–a5 і e2 — Queued, банер після повернення й reload, Finished, один запис у My tasks', async ({
    page,
  }) => {
    await signIn(page);
    const { documentId, periodKey } = await createMultiSheetDocument(page, 2, 2);
    const card = await ok<DocumentCard>(
      await page.request.get(`/api/v1/documents/${String(documentId)}?periodKey=${String(periodKey)}`),
      'GET document',
    );
    const projectId = card.projectId;

    // ── a1: область у діалозі; після підтвердження — «Queued», не «finished» ─────────────────────
    await page.goto(`/?projectId=${String(projectId)}`);
    await page.getByRole('button', { name: Recalculate }).first().click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText(/Recalculates all documents of|Пересчитываются все документы|барлық құжаттары/);
    await dialog.getByRole('button', { name: Recalculate }).click();

    await expect(page.getByText(/queued|в очередь|кезекке/i).first()).toBeVisible({ timeout: 30_000 });
    const banner = page.locator('[data-recalc-banner]').first();
    await expect(banner).toBeVisible({ timeout: 60_000 });
    const phase = await banner.getAttribute('data-recalc-phase');
    if (phase === 'queued' || phase === 'running') {
      await expect(banner.locator('[data-recalc-text]')).not.toContainText(/finished|завершено|аяқталды/i);
    }

    // ── a2: інша сторінка → назад; повне перезавантаження ───────────────────────────────────────
    await page.goto('/admin/periods');
    await page.goto(`/?projectId=${String(projectId)}`);
    await expect(page.locator('[data-recalc-banner]').first()).toBeVisible({ timeout: 60_000 });
    await page.reload();
    await expect(page.locator('[data-recalc-banner]').first()).toBeVisible({ timeout: 60_000 });

    // ── a3: усі документи завершились ────────────────────────────────────────────────────────────
    await expect(page.locator('[data-recalc-banner]').first()).toHaveAttribute(
      'data-recalc-phase',
      /^(finished|finishedWithErrors)$/,
      { timeout: 480_000 },
    );
    const finishedText = page.locator('[data-recalc-banner] [data-recalc-text]').first();
    await expect(finishedText).toContainText(/^Finished(?: with errors)?: \d+ recalculated, \d+ skipped \(approved\), \d+ failed/);
    await expect(page.locator('[data-recalc-open-list]').first()).toBeVisible();

    // ── a4: «My tasks» — один запис перерахунку проєкту, без рядків документів ─────────────────
    await page.getByRole('button', { name: /^(My tasks|Мои задачи|Менің тапсырмаларым)/ }).first().click();
    const drawer = page.getByRole('dialog');
    await expect(drawer.locator('[data-project-recalc-task]')).toHaveCount(1, { timeout: 30_000 });
    await expect(drawer.locator('[data-project-recalc-task]')).toContainText(/Project recalculation|Пересчёт проекта|қайта есептеу/);
    await page.keyboard.press('Escape');

    // ── a5: перелік за проєктом / видом / батьком ───────────────────────────────────────────────
    const parents = await ok<JobRow[]>(
      await page.request.get(`/api/v1/jobs?mine=true&kind=projectRecalculation&projectId=${String(projectId)}&limit=5`),
      'GET jobs kind=projectRecalculation',
    );
    expect(parents.length, 'перерахунку проєкту в переліку немає').toBeGreaterThanOrEqual(1);
    const parent = parents[0] as JobRow;
    expect(parent.projectId).toBe(projectId);
    expect(parent.fanOut?.total ?? 0).toBeGreaterThanOrEqual(1);

    const children = await ok<JobRow[]>(
      await page.request.get(`/api/v1/jobs?mine=true&parentJobId=${encodeURIComponent(parent.jobId)}`),
      'GET jobs parentJobId',
    );
    expect(children.length).toBeGreaterThanOrEqual(1);
    expect(children.every((c) => c.fanOutParentJobId === parent.jobId)).toBe(true);

    const mineTop = await ok<JobRow[]>(
      await page.request.get('/api/v1/jobs?mine=true&hideChildren=true&limit=50'),
      'GET jobs hideChildren',
    );
    expect(mineTop.some((j) => j.fanOutParentJobId === parent.jobId)).toBe(false);

    const badKind = await page.request.get('/api/v1/jobs?mine=true&kind=nope');
    expect(badKind.status()).toBe(422);

    await page.goto(`/admin/jobs?mine=1&project=${String(projectId)}`);
    await expect(page.locator(`[data-job-open="${parent.jobId}"]`)).toBeVisible({ timeout: 60_000 });

    // ── e2: Periods не каже «closes today» для минулої межі ─────────────────────────────────────
    await page.goto(`/admin/periods?projectId=${String(projectId)}`);
    await expect(page.locator('[data-periods-table="months"]')).toBeVisible({ timeout: 60_000 });
    const now = Date.now();
    const calendar = await ok<{ periods: { periodKey: number; endsAt: string; state: string }[] }>(
      await page.request.get(`/api/v1/projects/${String(projectId)}/periods`),
      'GET periods',
    );
    for (const period of calendar.periods.filter((p) => Date.parse(p.endsAt) <= now)) {
      const tile = page.locator(`[data-period-tile="${String(period.periodKey)}"]`);
      if ((await tile.count()) === 0) continue;
      await expect(tile).not.toContainText(/closes today|закрывается сегодня|бүгін жабылады/);
      await expect(page.locator(`[data-period-row="${String(period.periodKey)}"] [data-period-state]`)).toHaveAttribute(
        'data-period-state',
        'Closed',
      );
    }
  });
});
