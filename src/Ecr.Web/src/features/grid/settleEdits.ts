import { useCallback, useRef, useState } from 'react';
import { notifications } from '@mantine/notifications';
import { t } from '@/shared/i18n';
import { flushUnsaved, hasUnsavedChanges } from '@/shared/ui/unsavedSources';
import type { HeldEdit } from './pendingStore';

/**
 * AN-28 / L8-01: дії над документом (Submit, Approve/Reject, Validate, Export,
 * Import Apply, Recalculate, міграція версії) спершу зберігають те, що оператор
 * щойно набрав у сітці.
 *
 * Дефект: ці кнопки НЕ викликали `flushUnsaved`. Набрав значення -> одразу
 * Submit: POST /submit ішов раніше за PATCH (дебаунс 500 мс + закриття
 * редактора на наступному кадрі), сервер подавав без значення, а пізніший
 * PATCH отримував 403 `DocumentSubmitted` - значення втрачалося мовчки.
 */

/**
 * Чекає, доки RevoGrid зафіксує відкритий редактор (кадр + тік), і зберігає
 * все. `false` - частину зберегти не вдалося (відмова сервера, конфлікт, тайм-аут).
 *
 * ⚠ Запасний тайм-аут, бо `requestAnimationFrame` не спрацьовує у фоновій
 * вкладці: без нього дія зависала б до повернення на вкладку.
 */
export async function settleAndFlushEdits(): Promise<boolean> {
  await new Promise<void>((resolve) => {
    const fallback = setTimeout(resolve, 100);
    requestAnimationFrame(() => {
      clearTimeout(fallback);
      setTimeout(resolve, 0);
    });
  });

  return hasUnsavedChanges() ? flushUnsaved() : true;
}

/**
 * Хто вміє показати утриману комірку: перемкнути аркуш, прокрутити, підсвітити.
 *
 * ⚠ Реєструє сторінка документа - лише вона знає аркуші й таблиці; кнопки дій
 * (`SheetActions`, `ExportButton`, `ImportPanel`, ...) про них не знають.
 */
type HeldEditRevealer = (held: HeldEdit) => void;

let revealer: HeldEditRevealer | null = null;

/**
 * Звідки взяти утриману правку. Ставить `autosave.ts` (власник сховища).
 *
 * ⛔ Не статичний імпорт `pendingStore`: цей модуль тягнуть `SheetActions`/`ExportButton`,
 * а їх - `PeriodsPage`; сховище з `cellValue`/`decimal` додавало маршрутові ~2.7 КБ (D-132).
 */
let heldEditLookup: (() => HeldEdit | null) | null = null;

export function registerHeldEditLookup(lookup: () => HeldEdit | null): void {
  heldEditLookup = lookup;
}

/** Ставить показ утриманої комірки; повертає зняття (лише свого). */
export function registerHeldEditRevealer(reveal: HeldEditRevealer): () => void {
  revealer = reveal;

  return () => {
    if (revealer === reveal) revealer = null;
  };
}

export interface WhenEditsSavedOptions {
  /**
   * Дія лише ЧИТАЄ збережене (Validate, Export): незбережене її не блокує -
   * вона йде по збереженому, а оператор бачить попередження (AN-28 P2-1).
   * Блокуються лише дії, що змінюють стан чи дані: там утрачене значення
   * подалося б без нього.
   */
  readonly readOnly?: boolean;
}

/**
 * Пояснює, чому незбережене не збереглося.
 *
 * ⛔ AN-28 P2-1: раніше - один загальний тост через 3 с тиші. Тепер утримана
 * (відхилена сервером) правка називається одразу, з причиною сервера, а сама
 * комірка показується (`revealer`): інакше оператор шукав би її по 91 таблиці.
 */
