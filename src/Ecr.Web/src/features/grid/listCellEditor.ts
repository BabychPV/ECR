import type { ColumnDataSchemaModel, EditorBase, HyperFunc, VNode } from '@revolist/revogrid';
import { t } from '@/shared/i18n';

/**
 * Редактор-список комірки сітки: пошук, стрілки, Enter — для `Lookup`, `Bool`
 * і `Unit` (`X-13`, `R-01`).
 *
 * ⛔ Що ламалося. Три редактори були голим `<select>` зі збереженням на
 * `onChange` (`LookupCellEditor`, `BoolCellEditor`), і `autoFocus` у VNode
 * RevoGrid не спрацьовує — фокус лишався на сітці. Перша ж стрілка вниз, якою
 * людина хотіла ПОДИВИТИСЬ варіанти, міняла значення `<select>`, `onChange`
 * одразу зберігав його і закривав редактор: вибір робився випадково, а не
 * свідомо. Пошуку не було взагалі — довідник замовника це до 50 000 записів
 * (`RegistriesController`), і гортати їх стрілкою неможливо. Одиниця ж
 * (`Unit`) не мала редактора зовсім: текстове поле, у яке `kg` набрати можна,
 * а зберегти — ні («expects the identifier»).
 *
 * ⚠ Тепер правило одне на всі три типи: стрілки лише ПЕРЕМІЩУЮТЬ виділення,
 * значення підтверджує Enter (або Tab, або клік), Escape — скасовує (його
 * обробляє сама сітка: `KeyboardService` у режимі редагування). Друк —
 * пошук за підписом і підказкою, без урахування регістру.
 *
 * ⛔ Чому DOM збирається вручну, а не VNode-ами в `render`. RevoGrid
 * перемальовує `revogr-edit` лише тоді, коли змінюються ЙОГО властивості; стан
 * редактора (рядок пошуку, виділений варіант) живе в замиканні, і змінити його
 * на кожне натискання через `render` нічим. Тому `render` дає лише порожній
 * вузол-носій, а список і поле монтуються в нього в `componentDidRender` —
 * чистою функцією `mountListEditor`, яку можна перевірити в jsdom без самої
 * сітки.
 */

/** Варіант вибору. `value: null` — «очистити комірку». */
export interface ListOption {
  readonly value: string | null;
  readonly label: string;
  /** Друга, бляклим, частина рядка (код запису, розмірність одиниці). */
  readonly hint?: string | undefined;
}

/** Звідки редактор бере варіанти в мить відкриття. */
export interface ListSource {
  /** Варіанти; `null` — ще завантажуються. */
  readonly options: readonly ListOption[] | null;
}

/** Скільки варіантів показувати одночасно. */
export const MaxShownOptions = 50;

/** Скільки місця під полем потрібно, щоб перелік розкрився вниз, а не вгору. */
const ListMinSpacePx = 220;

/** Що знає смонтований редактор. */
export interface MountListEditorProps {
  readonly options: readonly ListOption[] | null;
  /** Поточне значення комірки (рядком) — з нього починається виділення. */
  readonly selected: string | null;
  /** Перший символ, з якого людина почала редагування, — початок пошуку. */
  readonly initialQuery: string;
  /** Підпис поля пошуку для читалки. */
  readonly ariaLabel: string;
  /** Підтвердження вибору. `viaTab` — піти на наступну комірку, а не лишитися. */
  readonly onCommit: (value: string | null, viaTab: boolean) => void;
}

/** Смонтований редактор: лише прибрати за собою. */
export interface MountedListEditor {
  readonly input: HTMLInputElement;
  /** Перелік варіантів — у `document.body`, не всередині `host`. */
  readonly list: HTMLUListElement;
  destroy(): void;
}

let listIds = 0;

