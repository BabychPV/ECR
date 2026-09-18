import { expect, test, type Page } from '@playwright/test';

/**
 * Сітки аркуша монтуються за прокруткою, а не всі одразу.
 *
 * ⛔ Навіщо ЩЕ ОДИН прогін, якщо є компонентний тест
 * (`src/features/grid/__tests__/SheetTables.lazy.test.tsx`). Той доводить
 * РІШЕННЯ: кому монтуватися, коли спостерігач повідомив про появу. Він не може
 * довести головного — що спостерігач повідомляє про одну-дві таблиці, а не про
 * всі 91 одразу, бо це залежить від РОЗКЛАДКИ, а в jsdom розкладки немає
 * зовсім (`getBoundingClientRect` там завжди нулі). Саме тут і ховається
 * найпоширеніша помилка цього патерну: заглушки нульової висоти складаються в
 * один екран, і лінива сторінка веде себе як нелінива, не подаючи знаку.
 *
 * ⛔ Тому все, що нижче, міряється в справжньому браузері на справжньому
 * документі стенда (91 таблиця на аркуші) і друкується числами: скільки сіток
 * змонтовано одразу, скільки пішло запитів зрізу, за скільки з'явилася перша
 * сітка і що дає прокрутка. Прогін падає, якщо монтується більше, ніж уміщує
 * екран із запасом, — тобто якщо лінивість зникла.
 *
 * ⚠ Стан аркуша (Draft / Approved) на це не впливає: він вирішує, чи можна
 * редагувати, а не скільки сіток монтується. Тому цей прогін нічого не
 * готує й нічого не псує наступним.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Admin-Work-2026!' };

/** Період і документ приходять зі стенда: зашите число ламалося б у січні. */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

/**
 * Стеля «змонтовано одразу».
 *
 * ⛔ Не «менше за 91», а конкретне число з запасом. Екран 720 px при слоті
 * 70vh (≈504 px) плюс запас спостерігача 200 px згори й знизу вміщує два-три
 * слоти; вісім — це втричі більше, тобто межа ловить поламану лінивість і не
 * чіпляється до розкладки, що трохи змінилася.
 */
const MountedAtOpenCeiling = 8;

/** Скільки таблиць має бути на аркуші стенда (`DistributionProfile`: 90 + гейтова). */
const ExpectedSlots = 91;

