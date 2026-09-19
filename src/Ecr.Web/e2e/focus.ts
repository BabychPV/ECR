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
 * Поле навколо цілі. `outline` малюється ЗА МЕЖАМИ рамки елемента, тому
 * `locator.screenshot()` його не бачить: у посилання на документ кільце є
 * (`solid 2px`), а вимір давав 0.00 %.
 */
const RingPadding = 6;

/** Область виміру разом із причиною, чому виміряти її НЕ МОЖНА. */
interface MeasureRegion {
  readonly clip: { x: number; y: number; width: number; height: number };

  /** `null` — міряти можна; рядок — чому ні, людськими словами. */
  readonly problem: string | null;
}

/**
 * Рахує область виміру і чесно каже, коли вона вироджена.
 *
 * ⛔ Вироджена область — це НЕ «нуль відсотків різниці». Раніше тут стояло
 * `Math.max(0, …)`, і коли ціль виїжджала за верхній край вікна, затиснута
 * область показувала ЗАКРІПЛЕНУ ШАПКУ — однакову на обох знімках. Вимір
 * давав рівно 0.00 %, а перевірка доповідала «кільце фокуса невидиме», тобто
 * повідомляла про дефект, якого немає. Перевірка, здатна вигадати дефект,
 * так само здатна промовчати про справжній: це та сама підміна «я виміряв»
 * на «я не зміг виміряти».
 */
async function regionFor(page: Page, target: Locator): Promise<MeasureRegion> {
  const box = await target.boundingBox();
  const viewport =
    page.viewportSize() ??
    (await page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight })));

  const clip = {
    x: (box?.x ?? 0) - RingPadding,
    y: (box?.y ?? 0) - RingPadding,
    width: (box?.width ?? 0) + RingPadding * 2,
    height: (box?.height ?? 0) + RingPadding * 2,
  };

  if (box === null) return { clip, problem: 'елемента немає в розкладці (boundingBox === null)' };
  if (box.width === 0 || box.height === 0) {
    return {
      clip,
      problem: `нульовий розмір ${box.width.toFixed(0)}×${box.height.toFixed(0)}`,
    };
  }

  if (
    clip.x < 0 ||
    clip.y < 0 ||
    clip.x + clip.width > viewport.width ||
    clip.y + clip.height > viewport.height
  ) {
    return {
      clip,
      problem:
        `ціль разом із полем у ${String(RingPadding)} px не вміщається у вікно — ` +
        `область x=${clip.x.toFixed(0)} y=${clip.y.toFixed(0)} ` +
        `${clip.width.toFixed(0)}×${clip.height.toFixed(0)}, ` +
        `вікно ${String(viewport.width)}×${String(viewport.height)}`,
    };
  }

  // ⛔ Перекриття ловиться окремо від меж вікна, і це не перестраховка:
  // прокрутка «як треба» вміє поставити ціль ПІД закріплену шапку. Тоді
  // область формально всередині вікна, але на обох знімках у ній та сама
  // шапка — знову 0.00 % і знову хибний висновок «кільця немає».
  const covering = await target.evaluate((element) => {
    const rect = element.getBoundingClientRect();
    const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);

    if (hit === null) return 'нічого (точка поза вікном)';
    if (hit === element || element.contains(hit) || hit.contains(element)) return null;

    return hit.tagName.toLowerCase();
  });

  if (covering !== null) return { clip, problem: `ціль перекрита <${covering}>` };

  return { clip, problem: null };
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
 *
 * ⛔ **Чому спершу прокрутка, а відмова лише потім.** Обидві поведінки
 * потрібні, і порядок між ними не довільний. Прокрутити ціль у вікно — це не
 * поблажка виміру, а відтворення того, що робить браузер справжньої людини:
 * Tab на елемент, якого не видно, сам прокручує сторінку до нього, тож
 * «кільце видно» треба міряти САМЕ в тому стані, у якому його побачить
 * людина, — інакше перевірка падала б на кожній довгій сторінці й не
 * доводила б нічого про кільце. Але прокрутка не має права бути мовчазним
 * порятунком: якщо ПІСЛЯ неї область усе одно вироджена (ціль більша за
 * вікно, нульовий розмір, ціль під закріпленою шапкою), помічник
 * ВІДМОВЛЯЄТЬСЯ міряти й називає справжню причину. Числа, якого не виміряв,
 * він не вигадує — ані «0.00 %», ані будь-якого іншого.
 */
export async function expectFocusRing(page: Page, target: Locator, where: string): Promise<void> {
  await expect(target, `${where}: елемента немає`).toBeVisible();

  // ⚠ Мінімальна прокрутка перша: сторінку, яка й так у потрібному місці, вона
  // не рухає, а отже не міняє того, що бачить наступний крок сценарію.
  await target.scrollIntoViewIfNeeded();

  // Прибираємо фокус із будь-чого перед виміром: якщо елемент уже у фокусі,
  // «до» і «після» збіглися б, і перевірка зеленіла б завжди.
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await settle(page);

  // ⛔ Геометрія міряється ПІСЛЯ прокрутки і ПІСЛЯ зняття фокуса — тобто рівно
  // в тому стані, який піде у знімок «до». Раніше вона бралася до всього
  // цього, і `target.focus()` нижче встигав прокрутити сторінку між двома
  // знімками: область лишалася старою, а під нею вже був інший вміст.
  let region = await regionFor(page, target);

  if (region.problem !== null) {
    // ⚠ Друга спроба — детермінована: по центру вікна. `scrollIntoViewIfNeeded`
    // прокручує мінімально й уміє лишити ціль впритул до краю або під
    // закріпленою шапкою; центр вікна не межує ні з чим.
    await target.evaluate((element) => {
      element.scrollIntoView({ block: 'center', inline: 'center' });
    });
    await settle(page);
    region = await regionFor(page, target);
  }

  expect(
    region.problem,
    `${where}: ВИМІР НЕМОЖЛИВИЙ — ${region.problem ?? ''}. Це не «кільця немає»: ` +
      'порівнювати нічого, і числа тут не буде.',
  ).toBeNull();

  const clip = region.clip;
  const before = await page.screenshot({ clip });

  await target.focus();
  await expectFocusVisible(page, where);

  // ⛔ Чекаємо, доки перехід ЗАВЕРШИТЬСЯ. Тема має анімацію до 150 мс
  // (`ФВ-14.26`), і знімок одразу після `focus()` ловить кадр ДО зміни
  // кольору межі — різниця виходила рівно 0.00 %, тобто перевірка
  // доповідала про відсутнє кільце там, де воно є.
  await settle(page);

  // ⛔ Розкладка не поїхала між знімками. Без цієї перевірки зсув сторінки дав
  // би ВЕЛИКУ різницю в тій самій області — тобто хибне «кільце видно», що
  // гірше за хибне падіння: воно мовчить про справжній дефект.
  const shifted = await regionFor(page, target);
  const shift = Math.max(
    Math.abs(shifted.clip.x - clip.x),
    Math.abs(shifted.clip.y - clip.y),
    Math.abs(shifted.clip.width - clip.width),
    Math.abs(shifted.clip.height - clip.height),
  );

  expect(
    shift,
    `${where}: розкладка зрушила між знімками на ${shift.toFixed(1)} px — вимір недійсний, ` +
      'бо в тій самій області опинився інший вміст.',
  ).toBeLessThanOrEqual(1);

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
