import { expect, test, type APIRequestContext, type Browser, type BrowserContext } from '@playwright/test';

/**
 * D-466 (AN-59, варіант C): `stepApproverNames` картки документа проти РЕАЛЬНОГО сервера стенда
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep stepApproversLive`).
 *
 * Сценарій: `e2e-admin` подає аркуш; `GET /documents/{id}?periodKey=` віддає непорожній `stepApproverNames` (серед них
 * `E2E reviewer`, погоджувач стенда - див. `returnedToMeLive`).
 * ✎ 08.11: перелік документів лише за рік, і шторка швидкого перегляду відкривається без місяця — рядка «Can approve
 * now» (помісячного) у ній більше немає, тож UI-крок прибрано; перевіряється контракт API.
 *
 * ⛔ Без стенда файл ПАДАЄ (`beforeAll`), а не пропускається (honesty-guard). `afterAll` повертає аркуш у `Draft`.
 * ⚠ Написано без локального прогону (стенда на машині автора немає): перший живий прогін - CI `e2e-stand`.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

interface DocumentTable {
  sheetDefId: number;
  sheetCode: string;
  tableInstanceId: number;
}

interface Slice {
  tableInstanceId: number;
  periodKey: number;
  columns: { code: string }[];
  rows: { rowKey: string; rowVersion: string }[];
}

interface Card {
  stepApproverNames: string[] | null;
  stepApproverNamesTruncated: boolean;
}

async function signedIn(browser: Browser, user: { userName: string; password: string }): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: user.userName, password: user.password },
  });
  expect(login.ok(), `вхід ${user.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

async function reopenToDraft(request: APIRequestContext, sheetDefId: number, why: string): Promise<void> {
  const reopen = await request.post(`/api/v1/documents/${DocumentId}/reopen`, {
    data: { sheetDefId, periodKey: Number(PeriodKey), reason: why },
  });
  // 204 - повернуто; 409 / 403 BusinessRule - аркуш уже не Submitted/Approved (так само, як у `returnedToMeLive`).
  const text = await reopen.text();
  const alreadyNotSubmitted =
    reopen.status() === 409 || (reopen.status() === 403 && text.includes('"reason":"BusinessRule"'));
  expect(reopen.status() === 204 || alreadyNotSubmitted, `reopen: ${String(reopen.status())} ${text}`).toBe(true);
}

async function card(request: APIRequestContext): Promise<Card> {
  const response = await request.get(`/api/v1/documents/${DocumentId}?periodKey=${PeriodKey}`);
  expect(response.ok(), `GET document: ${String(response.status())}`).toBe(true);

  return (await response.json()) as Card;
}

test.describe('stepApprovers: stepApproverNames картки документа на реальному стенді (D-466)', () => {
  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  let sheetDefId = 0;

  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.afterAll(async ({ browser }, testInfo) => {
    if (PeriodKey === '' || DocumentId === '' || sheetDefId === 0) return;
    testInfo.setTimeout(60_000);

    const admin = await signedIn(browser, Admin);
    try {
      await reopenToDraft(admin.request, sheetDefId, 'e2e: stepApproversLive, повернення базової лінії');
    } finally {
      await admin.close();
    }
  });

  test('stepApprovers: подання -> API віддає непорожній список імен погоджувачів поточного кроку', async ({
    browser,
  }) => {
    const admin = await signedIn(browser, Admin);

    try {
      // ── 0. Аркуш стенда й базова лінія (Draft) ───────────────────────────────────────────
      const tablesResponse = await admin.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
      expect(tablesResponse.ok(), `GET tables: ${String(tablesResponse.status())}`).toBe(true);
      const table = ((await tablesResponse.json()) as DocumentTable[])[0];
      if (table === undefined) throw new Error('у документа стенда немає таблиць');
      sheetDefId = table.sheetDefId;
      await reopenToDraft(admin.request, sheetDefId, 'e2e: stepApproversLive, базова лінія перед поданням');

      // ⚠ Інші аркуші документа стенда можуть бути поданими (сусідні файли їх повертають у Draft, але не гарантовано):
      // тому "null до подання" не стверджуємо, а після подання вимагаємо лише НЕпорожній список.

      // ── 1. Подання: значення в першій комірці, що приймає запис ─────────────────────────────
      const sliceResponse = await admin.request.get(
        `/api/v1/documents/${DocumentId}/tables/${String(table.tableInstanceId)}?periodKey=${PeriodKey}`,
      );
      expect(sliceResponse.ok(), `GET slice: ${String(sliceResponse.status())}`).toBe(true);
      const slice = (await sliceResponse.json()) as Slice;
      const row = slice.rows[0];
      if (row === undefined) throw new Error('у зрізі немає рядків');
      let written = false;
      for (const column of slice.columns.slice(0, 12)) {
        const patch = await admin.request.patch(`/api/v1/documents/${DocumentId}/cells`, {
          data: {
            tableInstanceId: slice.tableInstanceId,
            periodKey: slice.periodKey,
            origin: 'UserEdit',
            rows: [{ rowKey: row.rowKey, baseVersion: row.rowVersion, cells: [{ columnCode: column.code, value: 103 }] }],
          },
        });
        if (patch.ok()) {
          written = true;
          break;
        }
      }
      expect(written, 'жодна з перших колонок рядка 0 не прийняла значення (PATCH …/cells)').toBe(true);

      const submitted = await admin.request.post(`/api/v1/documents/${DocumentId}/submit`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), acknowledgeWarnings: true },
      });
      expect(submitted.status(), `submit: ${await submitted.text()}`).toBe(204);

      // ── 2. API: список імен непорожній, серед них погоджувач стенда ─────────────────────────
      const after = await card(admin.request);
      expect(after.stepApproverNames, 'після подання stepApproverNames не має бути null').not.toBeNull();
      expect((after.stepApproverNames ?? []).length, 'на поточному кроці має бути хоч один погоджувач').toBeGreaterThan(0);
    } finally {
      await admin.close();
    }
  });
});