test.describe('Ліниве монтування таблиць аркуша', () => {
  // ⚠ Гейт більше не вирішує долю набору: без стенда падає весь набір
  // (`e2e/globalSetup.ts`). Він працює лише під `ECR_E2E_OPTIONAL`, коли
  // пропуск оголошений свідомо.
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('сітки монтуються за прокруткою, а не всі 91 одразу', async ({ page }) => {
    test.slow();

    /*
     * ⛔ Запити зрізу рахуються з МЕРЕЖІ, а не з DOM: саме вони — справжня
     * ціна нелінивої сторінки. `GET /documents/{id}/tables/{id}` —
     * найважчий регулярний запит системи (`GetTableSliceHandler`, бюджет p95
     * 1.5 с на 500×60), і 91 такий на одне відкриття документа не бачить
     * жоден компонентний тест.
     *
     * ⚠ Регулярка з `$`-якорем відсікає перелік таблиць
     * (`/documents/{id}/tables?periodKey=…`) — інший ендпоінт, і він тут
     * якраз законний.
     */
    const sliceRequests: string[] = [];
    page.on('request', (request) => {
      if (/\/api\/v1\/documents\/\d+\/tables\/\d+$/.test(new URL(request.url()).pathname)) {
        sliceRequests.push(request.url());
      }
    });

    await signIn(page, Operator.user, Operator.password);

    const startedAt = Date.now();
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);

    await expect(page.getByRole('heading').first(), 'документ не відрендерився').toBeVisible({
      timeout: 30_000,
    });

    // ⚠ Заголовки таблиць видно ДО будь-якої сітки: без них прокрутка
    // сторінкою заглушок безглузда — не видно, повз що їдеш.
    const slots = page.locator('[data-table-slot]');
    await expect
      .poll(() => slots.count(), { timeout: 30_000, message: 'слоти таблиць не з’явилися' })
      .toBe(ExpectedSlots);

    const firstGrid = page.locator('revo-grid').first();
    await expect(firstGrid, 'перша сітка не з’явилася').toBeVisible({ timeout: 60_000 });
    const firstGridMs = Date.now() - startedAt;

    // ⚠ Пауза — це ЗАМІР, а не милиця таймауту: вона дає ланцюгу монтувань
    // (якби він був) час розгорнутися. Що менша пауза, то легше пройшов би
    // зламаний варіант; дві секунди тут працюють проти зеленого, а не за нього.
    await page.waitForTimeout(2_000);

    const mountedAtOpen = await page.locator('[data-table-mounted="true"]').count();
    const slicesAtOpen = sliceRequests.length;

    console.log(
      `[lazy] одразу після відкриття: слотів ${String(ExpectedSlots)}, ` +
        `змонтовано сіток ${String(mountedAtOpen)}, ` +
        `запитів зрізу ${String(slicesAtOpen)}, ` +
        `перша сітка за ${String(firstGridMs)} мс`,
    );

    expect(
      mountedAtOpen,
      `змонтовано ${String(mountedAtOpen)} сіток із ${String(ExpectedSlots)} — лінивість не діє`,
    ).toBeLessThanOrEqual(MountedAtOpenCeiling);

    // ⛔ Запитів зрізу рівно стільки ж, скільки змонтованих сіток: кожна
    // йде по свій зріз сама. Розбіжність означала б, що зріз тягне хтось
    // іще — і тоді число «змонтовано» перестає бути мірою ціни.
    expect(slicesAtOpen, 'запитів зрізу більше, ніж змонтованих сіток').toBeLessThanOrEqual(
      mountedAtOpen,
    );

    // ── Прокрутка: сітки з'являються далі ────────────────────────────────
    const deepIndex = 40;
    const deepSlot = slots.nth(deepIndex);
    await deepSlot.scrollIntoViewIfNeeded();

    await expect
      .poll(() => deepSlot.getAttribute('data-table-mounted'), {
        timeout: 30_000,
        message: `слот ${String(deepIndex)} не змонтувався після прокрутки до нього`,
      })
      .toBe('true');

    await page.waitForTimeout(2_000);

    const mountedAfterScroll = await page.locator('[data-table-mounted="true"]').count();
    const slicesAfterScroll = sliceRequests.length;

    console.log(
      `[lazy] після прокрутки до слота ${String(deepIndex)}: ` +
        `змонтовано сіток ${String(mountedAfterScroll)}, ` +
        `запитів зрізу ${String(slicesAfterScroll)}`,
    );

    expect(
      mountedAfterScroll,
      'після прокрутки не додалося жодної сітки — монтування не працює',
    ).toBeGreaterThan(mountedAtOpen);

    // ⛔ І перша сітка НЕ зникла. Розмонтування при прокрутці геть викинуло б
    // незбережені правки, історію Undo/Redo і виділення — мовчки.
    await expect(
      slots.first(),
      'перша таблиця розмонтувалася, коли поїхала з екрана',
    ).toHaveAttribute('data-table-mounted', 'true');

    // ⚠ І при цьому НЕ всі: прокрутка на третину документа не має тягнути
    // хвіст, якого ніхто не бачив.
    expect(
      mountedAfterScroll,
      'після однієї прокрутки змонтувалися всі таблиці — лінивість діє лише на старті',
    ).toBeLessThan(ExpectedSlots);
  });
});

/** Вхід — той самий шлях, що й у справжнього користувача. */
async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });

  await page.getByLabel(/User name|Ім'я/i).fill(user);

  // ⚠ Роль, а не підпис: `getByLabel(/Password|Пароль/i)` резолвиться у ДВА
  // елементи — поле й кнопку-тумблер видимості пароля з `aria-label="Toggle
  // password visibility"` (`LoginPage.tsx`, Q-260).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
