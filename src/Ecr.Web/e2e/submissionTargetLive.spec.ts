import { expect, test, type Browser, type BrowserContext } from '@playwright/test';
import { chooseSubmitPeriod } from './submitPeriod';

/**
 * D-441 (AN-128): «Подати» на РЕАЛЬНОМУ стенді (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `grep=submissionTargetLive`) називає й подає НАЙРАНІШИЙ неподаний період у стані Open/Grace, а не відкритий на
 * екрані; ▾ дає вибрати інший; `Scheduled` сервер не приймає.
 *
 * ⚠ Стенд живе за датами: політика `ECR-Standard` (Grace — з 15-го дня після кінця місяця до 45-го) дає поруч
 * із поточним Open-місяцем попередній у Grace лише частину місяця. Тому очікування не зашиті числами: ціль
 * звіряється з відповіддю сервера (`GET …/submission-target`) і календарем проєкту — найраніший неподаний
 * відкритий період, і він не пізніший за відкритий на екрані.
 *
 * ⚠ Сам клік подання перехоплюється (`page.route`, відповідь 204): прогін доводить, ЩО шле кнопка, і не міняє
 * базову лінію стенда для сусідніх прогонів (подання реального періоду перевіряють `keyboardPath`/`cellColor*`).
 *
 * ⚠ Написано без локального прогону (на машині автора немає стенда): перший живий прогін — CI.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

interface SubmissionTarget {
  targetPeriodKey: number | null;
  nextPeriodKey: number | null;
  periods: { periodKey: number; state: string; sheetStatus: string; submittable: boolean }[];
  monthColumns: { tableDefId: number; columnCode: string; monthNumber: number; periodKey: number; sheetStatus: string }[];
}

/** Тіло перехопленого `POST …/submit`. */
type Body = { sheetDefId?: number; periodKey?: number };

interface Calendar {
  periods: { periodKey: number; state: string }[];
}

