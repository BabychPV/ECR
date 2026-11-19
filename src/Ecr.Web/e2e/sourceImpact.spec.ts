import { expect, test, type Locator, type Page, type Request } from '@playwright/test';
import {
  bindingSqlAvailable,
  createImpactWorld,
  fetchImpact,
  sheetStatus,
  signIn,
  type DocRef,
  type ImpactWorld,
  type Policy,
} from './sourceImpactWorld';

/**
 * `B80 N5` (D-554; рішення людини 19.11): вплив повернення затвердженого документа-джерела на залежні документи на
 * живому стенді.
 *
 * Спека САМОДОСТАТНЯ (`sourceImpactWorld.ts`): кожен сценарій сам заводить через API довідник, шаблон-джерело (таблиця
 * потоку складу HSE400), шаблон-споживач із формулою, що читає довідник, проєкти, документи, подання й затвердження, а
 * зв'язку джерела з потрібною політикою кладе `tools/e2e/source-impact-binding.sql` (API її не створює; змінні
 * `ECR_E2E_SQL_*` виставляє `tools/e2e-stand.ps1`). Продуктових тестових ендпоінтів немає.
 *
 * Що доводиться тут, а не у vitest: справжній `GET /api/v1/documents/{id}/source-impact` віддає ТОЧНІ координати
 * залежних (документ, проєкт, шаблон, період, «буде / не буде перераховано»); діалог впливу за політикою зв'язки —
 * `Warn` (підтвердження), `ConfirmWithReason` (причина обов'язкова), `Block` (кнопки немає, сервер відмовляє);
 * адміністратор із галочкою «й затверджені» мусить дати причину, і після дії затверджений залежний перераховано
 * (операція `RecalculatedApproved` у журналі аркуша).
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `-Grep 'B80 N5'`.
 */
const ReopenButton = /Return for edits|Reopen/i;

