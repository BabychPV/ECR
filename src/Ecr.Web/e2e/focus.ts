import { expect, type Locator, type Page } from '@playwright/test';
import { rawDifference } from './greyscale';

/**
 * Перевірки фокуса для проходу без миші (`ФВ-14.16`, `D-141`).
 *
 * ⛔ «Кільце фокуса видно» — твердження про ПІКСЕЛІ, і перевірити його можна
 * лише в справжньому браузері. `:focus-visible` у jsdom не обчислюється, а
 * наявність класу нічого не доводить: клас може малювати прозоре кільце,
 * кільце того самого кольору, що й тло, або кільце під сусіднім елементом.
 *
 * ⚠ Перевірки розділені на дві навмисно. Дешева (`expectFocusVisible`) не
 * чіпає стану і йде після КОЖНОЇ зміни фокуса; дорога (`expectFocusRing`) сама
 * фокусує елемент і міряє різницю в пікселях. Перша спроба поєднати їх була
 * помилкою: щоб отримати «знімок без фокуса», помічник знімав фокус — тобто
 * ламав саме той стан, який щойно перевірив, і прохід далі йшов не звідти,
 * звідки мав.
 */

/** Де зараз фокус — так, як його бачить читалка. */
export interface FocusState {
  tag: string;
  role: string | null;
  label: string;
  visible: boolean;
}

/** Читає поточний фокус разом із доступним іменем. */
export async function focusState(page: Page): Promise<FocusState> {
  return page.evaluate(() => {
    const active = document.activeElement;
    if (active === null) return { tag: 'null', role: null, label: '', visible: false };

    const rect = active.getBoundingClientRect();

    return {
      tag: active.tagName.toLowerCase(),
      role: active.getAttribute('role'),

      // Ім'я береться так само, як його візьме читалка: `aria-label` або
      // власний текст. Порожнє ім'я — окремий дефект, і він має бути видно.
      label:
        active.getAttribute('aria-label') ??
        (active as HTMLElement).innerText?.trim().slice(0, 60) ??
        '',

      // ⚠ «У полі зору» — не «існує в дереві». Елемент за межами вікна
      // формально сфокусований, і Tab по ньому проходить, але людина не
      // бачить нічого і вважає, що фокус зник.
      visible:
        rect.width > 0 &&
        rect.height > 0 &&
        rect.bottom > 0 &&
        rect.right > 0 &&
        rect.top < window.innerHeight &&
        rect.left < window.innerWidth,
    };
  });
}

/**
 * Фокус не на `body` і сфокусований елемент у полі зору.
 *
 * ⛔ Дві перевірки, а не одна, бо це два різні дефекти:
 *   — фокус на `body` означає, що після дії людина «ніде» і Tab починає з
 *     нуля; так поводиться кожен модальний діалог, який закрили неправильно;
 *   — елемент поза вікном означає, що фокус є і його не видно.
 *
 * ⚠ Помічник НІЧОГО не змінює: його кличуть після кожної зміни фокуса, і
 * побічна дія тут зробила б непередбачуваним увесь прохід.
 *
 * @param page Сторінка.
 * @param where Назва кроку — потрапляє в текст падіння, інакше «фокус на body»
 *   не скаже, після чого саме.
 */
export async function expectFocusVisible(page: Page, where: string): Promise<FocusState> {
  const state = await focusState(page);

  expect(state.tag, `${where}: фокус на <${state.tag}> — людина без миші «ніде»`).not.toBe('body');
  expect(state.tag, `${where}: фокуса немає взагалі`).not.toBe('null');
  expect(state.visible, `${where}: сфокусований елемент поза полем зору`).toBe(true);

  return state;
}

/**
 * Кільце фокуса ВИДНО: знімок елемента до фокусування проти знімка після.
 *
 * ⛔ Це `ФВ-14.16`, записана як предикат про пікселі. Фокус, який є і якого не
 * видно, гірший за його відсутність: людина натискає Enter, не знаючи, на
 * чому саме.
 *
 * ⚠ Помічник САМ фокусує елемент і лишає фокус на ньому — тому виклик стоїть
 * там, де фокус і має опинитися, а не після нього.
 */
