/**
 * Сторож проти технічних ключів на екрані (`D-138`).
 *
 * ⛔ `A7-33` була видима **кожному** — сторінка входу показувала `login.title`
 * замість назви системи, — і прожила до живого запуску. Причина в тому, що
 * написи з'являлися від першого ж натискання клавіші: будь-хто, хто торкався
 * екрана, бачив уже правильний текст. **Жодна перевірка «подивитися очима»
 * цього б не спіймала, включно з ручною.**
 *
 * Тому перевірка машинна і не залежить від дотику.
 */

/**
 * Що вважається ключем: маленькі літери з крапками.
 *
 * ⚠ Прив'язка до початку і кінця обов'язкова: без неї під шаблон підпадає
 * будь-яке речення, у якому є слово з крапкою.
 */
const KeyLike = /^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$/;

/** Позначка відсутнього рядка; на екрані її бути не має ніде. */
const MissingMarker = '⟦';

/** Знайдений підозрілий текст разом із місцем. */
export interface KeyLikeHit {
  text: string;
  html: string;
}

/**
 * Шукає ключеподібний текст у піддереві.
 *
 * ⚠ Обходяться саме ТЕКСТОВІ вузли, а не `textContent` елементів: інакше один
 * абзац із адресою пошти всередині зробив би підозрілим весь блок, і виняток
 * довелося б вішати на пів сторінки.
 */
export function findKeyLikeText(container: Element): KeyLikeHit[] {
  const hits: KeyLikeHit[] = [];
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);

  let node = walker.nextNode();
  while (node !== null) {
    const text = (node.textContent ?? '').trim();
    const parent = node.parentElement;

    if (text.length > 0 && parent !== null && !isExempt(parent)) {
      if (text.includes(MissingMarker) || KeyLike.test(text)) {
        hits.push({ text, html: parent.outerHTML.slice(0, 200) });
      }
    }

    node = walker.nextNode();
  }

  return hits;
}

/**
 * Чи дозволено цьому вузлу містити текст із крапками.
 *
 * ⛔ Хибні спрацювання будуть обов'язково: під шаблон підпадають адреси пошти,
 * імена файлів (`openapi.snapshot.json`), доменні імена (`smtp.example.local`),
 * версії пакетів. Тому виняток є — але він ставиться **на дані**, не на
 * маршрут: сторож, вимкнений на сторінці, це сторож, якого немає.
 *
 * ⚠ Перевіряється весь ланцюг предків: атрибут природно висить на контейнері
 * списку, а текст лежить у вкладеному вузлі.
 */
function isExempt(element: Element): boolean {
  return element.closest('[data-allow-dotted]') !== null;
}

/** Читабельний опис для повідомлення про падіння тесту. */
export function describeHits(hits: KeyLikeHit[]): string {
  if (hits.length === 0) return '';

  return [
    'На екрані технічні ключі замість написів (D-138):',
    ...hits.map((hit) => `  «${hit.text}»\n    ${hit.html}`),
    '',
    'Якщо це дані за природою (пошта, ім\'я файла, доменне ім\'я) —',
    'позначити САМЕ їх атрибутом data-allow-dotted, не сторінку.',
  ].join('\n');
}