test.describe('B80 N5: вплив повернення джерела на залежні документи', () => {
  test.skip(
    !bindingSqlAvailable(),
    'ECR_E2E_OPTIONAL: немає ECR_E2E_SQL_SERVER/ECR_E2E_SQL_DATABASE — зв’язку джерела (API її не створює) нічим вставити. Стенд: tools/e2e-stand.ps1.',
  );
  test.describe.configure({ timeout: 300_000 });

  /** Відкриває документ-джерело, веде «Return for edits» до діалогу впливу й повертає діалог. */
  async function openImpactDialog(page: Page, world: ImpactWorld): Promise<Locator> {
    await page.goto(`/documents/${String(world.source.documentId)}?periodKey=${String(world.periodKey)}&sheet=${world.source.sheetCode}`);
    await page.getByRole('button', { name: ReopenButton }).first().click();
    const reasonDialog = page.getByRole('dialog');
    await reasonDialog.getByRole('textbox').fill('e2e: Виправлення складу газу');
    await reasonDialog.getByRole('button', { name: ReopenButton }).last().click();

    const dialog = page.getByRole('dialog').filter({ has: page.getByTestId('source-impact-cancel') });
    await expect(dialog, 'діалог впливу не з’явився').toBeVisible({ timeout: 30_000 });

    return dialog;
  }

  function trackReopen(page: Page): { posts: Request[] } {
    const posts: Request[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST' && /\/documents\/\d+\/reopen/.test(request.url())) posts.push(request);
    });

    return { posts };
  }

  function row(dialog: Locator, doc: DocRef): Locator {
    return dialog.getByTestId('source-impact-consumers').locator('li').filter({ hasText: doc.documentKey }).filter({ hasText: doc.projectCode });
  }

  async function expectCoordinates(dialog: Locator, world: ImpactWorld): Promise<void> {
    await expect(dialog.getByTestId('source-impact-consumers').locator('li')).toHaveCount(2);
    await expect(row(dialog, world.approvedConsumer)).toHaveCount(1);
    await expect(row(dialog, world.approvedConsumer)).toContainText(/will NOT be recalculated/i);
    await expect(row(dialog, world.pendingConsumer)).toHaveCount(1);
    await expect(row(dialog, world.pendingConsumer)).toContainText(/will be recalculated/i);
    await expect(row(dialog, world.pendingConsumer)).not.toContainText(/NOT/);
  }

  async function arrange(page: Page, policy: Policy): Promise<ImpactWorld> {
    await signIn(page);

    return createImpactWorld(page, policy);
  }

  test('точні координати залежних із відповіді сервера; правка й затвердження джерела залежних не зачіпають', async ({ page }) => {
    const world = await arrange(page, 'Warn');

    const impact = await fetchImpact(page, world, 'reopen');
    expect(impact.policy).toBe('Warn');
    expect(impact.required).toBe(true);
    expect(impact.blocked).toBe(false);
    expect(impact.reasonRequired).toBe(false);
    expect(impact.canRecalculateApproved).toBe(true);
    expect(impact.sourceMonths).toEqual([world.periodKey]);
    expect(impact.dependentCount).toBe(2);
    expect(impact.approvedCount).toBe(1);
    expect(impact.impactHash).toHaveLength(32);

    const byDocument = new Map(impact.consumers.map((c) => [c.documentId, c]));
    const approved = byDocument.get(world.approvedConsumer.documentId);
    const pending = byDocument.get(world.pendingConsumer.documentId);
    expect(approved, 'затверджений залежний відсутній у відповіді').toBeDefined();
    expect(pending, 'незатверджений залежний відсутній у відповіді').toBeDefined();
    expect(approved).toMatchObject({
      bindingCode: world.bindingCode,
      templateCode: world.consumerTemplateCode,
      projectCode: world.approvedConsumer.projectCode,
      documentKey: world.approvedConsumer.documentKey,
      periodKey: world.periodKey,
      approved: true,
      willRecalculate: false,
      skipReason: 'approved',
    });
    expect(pending).toMatchObject({
      bindingCode: world.bindingCode,
      templateCode: world.consumerTemplateCode,
      projectCode: world.pendingConsumer.projectCode,
      documentKey: world.pendingConsumer.documentKey,
      periodKey: world.periodKey,
      approved: false,
      willRecalculate: true,
      skipReason: null,
    });

    // Джерело вже затверджене: «approve» нічого не змінює; правка (action=edit) за `approvedOnly` залежних не бачить.
    const approve = await fetchImpact(page, world, 'approve');
    expect(approve.required).toBe(false);
    expect(approve.dependentCount).toBe(0);
    const edit = await fetchImpact(page, world, 'edit');
    expect(edit.required).toBe(false);
    expect(edit.blocked).toBe(false);
    expect(edit.dependentCount).toBe(0);

    // Явний перерахунок затверджених без причини відмовляється, нічого не ставиться.
    const refused = await page.request.post(`/api/v1/documents/${String(world.source.documentId)}/source-impact/recalculate-approved`, {
      data: { sheetDefId: world.source.sheetDefId, periodKey: world.periodKey, hash: impact.impactHash, reason: '' },
    });
    expect(refused.status()).toBe(422);
  });

  test('Warn: діалог показує координати, підтвердження без причини; Cancel нічого не змінює, «Return anyway» повертає джерело', async ({ page }) => {
    const world = await arrange(page, 'Warn');
    const { posts } = trackReopen(page);

    const dialog = await openImpactDialog(page, world);
    await expectCoordinates(dialog, world);
    await expect(dialog.getByTestId('source-impact-policy-warn')).toBeVisible();
    await expect(dialog.getByTestId('source-impact-reason')).toHaveCount(0);
    await expect(dialog.getByTestId('source-impact-blocked')).toHaveCount(0);
    // Фокус на безпечній дії.
    await expect(dialog.getByTestId('source-impact-cancel')).toBeFocused();

    await dialog.getByTestId('source-impact-cancel').click();
    await expect(dialog).toBeHidden();
    expect(posts, 'Cancel не мав надсилати POST reopen').toEqual([]);
    expect(await sheetStatus(page, world.source, world.periodKey)).toBe('Approved');

    // Тепер підтверджуємо: у запиті відбиток, який бачила людина, причини немає.
    const again = await openImpactDialog(page, world);
    const confirm = again.getByTestId('source-impact-confirm');
    await expect(confirm).toBeEnabled();
    const reopened = page.waitForResponse((r) => /\/documents\/\d+\/reopen/.test(r.url()) && r.request().method() === 'POST');
    await confirm.click();
    const response = await reopened;
    expect(response.status()).toBe(204);
    const body = response.request().postDataJSON() as { confirmedImpact: { hash: string; reason: string | null } };
    expect(body.confirmedImpact.hash).toHaveLength(32);
    expect(body.confirmedImpact.reason).toBeNull();

    await expect.poll(() => sheetStatus(page, world.source, world.periodKey), { timeout: 30_000 }).not.toBe('Approved');
    // Без галочки затверджений залежний не чіпається.
    expect(await sheetStatus(page, world.approvedConsumer, world.periodKey)).toBe('Approved');
  });

  test('ConfirmWithReason: без причини підтвердити не можна, причина йде на сервер', async ({ page }) => {
    const world = await arrange(page, 'ConfirmWithReason');

    const dialog = await openImpactDialog(page, world);
    await expectCoordinates(dialog, world);
    await expect(dialog.getByTestId('source-impact-policy-confirmWithReason')).toBeVisible();
    const confirm = dialog.getByTestId('source-impact-confirm');
    const reason = dialog.getByTestId('source-impact-reason');
    await expect(reason).toBeVisible();
    await expect(confirm, 'без причини підтвердження має бути вимкнене').toBeDisabled();

    await reason.fill('   ');
    await expect(confirm, 'пробіли — не причина').toBeDisabled();
    await reason.fill('Виправлення складу газу за актом 12');
    await expect(confirm).toBeEnabled();

    const reopened = page.waitForResponse((r) => /\/documents\/\d+\/reopen/.test(r.url()) && r.request().method() === 'POST');
    await confirm.click();
    const response = await reopened;
    expect(response.status()).toBe(204);
    const body = response.request().postDataJSON() as { confirmedImpact: { reason: string | null } };
    expect(body.confirmedImpact.reason).toBe('Виправлення складу газу за актом 12');
    await expect.poll(() => sheetStatus(page, world.source, world.periodKey), { timeout: 30_000 }).not.toBe('Approved');
  });

  test('Block: діалог без кнопки підтвердження, сервер відмовляє навіть напряму, джерело лишається затвердженим', async ({ page }) => {
    const world = await arrange(page, 'Block');
    const { posts } = trackReopen(page);

    const impact = await fetchImpact(page, world, 'reopen');
    expect(impact.policy).toBe('Block');
    expect(impact.blocked).toBe(true);

    const dialog = await openImpactDialog(page, world);
    await expectCoordinates(dialog, world);
    await expect(dialog.getByTestId('source-impact-blocked')).toBeVisible();
    await expect(dialog.getByTestId('source-impact-confirm'), 'за Block кнопки підтвердження немає').toHaveCount(0);
    await expect(dialog.getByTestId('source-impact-reason')).toHaveCount(0);
    await expect(dialog.getByTestId('source-impact-approved'), 'за Block галочки «й затверджені» немає').toHaveCount(0);

    await dialog.getByTestId('source-impact-cancel').click();
    await expect(dialog).toBeHidden();
    expect(posts, 'POST reopen не мав піти').toEqual([]);

    // Прямий запит навіть із відбитком і причиною — відмова сервера.
    const refused = await page.request.post(`/api/v1/documents/${String(world.source.documentId)}/reopen`, {
      data: {
        sheetDefId: world.source.sheetDefId,
        periodKey: world.periodKey,
        reason: 'e2e: напряму',
        confirmedImpact: { hash: impact.impactHash, reason: 'дуже треба' },
      },
    });
    expect(refused.status()).toBe(422);
    expect(((await refused.json()) as { errorCode: string }).errorCode).toBe('ECR-SRC-0422');
    expect(await sheetStatus(page, world.source, world.periodKey)).toBe('Approved');
  });

  test('адміністратор: галочка «й затверджені» вимагає причину, після дії затверджений залежний перераховано (журнал аркуша)', async ({ page }) => {
    const world = await arrange(page, 'Warn');

    const dialog = await openImpactDialog(page, world);
    const confirm = dialog.getByTestId('source-impact-confirm');
    const checkbox = dialog.getByRole('checkbox', { name: /Also recalculate approved/i });
    await expect(checkbox, 'адміністратор має бачити галочку «й затверджені»').toBeVisible();
    await expect(dialog.getByTestId('source-impact-reason')).toHaveCount(0);
    await expect(confirm).toBeEnabled();

    await checkbox.check();
    const reason = dialog.getByTestId('source-impact-reason');
    await expect(reason, 'із галочкою причина обов’язкова').toBeVisible();
    await expect(confirm).toBeDisabled();
    await reason.fill('Перерахувати затверджений Land після виправлення складу');
    await expect(confirm).toBeEnabled();

    const reopened = page.waitForResponse((r) => /\/documents\/\d+\/reopen/.test(r.url()) && r.request().method() === 'POST');
    await confirm.click();
    const response = await reopened;
    expect(response.status()).toBe(204);
    const body = response.request().postDataJSON() as {
      confirmedImpact: { reason: string | null; recalculateApproved?: boolean };
    };
    expect(body.confirmedImpact.recalculateApproved).toBe(true);
    expect(body.confirmedImpact.reason).toBe('Перерахувати затверджений Land після виправлення складу');

    // Перерахунок затвердженого — задача фону: у журналі структурних змін (`SheetRecalc`) на аркуш затвердженого залежного
    // з'являється `RecalculatedApproved` із причиною адміністратора.
    const consumer = world.approvedConsumer;
    const from = new Date(Date.now() - 2 * 3_600_000).toISOString();
    const to = new Date(Date.now() + 2 * 3_600_000).toISOString();
    await expect
      .poll(
        async () => {
          const result = await page.request.get(
            `/api/v1/audit/structure?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&entityType=SheetRecalc&limit=200`,
          );
          const text = result.ok() ? JSON.stringify(await result.json()) : `HTTP ${String(result.status())}`;
          const mine = text.includes(`documentId\\":${String(consumer.documentId)},`);
          const found = mine && text.includes('RecalculatedApproved') && text.includes('Перерахувати затверджений Land після виправлення складу');

          return found ? 'RecalculatedApproved' : `немає запису для документа ${String(consumer.documentId)}: ${text.slice(0, 1500)}`;
        },
        { message: 'у журналі змін немає RecalculatedApproved для затвердженого залежного', timeout: 120_000, intervals: [2_000, 5_000] },
      )
      .toBe('RecalculatedApproved');
    // Сам статус залишається затвердженим: перераховано дані, а не повернено в роботу.
    expect(await sheetStatus(page, consumer, world.periodKey)).toBe('Approved');
  });
});