/**
 * Монтує поле пошуку й список варіантів у `host`.
 *
 * ⚠ Фокус переходить у поле ПІСЛЯ поточного кадру (`setTimeout(0)`) — той самий
 * прийом, що й у вбудованого `TextEditor` RevoGrid (`await timeout()`): у мить
 * `componentDidRender` вузол ще не приєднано до видимого дерева, і `focus()`
 * без відкладення тихо нічого не робить. Саме тому `autoFocus` у VNode і не
 * працював.
 */
export function mountListEditor(host: HTMLElement, props: MountListEditorProps): MountedListEditor {
  const listId = `ecr-list-editor-${String(++listIds)}`;
  const doc = host.ownerDocument;

  const input = doc.createElement('input');
  input.type = 'text';
  input.className = 'ecr-list-editor-input';
  input.setAttribute('role', 'combobox');
  input.setAttribute('aria-autocomplete', 'list');
  input.setAttribute('aria-expanded', 'true');
  input.setAttribute('aria-controls', listId);
  input.setAttribute('aria-label', props.ariaLabel);
  input.placeholder = t('grid.listSearchPlaceholder');
  input.autocomplete = 'off';
  input.value = props.initialQuery;

  const list = doc.createElement('ul');
  list.id = listId;
  list.className = 'ecr-list-editor-list';
  list.setAttribute('role', 'listbox');

  host.replaceChildren(input);

  /*
   * ⛔ Перелік — у `document.body`, а не під полем. Вузол редактора живе
   * всередині вікна даних RevoGrid, яке обрізає все, що виходить за його межі
   * (`overflow: hidden`), і зсувається `transform`-ом при прокрутці — тож і
   * `position: fixed` там рахувався б від зсунутого предка. Живцем на стенді
   * це давало два видимі варіанти з чотирьох у таблиці з трьома рядками.
   * Тому перелік винесено на верхній рівень і прив'язано до поля координатами.
   */
  doc.body.append(list);

  const place = (): void => {
    const box = input.getBoundingClientRect();
    const viewHeight = doc.defaultView?.innerHeight ?? 0;
    const below = viewHeight - box.bottom;

    list.style.position = 'fixed';
    list.style.left = `${String(Math.round(box.left))}px`;
    list.style.minWidth = `${String(Math.round(box.width))}px`;

    // ⚠ Унизу екрана перелік розкривається ВГОРУ: інакше варіанти опинилися б
    // за краєм вікна, куди людина не дістане.
    if (below < ListMinSpacePx && box.top > below) {
      list.style.top = '';
      list.style.bottom = `${String(Math.round(viewHeight - box.top))}px`;
    } else {
      list.style.bottom = '';
      list.style.top = `${String(Math.round(box.bottom))}px`;
    }
  };

  place();

  // ⚠ Прокрутка будь-якого предка (сітка, сторінка) зсуває поле — перелік іде
  // за ним. `capture`, бо подія прокрутки не спливає.
  const view = doc.defaultView;
  view?.addEventListener('scroll', place, true);
  view?.addEventListener('resize', place);

  let shown: readonly ListOption[] = [];
  let active = -1;

  /** Чи людина вже щось обрала — інакше Tab просто йде далі, нічого не змінюючи. */
  let touched = props.initialQuery.length > 0;

  const optionId = (index: number): string => `${listId}-${String(index)}`;

  const paintActive = (): void => {
    for (const [index, node] of [...list.querySelectorAll<HTMLLIElement>('[role="option"]')].entries()) {
      const isActive = index === active;
      node.setAttribute('aria-selected', String(isActive));
      node.classList.toggle('ecr-list-editor-active', isActive);
      if (isActive) node.scrollIntoView?.({ block: 'nearest' });
    }

    if (active >= 0) input.setAttribute('aria-activedescendant', optionId(active));
    else input.removeAttribute('aria-activedescendant');
  };

  const note = (text: string): HTMLLIElement => {
    const item = doc.createElement('li');
    item.className = 'ecr-list-editor-note';
    item.setAttribute('role', 'presentation');
    item.textContent = text;

    return item;
  };

  const paint = (keepSelected: boolean): void => {
    if (props.options === null) {
      shown = [];
      active = -1;
      list.replaceChildren(note(t('grid.listLoading')));
      list.setAttribute('aria-busy', 'true');
      paintActive();

      return;
    }

    list.removeAttribute('aria-busy');

    // ⚠ «Очистити» (`value: null`) — лише над порожнім пошуком: людина, що
    // набирає «газ», шукає газ, а не спосіб стерти комірку.
    const matched =
      input.value.trim().length === 0
        ? props.options
        : filterOptions(props.options.filter((option) => option.value !== null), input.value);
    shown = matched.slice(0, MaxShownOptions);

    const items: HTMLLIElement[] = shown.map((option, index) => {
      const item = doc.createElement('li');
      item.id = optionId(index);
      item.className = 'ecr-list-editor-option';
      item.setAttribute('role', 'option');
      item.dataset['value'] = option.value ?? '';

      const label = doc.createElement('span');
      label.textContent = option.label;
      item.append(label);

      if (option.hint !== undefined && option.hint.length > 0) {
        const hint = doc.createElement('span');
        hint.className = 'ecr-list-editor-hint';
        hint.textContent = option.hint;
        item.append(hint);
      }

      // ⚠ `mousedown` гаситься: інакше поле втратило б фокус раніше за `click`,
      // і сітка закрила б редактор, так і не дізнавшись про вибір. `mouseup` —
      // теж: перелік поза `revo-grid`, і сітка вважала б відпускання кнопки
      // «кліком поза таблицею» й зняла фокус (`revo-grid.mouseupHandle`).
      item.addEventListener('mousedown', (event) => event.preventDefault());
      item.addEventListener('mouseup', (event) => event.preventDefault());
      item.addEventListener('click', () => props.onCommit(option.value, false));

      return item;
    });

    const extra: HTMLLIElement[] = [];
    if (matched.length === 0) extra.push(note(t('grid.listNothingFound')));
    if (matched.length > shown.length) {
      // ⛔ Решта не зникає мовчки: людина, що бачить 50 із 50 000, має знати,
      // що звужувати пошук — це шлях, а не «запису немає».
      extra.push(note(t('grid.listMore', { count: matched.length - shown.length })));
    }

    list.replaceChildren(...items, ...extra);

    const selectedIndex = keepSelected
      ? shown.findIndex((option) => (option.value ?? null) === props.selected)
      : -1;

    active = shown.length === 0 ? -1 : selectedIndex >= 0 ? selectedIndex : 0;
    paintActive();
  };

  const move = (delta: number): void => {
    if (shown.length === 0) return;

    touched = true;
    active = Math.min(shown.length - 1, Math.max(0, (active < 0 ? 0 : active) + delta));
    paintActive();
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.isComposing) return;

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        move(1);
        return;
      case 'ArrowUp':
        event.preventDefault();
        move(-1);
        return;
      case 'PageDown':
        event.preventDefault();
        move(10);
        return;
      case 'PageUp':
        event.preventDefault();
        move(-10);
        return;
      case 'Enter': {
        // ⚠ Enter без жодного варіанта (порожній пошук, нічого не знайдено) не
        // зберігає нічого: інакше «не знайшов» мовчки стирало б комірку.
        event.preventDefault();
        event.stopPropagation();
        const option = shown[active];
        if (option !== undefined) props.onCommit(option.value, false);
        return;
      }
      case 'Tab': {
        // ⚠ Tab підтверджує ЛИШЕ свідомий вибір (друк чи стрілки): пройти
        // табом крізь комірку не означає її переписати. Саме переміщення на
        // наступну комірку робить сітка (`KeyboardService`, режим редагування).
        const option = shown[active];
        if (touched && option !== undefined) props.onCommit(option.value, true);
        return;
      }
      default:
    }
  };

  const onInput = (): void => {
    touched = true;
    paint(false);
  };

  input.addEventListener('keydown', onKeyDown);
  input.addEventListener('input', onInput);

  paint(props.initialQuery.length === 0);

  const focusTimer = setTimeout(() => input.focus(), 0);

  return {
    input,
    list,
    destroy(): void {
      clearTimeout(focusTimer);
      view?.removeEventListener('scroll', place, true);
      view?.removeEventListener('resize', place);
      list.remove();
      input.removeEventListener('keydown', onKeyDown);
      input.removeEventListener('input', onInput);
    },
  };
}

