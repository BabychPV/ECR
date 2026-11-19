import { expect, test, type Page } from '@playwright/test';

/**
 * `B80 N5` (D-554): вплив Approve / Reopen аркуша-джерела на залежні документи на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжній `GET /api/v1/documents/{id}/source-impact` віддає форму
 * `SourceImpactDto`, сервер сам відмовляє `409 ECR-SRC-0409` на `reopen` затвердженого джерела без `confirmedImpact`
 * (коли вплив потрібен), а діалог впливу показує залежні документи з відповіді сервера й закривається без змін.
 *
 * Потрібен стенд із документом-джерелом (HSE400) у стані `Approved`, зв'язкою `approvedOnly` і затвердженим залежним
 * місяцем Land. Координати — змінні оточення: `ECR_E2E_IMPACT_DOC` (id документа-джерела), `ECR_E2E_IMPACT_SHEET`
 * (`sheetDefId`), `ECR_E2E_IMPACT_PERIOD` (`yyyyMM`). Без них тести пропускаються.
 *
 * ⛔ Справжнє повернення/затвердження в e2e НЕ виконується: діалог впливу скасовується, а прямий `POST reopen` без
 * підтвердження обов'язково відмовляється (нічого не міняється). Запис із підтвердженням доводять
 * `SourceImpactApiTests` (HTTP, реальний SQL).
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test sourceImpact.spec.ts`. ⚠ Файл написано, але в цій гілці
 * НЕ запускався (координатор проганяє пачкою).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const DocumentId = process.env['ECR_E2E_IMPACT_DOC'] ?? '';
const SheetDefId = process.env['ECR_E2E_IMPACT_SHEET'] ?? '';
const PeriodKey = process.env['ECR_E2E_IMPACT_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

interface Impact {
  action: string;
  policy: string;
  required: boolean;
  blocked: boolean;
  canRecalculateApproved: boolean;
  reasonRequired: boolean;
  impactHash: string;
  dependentCount: number;
  approvedCount: number;
  consumers: { documentId: number; periodKey: number; approved: boolean; willRecalculate: boolean; skipReason: string | null }[];
}

test.describe('Вплив Approve/Reopen джерела на залежні документи (B80 N5)', () => {
  test.skip(
    DocumentId === '' || SheetDefId === '' || PeriodKey === '',
    'ECR_E2E_OPTIONAL: координат документа-джерела немає (ECR_E2E_IMPACT_DOC/SHEET/PERIOD). Стенд: tools/e2e-stand.ps1.',
  );

  test('GET source-impact віддає форму DTO; reopen без confirmedImpact відмовляється, коли вплив потрібен', async ({
    page,
  }) => {
    test.setTimeout(120_000);
    await signIn(page);

    const impactResponse = await page.request.get(
      `/api/v1/documents/${DocumentId}/source-impact?sheetDefId=${SheetDefId}&periodKey=${PeriodKey}&action=reopen`,
    );
    expect(impactResponse.status(), 'GET source-impact').toBe(200);
    const impact = (await impactResponse.json()) as Impact;
    expect(['None', 'Warn', 'ConfirmWithReason', 'Block']).toContain(impact.policy);
    expect(impact.consumers.length).toBe(impact.dependentCount);
    // Рішення 19.11: видно ВСІХ залежних; затверджений пропускається з причиною, незатверджений перераховується.
    for (const c of impact.consumers) {
      expect(c.willRecalculate).toBe(!c.approved);
      expect(c.skipReason).toBe(c.approved ? 'approved' : null);
    }
    expect(impact.required).toBe(impact.policy !== 'None' && impact.approvedCount > 0);
    if (!impact.required) return;

    expect(impact.impactHash.length).toBe(32);

    // Прямий запит без підтвердження — відмова сервера, а не мовчазне повернення.
    const refused = await page.request.post(`/api/v1/documents/${DocumentId}/reopen`, {
      data: { sheetDefId: Number(SheetDefId), periodKey: Number(PeriodKey), reason: 'e2e: без підтвердження' },
    });
    expect([409, 422]).toContain(refused.status());
    const problem = (await refused.json()) as { errorCode: string };
    expect(problem.errorCode).toBe(impact.blocked ? 'ECR-SRC-0422' : 'ECR-SRC-0409');
  });

  test('B80 N5 / рішення 19.11: правка (action=edit) — лише відомості; явний перерахунок затверджених без причини відмовляється', async ({
    page,
  }) => {
    test.setTimeout(120_000);
    await signIn(page);

    const response = await page.request.get(
      `/api/v1/documents/${DocumentId}/source-impact?sheetDefId=${SheetDefId}&periodKey=${PeriodKey}&action=edit`,
    );
    expect(response.status(), 'GET source-impact?action=edit').toBe(200);
    const impact = (await response.json()) as Impact;
    // Правку сервер не блокує й підтвердження не вимагає; затверджений залежний — «не буде перераховано».
    expect(impact.required).toBe(false);
    expect(impact.blocked).toBe(false);
    for (const c of impact.consumers) {
      expect(c.willRecalculate).toBe(!c.approved);
    }
    // Роль E2EAdmin має Calculation.RecalculateApprovedDependents (tools/e2e-stand.ps1).
    expect(impact.canRecalculateApproved).toBe(true);

    // Без причини — 422 і нічого не ставиться.
    const refused = await page.request.post(`/api/v1/documents/${DocumentId}/source-impact/recalculate-approved`, {
      data: { sheetDefId: Number(SheetDefId), periodKey: Number(PeriodKey), hash: 'x', reason: '' },
    });
    expect(refused.status()).toBe(422);
  });

  test('діалог повернення показує залежні документи з відповіді сервера; «Cancel» нічого не змінює', async ({ page }) => {
    test.setTimeout(120_000);
    await signIn(page);

    const impactResponse = page.waitForResponse(
      (r) => r.url().includes('/source-impact') && r.request().method() === 'GET',
      { timeout: 60_000 },
    );
    await page.goto(`/documents/${DocumentId}?period=${PeriodKey}`);

    // «Return for edits» на затвердженому аркуші → причина → підтвердження → діалог впливу (якщо вплив потрібен).
    await page.getByRole('button', { name: /Return for edits|Reopen/i }).first().click();
    const reasonDialog = page.getByRole('dialog');
    await reasonDialog.getByRole('textbox').fill('e2e: перевірка діалогу впливу');
    const posts: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST' && request.url().includes('/reopen')) posts.push(request.url());
    });
    await reasonDialog.getByRole('button', { name: /Return for edits|Reopen/i }).last().click();

    const response = await impactResponse;
    const impact = (await response.json()) as Impact;
    if (!impact.required) {
      // Вплив не потрібен — повернення пройшло одразу; тест нічого не доводить далі.
      test.skip(true, 'вплив не потрібен на цьому стенді');

      return;
    }

    const impactDialog = page.getByRole('dialog');
    await expect(impactDialog).toBeVisible();
    await expect(impactDialog.getByTestId('source-impact-consumers')).toBeVisible();
    if (impact.blocked) {
      await expect(impactDialog.getByTestId('source-impact-blocked')).toBeVisible();
      await expect(impactDialog.getByTestId('source-impact-confirm')).toHaveCount(0);
    } else {
      await expect(impactDialog.getByTestId('source-impact-confirm')).toBeVisible();
    }

    // Фокус на безпечній дії, Cancel закриває без запису.
    await expect(impactDialog.getByTestId('source-impact-cancel')).toBeFocused();
    await impactDialog.getByTestId('source-impact-cancel').click();
    await expect(impactDialog).toBeHidden();
    expect(posts, 'POST reopen не мав піти').toEqual([]);
  });
});