export async function expectFocusRing(page: Page, target: Locator, where: string): Promise<void> {
  await expect(target, `${where}: елемента немає`).toBeVisible();

  // ⛔ Знімається не сам елемент, а ОБЛАСТЬ навколо нього з полем у 6 px.
  // `outline` малюється ЗА МЕЖАМИ рамки елемента, тому `locator.screenshot()`
  // його не бачить: у посилання на документ кільце є (`solid 2px`), а вимір
  // давав 0.00 %. Це був би дефект перевірки, який змусив би «полагодити»
  // справний індикатор.
  const box = await target.boundingBox();
  expect(box, `${where}: елемент без геометрії`).not.toBeNull();

  const padding = 6;
  const clip = {
    x: Math.max(0, (box?.x ?? 0) - padding),
    y: Math.max(0, (box?.y ?? 0) - padding),
    width: (box?.width ?? 0) + padding * 2,
    height: (box?.height ?? 0) + padding * 2,
  };

  // Прибираємо фокус із будь-чого перед виміром: якщо елемент уже у фокусі,
  // «до» і «після» збіглися б, і перевірка зеленіла б завжди.
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await settle(page);
  const before = await page.screenshot({ clip });

  await target.focus();
  await expectFocusVisible(page, where);

  // ⛔ Чекаємо, доки перехід ЗАВЕРШИТЬСЯ. Тема має анімацію до 150 мс
  // (`ФВ-14.26`), і знімок одразу після `focus()` ловить кадр ДО зміни
  // кольору межі — різниця виходила рівно 0.00 %, тобто перевірка
  // доповідала про відсутнє кільце там, де воно є.
  await settle(page);

  const after = await page.screenshot({ clip });

  // ⛔ `rawDifference`, а НЕ `structuralDifference`. Друга прибирає тон
  // навмисно — це її робота в гейті станів комірки (`ФВ-14.18`). Кільце
  // фокуса саме тоном і є: межа міняє колір, форма лишається тією самою.
  // Нормалізований вимір давав рівно 0.00 % і вимагав би «полагодити»
  // справний індикатор.
  const difference = rawDifference(before, after);

  // ⚠ Поріг 0.5 % на рівні елемента, а не 2 % як у станах комірки: кільце
  // фокуса — це рамка по периметру, і на великому полі вона займає малу
  // частку площі. Нижче цього значення різниця нерозрізненна від
  // згладжування тексту.
  expect(
    difference,
    `${where}: кільце фокуса невидиме — різниця ${(difference * 100).toFixed(2)} %`,
  ).toBeGreaterThan(0.005);
}

/**
 * Дає інтерфейсу дожити перехід перед виміром.
 *
 * ⚠ Два кадри плюс запас: перший кадр застосовує клас, другий малює
 * результат, а 250 мс перекривають найдовшу анімацію теми (`motionBase`
 * ≤ 150 мс). Фіксована пауза тут чесніша за очікування селектора: ми
 * міряємо ПІКСЕЛІ, і чекати треба саме на них.
 */
async function settle(page: Page): Promise<void> {
  await page.evaluate(
    () =>
      new Promise<void>((resolve) => {
        requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
      }),
  );

  await page.waitForTimeout(250);
}

/**
 * Модальний діалог тримає фокус усередині (`ФВ-14.17`).
 *
 * ⛔ Пастка фокуса — не зручність. Без неї Tab виводить людину на елементи
 * під діалогом: вона натискає те, чого не бачить, у формі, яку вважає
 * закритою.
 *
 * ⚠ Перевіряється обходом ПО КОЛУ: тиснемо Tab більше разів, ніж у діалозі
 * зупинок, і жодного разу фокус не має вийти за його межі.
 */
export async function expectFocusTrapped(page: Page, dialog: string): Promise<void> {
  const inside = async (): Promise<boolean> =>
    page.evaluate((selector) => {
      const modal = document.querySelector(selector);
      const active = document.activeElement;

      return modal !== null && active !== null && modal.contains(active);
    }, dialog);

  expect(await inside(), 'фокус не потрапив у діалог при відкритті').toBe(true);

  for (let i = 0; i < 12; i++) {
    await page.keyboard.press('Tab');
    expect(await inside(), `фокус вийшов за межі діалогу на ${i + 1}-му Tab`).toBe(true);
  }
}