async function signedIn(browser: Browser): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: Admin.userName, password: Admin.password },
  });
  expect(login.ok(), `вхід ${Admin.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

test.describe('submissionTarget: «Подати: <період>» на реальному стенді (D-441)', () => {
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.describe.configure({ mode: 'serial', timeout: 180_000 });

  test('кнопка називає й подає найраніший неподаний Open/Grace; вибір іншого періоду; Scheduled — 403', async ({
    browser,
  }) => {
    const context = await signedIn(browser);
    const page = await context.newPage();

    try {
      const targetResponse = page.waitForResponse(
        (response) =>
          response.request().method() === 'GET' &&
          new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/submission-target`,
        { timeout: 90_000 },
      );
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      const reply = await targetResponse;
      expect(reply.status(), `GET submission-target: ${await reply.text()}`).toBe(200);
      const target = (await reply.json()) as SubmissionTarget;

      // ── 1. Ціль — найраніший неподаний відкритий період, не пізніший за відкритий на екрані ──────────
      const submittable = target.periods.filter((period) => period.submittable).map((period) => period.periodKey);
      expect(submittable.length, 'на стенді немає жодного неподаного відкритого періоду').toBeGreaterThan(0);
      expect(target.targetPeriodKey).toBe(submittable[0]);
      expect(target.targetPeriodKey ?? Number.MAX_SAFE_INTEGER).toBeLessThanOrEqual(Number(PeriodKey));
      expect(target.periods.every((period) => period.state === 'Open' || period.state === 'Grace')).toBe(true);

      // Календар проєкту: кожен Open/Grace-період, раніший за ціль, у переліку сервера — уже поданий.
      const summary = await context.request.get(`/api/v1/documents/${DocumentId}?periodKey=${PeriodKey}`);
      const projectId = ((await summary.json()) as { projectId: number }).projectId;
      const calendar = (await (await context.request.get(`/api/v1/projects/${String(projectId)}/periods`)).json()) as Calendar;
      const targetKey = target.targetPeriodKey ?? 0;
      for (const period of target.periods.filter((p) => p.periodKey < targetKey)) {
        expect(period.submittable, `період ${String(period.periodKey)} раніший за ціль і не поданий`).toBe(false);
      }

      // ── 2. Кнопка «Подати: <період>» називає ЦІЛЬ; попередження «наступний — …», коли за нею чекає ще один ──
      const submit = page.getByTestId('document-submit');
      await expect(submit, 'у шапці немає кнопки подання').toBeVisible({ timeout: 30_000 });
      await expect(submit).toHaveAttribute('data-period-key', String(target.targetPeriodKey));
      await expect(submit).toHaveText(/^(Submit|Подать|Тапсыру): .+/);
      await expect(submit).toContainText(String(Math.trunc(targetKey / 100)));
      if (target.nextPeriodKey !== null) {
        await expect(page.getByTestId('submit-next-period'), 'немає попередження «наступний — …»').toBeVisible();
      } else {
        await expect(page.getByTestId('submit-next-period')).toHaveCount(0);
      }

      // ── 3. Клік шле САМЕ ціль (перехоплено: базова лінія стенда не змінюється) ─────────────────────────
      const sent: { body: { sheetDefId?: number; periodKey?: number } | null } = { body: null };
      await page.route(`**/api/v1/documents/${DocumentId}/submit`, async (route) => {
        sent.body = route.request().postDataJSON() as { sheetDefId?: number; periodKey?: number };
        await route.fulfill({ status: 204, body: '' });
      });
      await submit.click();
      await expect.poll(() => sent.body?.periodKey, { message: 'кнопка не надіслала подання', timeout: 30_000 }).toBe(
        target.targetPeriodKey,
      );
      await page.unroute(`**/api/v1/documents/${DocumentId}/submit`);

      // ── 4. Вибір іншого відкритого періоду ▾ переводить кнопку на нього ───────────────────────────────
      const other = submittable.find((key) => key !== target.targetPeriodKey);
      if (other !== undefined) {
        await chooseSubmitPeriod(page, other);
        await expect(submit).toHaveAttribute('data-period-key', String(other));
      }

      // ── 5. Scheduled-період сервер не приймає (EditRules.CanSubmit, D-441) ─────────────────────────────
      const scheduled = calendar.periods.find((period) => period.state === 'Scheduled');
      if (scheduled !== undefined) {
        const tables = (await (
          await context.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`)
        ).json()) as { sheetDefId: number }[];
        const sheetDefId = tables[0]?.sheetDefId;
        if (sheetDefId === undefined) throw new Error('у документа стенда немає таблиць');

        const refused = await context.request.post(`/api/v1/documents/${DocumentId}/submit`, {
          data: { sheetDefId, periodKey: scheduled.periodKey },
        });
        const body = await refused.text();
        expect(refused.status(), `submit Scheduled ${String(scheduled.periodKey)}: ${body}`).toBe(403);
        expect(body).toContain('PeriodNotOpenYet');
      }
    } finally {
      await context.close();
    }
  });

  /*
   * ✎ D-441 CLS: «More» не рухається, коли `submission-target` відповідає ПІСЛЯ першого малюнка рядка дій.
   *
   * ⛔ На старому коді «Validate» і «More» малювалися одразу, а «Подати: <період>» з ▾ і «Next to submit: …» —
   * лише з відповіддю: група дій (притиснута праворуч) розширювалась і переносилась, «More» з'їжджала з ~(1055,138)
   * на ~(861,162) між натиском і відпуском миші (e2e-stand 39994116932, 40023308269). Відповідь тут навмисно
   * притримується (2,5 с), щоб «пізня відповідь» була не випадком таймінгу, а умовою прогону: старий код червоніє
   * детерміновано.
   */
  test('«More» зʼявляється одразу на остаточному місці: пізня відповідь submission-target не зсуває рядок дій', async ({
    browser,
  }) => {
    const context = await signedIn(browser);
    const page = await context.newPage();
    const targetPath = `/api/v1/documents/${DocumentId}/submission-target`;

    try {
      await page.route(
        (url) => url.pathname === targetPath,
        async (route) => {
          await new Promise((resolve) => setTimeout(resolve, 2_500));
          await route.continue();
        },
      );
      const targetResponse = page.waitForResponse(
        (response) => response.request().method() === 'GET' && new URL(response.url()).pathname === targetPath,
        { timeout: 90_000 },
      );

      // Поки відповіді немає — місце дій тримає скелет (не кнопки). ⚠ Стежимо паралельно і звіряємо В КІНЦІ:
      // очікування скелета ПЕРЕД заміром відклало б перший замір «More» за мить зсуву, і старий код пройшов би.
      const skeletonSeen = page
        .getByTestId('document-actions-pending')
        .waitFor({ state: 'visible', timeout: 60_000 })
        .then(
          () => true,
          () => false,
        );

      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);

      const more = page.getByTestId('document-more');
      await expect(more).toBeVisible({ timeout: 60_000 });
      const first = await more.boundingBox();
      if (first === null) throw new Error('«More» видима, але без розмірів');

      expect((await targetResponse).status()).toBe(200);
      // Стенд має неподаний відкритий період (перший тест), тож «Подати» є — рядок дій у повній формі.
      await expect(page.getByTestId('document-submit')).toBeVisible({ timeout: 30_000 });
      // Два кадри й пауза: усе, що відповідь могла домалювати (▾, «Next to submit: …»), уже розкладене.
      await page.evaluate(
        () => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))),
      );
      await page.waitForTimeout(500);

      const settled = await more.boundingBox();
      if (settled === null) throw new Error('«More» зникла після відповіді submission-target');
      expect(
        { x: Math.round(settled.x), y: Math.round(settled.y) },
        `«More» зсунулась: (${first.x.toFixed(0)},${first.y.toFixed(0)}) → (${settled.x.toFixed(0)},${settled.y.toFixed(0)})`,
      ).toEqual({ x: Math.round(first.x), y: Math.round(first.y) });
      expect(await skeletonSeen, 'немає скелета рядка дій до відповіді submission-target').toBe(true);
    } finally {
      await context.close();
    }
  });

  /*
   * ✎ D-441 «миготіння скелета» (HANDOFF-2026-10-24 §4): зміна періоду міняє ключ `submission-target`, і на старому
   * коді ВЕСЬ рядок дій на один запит підмінявся скелетом — «More» зникала й поверталась. Тепер лишається відповідь
   * попереднього ключа: кнопки на місці, а «Подати» недоступна, доки не прийде ціль нового періоду, і клік під час
   * очікування нічого не шле.
   *
   * ⚠ Документ стенда має ОДИН аркуш (`Ecr.DataGen`), тож ключ міняється періодом. Перший захід на період
   * притримується до кінця тесту: зведення й таблиці того періоду вже в кеші (сторінка не провалюється в скелет
   * сторінки), а запит `submission-target` ще в дорозі — рівно стан «перемкнули, відповідь іде». Перемикання —
   * клієнтське (історія браузера), як вибір періоду в шапці: повне перезавантаження скинуло б кеш.
   */
  test('перемкнули період: рядок дій без скелета, «More» на місці, «Подати» вимкнена до відповіді й нічого не шле', async ({
    browser,
  }) => {
    const context = await signedIn(browser);
    const page = await context.newPage();
    const targetPath = `/api/v1/documents/${DocumentId}/submission-target`;

    try {
      // ── Інший період того самого документа: є аркуш, той самий стан аркушів (рядок дій тієї ж форми) ──────
      const summaryOf = async (key: number): Promise<{ projectId: number; sheetStates: Record<string, string> } | null> => {
        const response = await context.request.get(`/api/v1/documents/${DocumentId}?periodKey=${String(key)}`);
        return response.ok() ? ((await response.json()) as { projectId: number; sheetStates: Record<string, string> }) : null;
      };
      const here = await summaryOf(Number(PeriodKey));
      if (here === null) throw new Error(`зведення документа за ${PeriodKey} недоступне`);
      const calendar = (await (await context.request.get(`/api/v1/projects/${String(here.projectId)}/periods`)).json()) as Calendar;
      const candidates = calendar.periods
        .map((period) => period.periodKey)
        .filter((key) => key !== Number(PeriodKey))
        .sort((a, b) => Math.abs(a - Number(PeriodKey)) - Math.abs(b - Number(PeriodKey)));
      let other: number | undefined;
      for (const key of candidates) {
        const summary = await summaryOf(key);
        if (summary === null || JSON.stringify(summary.sheetStates) !== JSON.stringify(here.sheetStates)) continue;
        const tables = await context.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${String(key)}`);
        if (tables.ok() && ((await tables.json()) as unknown[]).length > 0) {
          other = key;
          break;
        }
      }
      if (other === undefined) throw new Error('у календарі стенда немає другого періоду з тим самим станом аркушів');
      const otherKey = String(other);

      // ── Відповідь submission-target за інший період тримається, доки тест не відпустить ─────────────────
      let releaseOther: () => void = () => undefined;
      const otherHeld = new Promise<void>((resolve) => {
        releaseOther = resolve;
      });
      let otherAsked = 0;
      await page.route(
        (url) => url.pathname === targetPath && url.searchParams.get('periodKey') === otherKey,
        async (route) => {
          otherAsked += 1;
          await otherHeld;
          await route.continue();
        },
      );
      const sent: Body[] = [];
      await page.route(`**/api/v1/documents/${DocumentId}/submit`, async (route) => {
        sent.push(route.request().postDataJSON() as Body);
        await route.fulfill({ status: 204, body: '' });
      });

      // 1. Інший період: зведення й таблиці стають у кеш, ціль — у дорозі (притримана).
      await page.goto(`/documents/${DocumentId}?periodKey=${otherKey}`);
      await expect(page.getByTestId('document-toolbar')).toBeVisible({ timeout: 60_000 });
      await expect.poll(() => otherAsked, { message: 'запиту submission-target за інший період немає', timeout: 30_000 }).toBeGreaterThan(0);

      // 2. Клієнтом — на період стенда: ціль приходить, рядок дій остаточний.
      const switchTo = async (key: string): Promise<void> => {
        await page.evaluate((periodKey) => {
          const url = new URL(window.location.href);
          url.searchParams.set('periodKey', periodKey);
          window.history.pushState(window.history.state, '', url);
          window.dispatchEvent(new PopStateEvent('popstate', { state: window.history.state }));
        }, key);
      };
      await switchTo(PeriodKey);
      const submit = page.getByTestId('document-submit');
      const more = page.getByTestId('document-more');
      await expect(submit, 'на періоді стенда немає «Подати»').toHaveAttribute('data-period-key', /\d+/, { timeout: 60_000 });
      await page.evaluate(
        () => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))),
      );
      await page.waitForTimeout(500);
      const before = await more.boundingBox();
      if (before === null) throw new Error('«More» видима, але без розмірів');

      // Кожен кадр: чи з'являвся скелет, чи зникала або рухалась «More» (звіряється наприкінці).
      await page.evaluate(
        ({ x, y }) => {
          const watch = { pending: false, missing: false, shift: 0, stop: false };
          (window as unknown as { __actionsWatch: typeof watch }).__actionsWatch = watch;
          const frame = (): void => {
            if (document.querySelector('[data-testid="document-actions-pending"]') !== null) watch.pending = true;
            const node = document.querySelector('[data-testid="document-more"]');
            if (node === null) watch.missing = true;
            else {
              const rect = node.getBoundingClientRect();
              watch.shift = Math.max(watch.shift, Math.abs(rect.x - x), Math.abs(rect.y - y));
            }
            if (!watch.stop) requestAnimationFrame(frame);
          };
          requestAnimationFrame(frame);
        },
        { x: before.x, y: before.y },
      );

      // 3. Назад на інший період — його ціль ще притримана.
      await switchTo(otherKey);
      await page.waitForTimeout(1_500);

      await expect(page.getByTestId('document-actions-pending'), 'скелет рядка дій під час перемикання').toHaveCount(0);
      await expect(more).toBeVisible();
      await expect(submit).toBeVisible();
      await expect(submit, '«Подати» доступна, поки ціль нового періоду не прийшла').toBeDisabled();
      await expect(submit).toHaveAttribute('aria-disabled', 'true');
      await expect(submit).not.toHaveAttribute('data-period-key', /.*/);
      const during = await more.boundingBox();
      expect(during && { x: Math.round(during.x), y: Math.round(during.y) }, '«More» зсунулась під час перемикання').toEqual({
        x: Math.round(before.x),
        y: Math.round(before.y),
      });

      // ⛔ Клік під час очікування не шле ціль попереднього періоду (і жодну).
      await submit.click({ force: true });
      await page.waitForTimeout(1_000);
      expect(sent, 'клік «Подати» під час перемикання надіслав подання').toEqual([]);

      // 4. Відповідь нового періоду: кнопка доступна і називає ЙОГО ціль.
      const otherResponse = page.waitForResponse(
        (response) =>
          response.request().method() === 'GET' &&
          new URL(response.url()).pathname === targetPath &&
          new URL(response.url()).searchParams.get('periodKey') === otherKey,
        { timeout: 60_000 },
      );
      releaseOther();
      const reply = await otherResponse;
      expect(reply.status(), `GET submission-target ${otherKey}: ${await reply.text()}`).toBe(200);
      const target = (await reply.json()) as SubmissionTarget;

      const seen = await page.evaluate(() => {
        const state = (window as unknown as { __actionsWatch: { pending: boolean; missing: boolean; shift: number; stop: boolean } })
          .__actionsWatch;
        state.stop = true;
        return { pending: state.pending, missing: state.missing, shift: state.shift };
      });
      expect(seen.pending, 'скелет рядка дій з\'являвся під час перемикання періоду').toBe(false);
      expect(seen.missing, '«More» зникала під час перемикання періоду').toBe(false);
      expect(seen.shift, '«More» рухалась під час перемикання періоду (px)').toBeLessThanOrEqual(1);

      if (target.targetPeriodKey !== null) {
        await expect(submit).toHaveAttribute('data-period-key', String(target.targetPeriodKey), { timeout: 30_000 });
        await expect(submit).toBeEnabled();
        await submit.click();
        await expect.poll(() => sent.length, { message: 'кнопка не надіслала подання', timeout: 30_000 }).toBe(1);
        expect(sent[0]?.periodKey).toBe(target.targetPeriodKey);
      } else {
        await expect(submit).toHaveCount(0, { timeout: 30_000 });
      }
    } finally {
      await context.close();
    }
  });
});
