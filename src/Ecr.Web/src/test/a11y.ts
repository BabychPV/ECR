import axe, { type AxeResults, type Result } from 'axe-core';

/**
 * Перевірка доступності (`ФВ-14.16`, `D-127`).
 *
 * ⛔ Блокує CI: нуль порушень рівня `critical` і `serious`. Це не формальність
 * для звітності — `ФВ-14.4` уже вимагає **повної роботи grid із клавіатури**,
 * тобто половина AA в системі є як функціональна вимога, і решта добудовується
 * дешево. Після релізу та сама робота коштує вдесятеро.
 *
 * ⚠ Що axe в jsdom **не** перевіряє: контраст кольорів. Обчислення контрасту
 * потребує розкладки, а її в jsdom немає — правило `color-contrast` мовчки
 * пропускається. Тому контраст рахує окремий тест над токенами теми
 * (`shared/theme/__tests__/contrast.test.ts`), і саме він, а не axe, є
 * доказом виконання `ФВ-14.17`.
 *
 * ⚠ Порушення рівнів `minor` і `moderate` не блокують: серед них багато
 * рекомендацій, які в межах компонентної бібліотеки виправити неможливо, а
 * поріг, який неможливо втримати, вимикають цілком — і разом із ним зникають
 * `critical`.
 */
const Blocking = new Set(['critical', 'serious']);

/** Порушення, яке має бути виправлене. */
export interface Violation {
  id: string;
  impact: string;
  help: string;
  nodes: string[];
}

/**
 * Проганяє axe над піддеревом і повертає **лише блокуючі** порушення.
 *
 * ⚠ Повертає перелік, а не кидає виняток: тест сам вирішує, що з ним робити,
 * і в повідомленні про падіння видно, ЩО саме і НА ЯКОМУ вузлі — інакше
 * доводиться відтворювати вручну.
 */
export async function findViolations(container: Element): Promise<Violation[]> {
  const results: AxeResults = await axe.run(container, {
    // Збираються лише порушення: без цього axe складає ще й перелік усього,
    // що пройшло, — на сторінці Mantine це тисячі вузлів.
    resultTypes: ['violations'],
    elementRef: false,

    // ⚠ Правила беруться за тегами WCAG 2.1 A/AA — рівно те, що вимагає
    // `ФВ-14.16`. Повний набір axe містить і «best practice», яких стандарт
    // не вимагає, і вони перетворили б блокуючу перевірку на шум.
    // ⛔ Перелік правил ЯВНИЙ, а не «усі теги WCAG». Причина суто практична:
    // повний набір у jsdom проганяється по 35 секунд на сторінку, тобто сім
    // хвилин на дванадцять маршрутів — а перевірку, яка стільки триває,
    // вимикають. Тут лишені правила рівня `critical`/`serious`, які в jsdom
    // працюють чесно.
    //
    // ⛔ `color-contrast` тут немає навмисно: правило малює елемент на
    // `<canvas>`, щоб дістати фактичний колір, а в jsdom `getContext` не
    // реалізований — axe не падає, а зависає. Контраст натомість рахує окремий
    // тест над токенами теми, і він СУВОРІШИЙ: axe бачив би лише те, що зараз
    // на екрані, а тест перевіряє кожну пару в обох темах.
    runOnly: {
      type: 'rule',
      values: [
        // Кожен елемент керування має доступне ім'я.
        'label',
        'button-name',
        'link-name',
        'input-button-name',
        'select-name',
        'aria-input-field-name',
        'aria-toggle-field-name',
        'aria-command-name',
        'form-field-multiple-labels',

        // ARIA вжита правильно: чужа роль ламає навігацію читалкою повністю.
        'aria-valid-attr',
        'aria-valid-attr-value',
        'aria-required-attr',
        'aria-required-children',
        'aria-required-parent',
        'aria-roles',
        'aria-hidden-focus',
        'aria-hidden-body',

        // Структура: таблиці, заголовки, зображення, дублікати ідентифікаторів.
        'image-alt',
        'td-headers-attr',
        'th-has-data-cells',
        'duplicate-id-aria',
        'empty-heading',
        'html-has-lang',
        'nested-interactive',

        // ⚠ WCAG 2.4.1 Bypass Blocks (Q-263). Раніше відсутнє навмисно —
        // не тому, що правило не блокуюче (воно `serious`), а тому, що
        // порушення, яке воно ловить (навігація `AppShell.Navbar` до 15
        // пунктів рендериться в DOM РАНІШЕ за `AppShell.Main`, і
        // клавіатурний користувач без читалки не мав як це оминути), не
        // було виправлене — гейт мовчав би про відоме й нефіксоване. Тепер,
        // коли `AppLayout.tsx` додав «Пропустити навігацію» першим
        // фокусованим елементом сторінки, правило вмикається, щоб
        // регресія (посилання прибрали чи посунули нижче за нав) не
        // проїхала непоміченою.
        'bypass',
      ],
    },
  });

  return results.violations
    .filter((violation: Result) => Blocking.has(violation.impact ?? ''))
    .map((violation: Result) => ({
      id: violation.id,
      impact: violation.impact ?? '',
      help: violation.help,
      nodes: violation.nodes.map((node) => node.html),
    }));
}

/** Читабельний опис порушень для повідомлення про падіння тесту. */
export function describe(violations: Violation[]): string {
  return violations
    .map((v) => `${v.impact} · ${v.id}: ${v.help}\n    ${v.nodes.join('\n    ')}`)
    .join('\n');
}
