/**
 * AN-39 / L8-15: чи ЛЮДИНА щось ввела в редакторі комірки (D-283, Q10=A).
 *
 * ⛔ Клік повз редактор (`applyOnClose`, T4-03) повертає в `afteredit` те, що стояло в полі, -
 * а в порожній комірці там показаний `defaultValue`. Це не введення: писати його не можна.
 * Але ЯВНИЙ ввід значення, рівного default (`0` при default `0`), писати треба, інакше це
 * тиха незбережена правка. Порівняння з default їх не розрізняє - тому відстежується факт
 * введення: подія `input`/`change` у редакторі.
 *
 * ⚠ Редактори списку й дати комітять власною дією (вибір, Enter/Tab, день у календарі) і
 * шлють `ExplicitCommitEvent` (`announceExplicitCommit`); клік повз її не породжує - тому для
 * списку «нічого не обрано» не пише default (Q10=A).
 *
 * ⚠ Живий Chromium + справжній revo-grid 4.11: клік повз редактор БЕЗ `getValue` віддає в
 * `beforeedit/afteredit` значення комірки (не `undefined`) - тож саме `untouched` тут і рятує
 * від запису показаного default.
 */
export interface EditorTouched {
  isTouched(): boolean;
  reset(): void;
  dispose(): void;
}

const Wrapper = '.edit-input-wrapper';

/** Подія, яку редактор з власним комітом шле вгору перед `save()` (явний вибір людини). */
export const ExplicitCommitEvent = 'ecr:explicit-commit';

/** Редактор повідомляє сітку: зараз буде явний коміт (не клік повз). */
export function announceExplicitCommit(editorElement: Element | null | undefined): void {
  editorElement?.dispatchEvent(new CustomEvent(ExplicitCommitEvent, { bubbles: true }));
}

export function trackEditorTouched(container: HTMLElement): EditorTouched {
  let touched = false;

  const inEditor = (target: EventTarget | null): target is Element =>
    target instanceof Element && target.closest(Wrapper) !== null;

  // Редагування, почате НАБОРОМ символа, відкриває редактор уже з цим символом (події `input`
  // немає) - це введення, а не заходження в комірку.
  let typedToOpen = false;

  const onKey = (event: Event): void => {
    const key = event as KeyboardEvent;
    if (inEditor(key.target)) return;

    typedToOpen = key.key.length === 1 && !key.ctrlKey && !key.metaKey && !key.altKey;
  };

  const onOpen = (event: Event): void => {
    if (!inEditor(event.target)) return;

    touched = typedToOpen;
    typedToOpen = false;
  };

  // Редактори списку/дати комітять ЯВНОЮ дією (вибір, Enter, день у календарі) і повідомляють про
  // це подією; клік повз (`applyOnClose`) її не породжує.
  const onExplicitCommit = (): void => {
    touched = true;
  };

  const onInput = (event: Event): void => {
    if (inEditor(event.target)) touched = true;
  };

  container.addEventListener('keydown', onKey, true);
  container.addEventListener(ExplicitCommitEvent, onExplicitCommit);
  container.addEventListener('focusin', onOpen);
  container.addEventListener('input', onInput);
  container.addEventListener('change', onInput);

  return {
    isTouched: () => touched,
    reset: () => {
      touched = false;
    },
    dispose: () => {
      container.removeEventListener('keydown', onKey, true);
      container.removeEventListener(ExplicitCommitEvent, onExplicitCommit);
      container.removeEventListener('focusin', onOpen);
      container.removeEventListener('input', onInput);
      container.removeEventListener('change', onInput);
    },
  };
}