function explainUnsaved(options: WhenEditsSavedOptions): void {
  const held = heldEditLookup?.() ?? null;

  if (options.readOnly === true) {
    notifications.show({ color: 'statusWarning', message: t('document.unsavedNotIncluded') });
  } else if (held === null) {
    notifications.show({ color: 'statusError', message: t('document.unsavedBlocksAction') });
  } else {
    notifications.show({
      color: 'statusError',
      title: t('document.heldEditBlocksAction'),
      message: held.message,
    });
  }

  if (held !== null) revealer?.(held);
}

/**
 * Виконує `action` ЛИШЕ після успішного збереження незбереженого. Якщо
 * зберегти не вдалося - дія не виконується (крім `readOnly`), оператор бачить
 * причину, а утримана комірка - підсвічена.
 *
 * @returns Чи виконано `action`.
 */
export async function whenEditsSaved(
  action: () => void,
  options: WhenEditsSavedOptions = {},
): Promise<boolean> {
  const saved = await settleAndFlushEdits();

  if (!saved) explainUnsaved(options);

  if (saved || options.readOnly === true) {
    action();

    return true;
  }

  return false;
}

/** Дія кнопки: синхронна або проміс (`mutateAsync`) - тоді кнопка зайнята до його кінця. */
type SettledAction = () => void | Promise<unknown>;

/**
 * Кнопка дії над документом: зайнятість і single-flight на ВЕСЬ шлях
 * «зберегти набране -> дія» (AN-28 P2-2).
 *
 * ⛔ Що ламалося. `whenEditsSaved` триває кадр + оберт PATCH (до 3 с), а
 * `loading={mutation.isPending}` у цей час - false: мутація ще не почалась.
 * Кнопка лишалась активною без жодної ознаки роботи, і другий клік ставив у
 * чергу другий Submit/Approve/Apply/експорт (журнал зонда: flush, flush,
 * POST submit, POST submit).
 *
 * ⚠ Захист СИНХРОННИЙ (`useRef`), не через стан React: `disabled` настає лише
 * на наступному рендері, а подвійний клік встигає раніше (той самий урок, що
 * `recalculateInFlight` у `SheetActions`). `settling` - для показу (`loading`).
 *
 * ⚠ Якщо дія повертає проміс (`mutateAsync`), зайнятість тримається до його
 * кінця: між «збережено» і `isPending` мутації інакше лишалась би щілина.
 * Відмову промісу показує сама мутація (`onError`); тут вона лише гаситься.
 *
 * @param busy Зовнішня зайнятість (мутація вже летить) - клік ігнорується.
 */
export function useSettledAction(busy = false): {
  /** Іде збереження набраного або сама дія. */
  readonly settling: boolean;
  /** Запускає `action` після збереження; повторний виклик, доки триває попередній, - нічого. */
  readonly run: (action: SettledAction, options?: WhenEditsSavedOptions) => void;
} {
  const [settling, setSettling] = useState(false);
  const inFlight = useRef(false);
  const busyNow = useRef(busy);
  busyNow.current = busy;

  const run = useCallback((action: SettledAction, options: WhenEditsSavedOptions = {}): void => {
    if (inFlight.current || busyNow.current) return;

    inFlight.current = true;
    setSettling(true);

    let running: Promise<unknown> | undefined;
    void whenEditsSaved(() => {
      const result = action();
      if (result instanceof Promise) running = result;
    }, options)
      .then(async () => {
        await running?.catch(() => undefined);
      })
      .finally(() => {
        inFlight.current = false;
        setSettling(false);
      });
  }, []);

  return { settling, run };
}

/**
 * ✎ UI-14: показує першу утриману (відхилену сервером) правку — посилання
 * «Show» у стані збереження над сітками. Повтор збереження лишається кнопкою
 * сітки «Retry save» (`DocumentGrid.tsx`), тут лише перехід до неї.
 *
 * @returns Чи було що показати.
 */
export function revealFirstHeldEdit(): boolean {
  const held = heldEditLookup?.() ?? null;
  if (held === null || revealer === null) return false;

  revealer(held);

  return true;
}