/**
 * Варіанти, що містять КОЖНЕ слово запиту в підписі чи підказці.
 *
 * ⚠ Без урахування регістру й порядку слів: «газ прир» знаходить «Природний
 * газ». Порожній запит — усі варіанти в їхньому порядку.
 *
 * ⚠ Нижній регістр рахується раз на масив варіантів (`WeakMap`), а не на
 * кожне натискання: 50 000 записів довідника — це 50 000 `toLowerCase()` на
 * символ, і саме це й відчувалося б як затримка друку.
 */
export function filterOptions(options: readonly ListOption[], query: string): readonly ListOption[] {
  const words = query.trim().toLowerCase().split(/\s+/).filter((word) => word.length > 0);
  if (words.length === 0) return options;

  const haystacks = haystacksOf(options);

  return options.filter((_, index) => {
    const text = haystacks[index] ?? '';

    return words.every((word) => text.includes(word));
  });
}

const haystackCache = new WeakMap<readonly ListOption[], readonly string[]>();

function haystacksOf(options: readonly ListOption[]): readonly string[] {
  const known = haystackCache.get(options);
  if (known !== undefined) return known;

  const built = options.map((option) => `${option.label} ${option.hint ?? ''}`.toLowerCase());
  haystackCache.set(options, built);

  return built;
}

