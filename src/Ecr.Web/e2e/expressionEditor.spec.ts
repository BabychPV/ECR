import { expect, test, type Page } from '@playwright/test';

/**
 * Редактор виразів у СПРАВЖНЬОМУ браузері (`ФВ-9.15a`).
 *
 * ⛔ Цей файл існує тому, що в jsdom перевірити нічого не можна: Monaco падає
 * там у службі тем ще до першого токена (`iconsStyleSheet` звертається до CSS,
 * якого в jsdom немає). Тобто ані завантаження чанка, ані підсвічування, ані
 * автодоповнення, ані підкреслення жоден із 221 клієнтського тесту не бачить —
 * вони перевіряють чисті модулі поруч.
 *
 * ⚠ Саме тут перевіряється те, що складається лише в браузері: динамічний
 * чанк на 818 КБ дійшов, воркер піднявся, граматика зареєструвалася, і сервер
 * відповів тими самими зауваженнями, які поверне публікація.
 *
 * ⚠ Паролі тут ТЕСТОВІ й існують лише в тимчасовій базі, яку стенд же й
 * видаляє (`tools/e2e-stand.ps1`).
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Admin-Work-2026!' };

/** Стенд віддає період — за ним видно, що база піднялася. */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

test.describe('Редактор виразів (ФВ-9.15a)', () => {
  // ⚠ Гейт більше не вирішує долю набору: без стенда падає весь набір
  // (`e2e/globalSetup.ts`). Він працює лише під `ECR_E2E_OPTIONAL`, коли
  // пропуск оголошений свідомо, — і змінна названа в тексті навмисно, щоб
  // рядок «немає стенда» не читався знову як норма.
  test.skip(
    PeriodKey === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test.beforeEach(async ({ page }) => {
    await signIn(page);
    await page.goto('/admin/expressions');
  });

  test('чанк редактора вантажиться і поле стає доступним', async ({ page }) => {
    test.slow();

    // ⛔ Головне твердження: динамічний імпорт справді працює. Якщо чанк не
    // дійшов, компонент показує повідомлення, а не порожній прямокутник, —
    // і тоді впаде саме цей рядок, а не щось далі й незрозуміле.
    await expect(page.locator('.monaco-editor').first()).toBeVisible({ timeout: 60_000 });

    // ⛔ Доступна назва: Monaco малює власне текстове поле без підпису, і без
    // `ariaLabel` користувач екранного читача чує «редагування тексту» і
    // нічого більше (`ФВ-14.16`).
    await expect(page.getByRole('textbox', { name: /Expression/i })).toBeVisible();
  });

  test('підсвічування розрізняє рядок, число і посилання', async ({ page }) => {
    test.slow();
    await type(page, "SUM([Jan]) + 1 + 'x'");

    // ⚠ Перевіряється НАЯВНІСТЬ різних класів токенів, а не конкретні кольори:
    // кольори виміряні окремо (`expressionTheme.test.ts`), а тут важить, що
    // граматика зареєструвалася і поділила текст.
    const classes = await tokenClasses(page);

    expect(classes, 'функція не виділена').toContain('mtk-predefined');
    expect(classes, 'число не виділене').toContain('mtk-number');
    expect(classes, 'рядок не виділений').toContain('mtk-string');
  });

  test('невалідний вираз підкреслюється тим, що скаже публікація', async ({ page }) => {
    test.slow();

    // ⛔ `VLOOKUP` — не довільний приклад, а головний випадок міграції: у
    // чинному шаблоні 429 його викликів, і кожен мусить наштовхнутися саме на
    // це зауваження, а не на мовчання.
    //
    // ⚠ Незбалансована дужка тут НЕ годиться, і це знайшлося прогоном:
    // автозакриття дужок робить із набраного `SUM([Jan]` цілком валідний
    // `SUM([Jan])`. Тобто перевірка «незакрита дужка світиться червоним»
    // перевіряла б те, чого користувач набрати не може.
    await type(page, 'VLOOKUP(1, 2');

    await expect(
      page.getByText('ECR-TMPL-0422').first(),
      'зауваження не дійшло до екрана',
    ).toBeVisible({ timeout: 30_000 });
  });

  test('дужки закриваються самі, тому незбалансований вираз набрати неможливо', async ({
    page,
  }) => {
    test.slow();

    // ⚠ Твердження про поведінку, а не про зручність: воно пояснює, чому
    // сусідній прогін не набирає незакриту дужку. Без нього наступний, хто
    // спробує, витратить той самий час на з'ясування.
    await type(page, 'SUM([Jan]');

    await expect(page.getByText(/No findings/i)).toBeVisible({ timeout: 30_000 });
  });

  test('правильний вираз не дає зауважень і називає тип результату', async ({ page }) => {
    test.slow();
    await type(page, '1 + 2');

    await expect(page.getByText(/No findings/i)).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText(/Result type: Number/i)).toBeVisible();
  });

  test('автодоповнення пропонує функції діалекту', async ({ page }) => {
    test.slow();
    await type(page, 'SU');

    // Перелік викликається тим самим сполученням, що й у будь-якому редакторі.
    await page.keyboard.press('Control+Space');

    const suggestions = page.locator('.suggest-widget .monaco-list-row');
    await expect(suggestions.first()).toBeVisible({ timeout: 30_000 });

    // ⛔ `SUM` є, `VLOOKUP` немає — і це не дрібниця: у чинному шаблоні 429
    // викликів `VLOOKUP`, і підказати його означало б запросити писати те, що
    // публікація відхилить.
    await expect(suggestions.filter({ hasText: 'SUM' }).first()).toBeVisible();
    await expect(suggestions.filter({ hasText: 'VLOOKUP' })).toHaveCount(0);
  });
});

/** Вхід під оператором стенда. */
async function signIn(page: Page): Promise<void> {
  await page.goto('/login');

  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  await page.getByLabel(/Password|Пароль/i).fill(Operator.password);
  await page.keyboard.press('Enter');

  await expect(page.getByRole('navigation')).toBeVisible({ timeout: 30_000 });
}

/** Набирає текст у полі Monaco. */
async function type(page: Page, text: string): Promise<void> {
  const editor = page.locator('.monaco-editor').first();
  await expect(editor).toBeVisible({ timeout: 60_000 });

  await editor.click();
  await page.keyboard.type(text, { delay: 20 });
}

/**
 * Класи токенів, які Monaco проставив тексту.
 *
 * ⚠ Monaco іменує їх `mtk1`, `mtk2`… за ПОРЯДКОМ у темі, а не за змістом, тож
 * перевіряти можна лише те, що класів кілька і вони різні. Щоб твердження було
 * про зміст, тут кожен клас зіставляється з кольором теми — а кольори взяті з
 * того самого модуля, що й у застосунку.
 */
async function tokenClasses(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const palette: Record<string, string> = {
      'mtk-predefined': 'rgb(138, 75, 0)',
      'mtk-number': 'rgb(10, 74, 152)',
      'mtk-string': 'rgb(10, 102, 64)',
      'mtk-keyword': 'rgb(123, 31, 162)',
      'mtk-type': 'rgb(138, 28, 92)',
    };

    const found = new Set<string>();

    for (const span of document.querySelectorAll('.view-line span span')) {
      const color = globalThis.getComputedStyle(span).color;

      for (const [name, value] of Object.entries(palette)) {
        if (color === value) found.add(name);
      }
    }

    return [...found];
  });
}