/** Значення комірки рядком: `5`, `'5'` і `true` — те, з чим порівнюються варіанти. */
function valueText(value: unknown): string | null {
  if (value === null || value === undefined) return null;

  const text = String(value).trim();

  return text.length === 0 ? null : text;
}

/**
 * Фабрика редактора RevoGrid для одного джерела варіантів.
 *
 * @param source Варіанти в МИТЬ ВІДКРИТТЯ: довідник міг приїхати вже після
 * побудови колонок, і редактор має бачити свіжий перелік, а не той, що був.
 * @param toSelected Як значення моделі стає рядком варіанта (`true` → `'true'`).
 */
export function createListCellEditor(
  source: () => ListSource,
  ariaLabel: string,
  toSelected: (value: unknown) => string | null = valueText,
) {
  return (
    column: ColumnDataSchemaModel,
    save: (value?: unknown, preventFocus?: boolean) => void,
  ): EditorBase => {
    let mounted: MountedListEditor | null = null;

    const editor: EditorBase = {
      render(h: HyperFunc<VNode>): VNode {
        return h('div', { class: 'ecr-list-editor' });
      },

      componentDidRender(): void {
        if (mounted !== null || !(editor.element instanceof HTMLElement)) return;

        const current = toSelected(column.value);

        // ⚠ Редагування почалося з літери — `editCell.val` несе саме її, а не
        // значення комірки. Так друк одразу стає пошуком, як у Excel.
        const started = editor.editCell?.val;
        const initialQuery =
          typeof started === 'string' && started !== String(column.value ?? '') ? started : '';

        mounted = mountListEditor(editor.element, {
          options: source().options,
          selected: current,
          initialQuery,
          ariaLabel,
          onCommit: (value, viaTab) => {
            mounted?.input.blur();
            save(value ?? '', viaTab);
          },
        });
      },

      // ⚠ Поле знімає фокус до закриття — той самий захист від «стрибка»
      // прокрутки, що й у вбудованого `TextEditor.beforeDisconnect`.
      beforeDisconnect(): void {
        mounted?.input.blur();
      },

      disconnectedCallback(): void {
        mounted?.destroy();
        mounted = null;
      },
    };

    return editor;
  };
}
