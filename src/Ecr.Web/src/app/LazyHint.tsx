import { cloneElement, useEffect, useId, useLayoutEffect, useRef, useState, type ComponentProps, type JSX, type ReactElement } from 'react';
import type { FloatingPosition } from '@mantine/core';
import type { Hint as HintComponent } from '@/shared/ui/Hint';

/** Атрибут, за яким знаходимо тригер після заміни (див. `LazyHint`). */
const TriggerAttribute = 'data-lazy-hint';

let loadedHint: typeof HintComponent | null = null;
let loading: Promise<void> | null = null;

function loadHint(): Promise<void> {
  loading ??= import('@/shared/ui/Hint').then((module) => {
    loadedHint = module.Hint;
  });

  return loading;
}

// Запит чанка стартує вже з імпортом модуля каркаса, а не з першого ефекту
// тригера: до першого кадру меню чанк зазвичай уже є, і підміна нікого не
// застає під фокусом.
void loadHint();

function findTrigger(key: string): HTMLElement | undefined {
  return [...document.querySelectorAll<HTMLElement>(`[${TriggerAttribute}]`)].find(
    (element) => element.getAttribute(TriggerAttribute) === key,
  );
}

/**
 * `Hint` для каркаса (меню), підвантажений окремим чанком.
 *
 * ⛔ Не статичний імпорт: каркас — у графі КОЖНОГО маршруту, і підказка там
 * додавала ~0.8 КБ gzip до `DocumentPage`/`PeriodsPage` і 1.5 КБ до
 * `PipelinePage` (бюджет `D-132`). Поки чанк їде, тригер рендериться сам —
 * доступне ім'я в нього власне (`aria-label`), бракує лише видимої підказки.
 *
 * ⛔ Обгортати тригер ЗАВЖДИ, а не лише коли підказка потрібна (`disabled`
 * замість умовного рендера): інакше дерево між станами різне, тригер
 * перемонтовується, і фокус клавіатури падає на `body`. Заодно заміна
 * `fallback` на `Hint` стається одразу після першого кадру каркаса, а не
 * під фокусом людини, що вперше згортає меню.
 *
 * ⛔ Чанк `Hint` може дійти ПІСЛЯ того, як людина вже сфокусувала тригер і
 * натиснула Enter (повільний диск, холодний кеш): тоді `fallback` змінюється на
 * `Hint`, React бачить інший тип елемента на тому ж місці й створює тригер
 * ЗАНОВО — фокус падає на `body` (WCAG 2.4.3; e2e `navbarCollapse`, спорадично
 * при повільному чанку). Тому підміну робимо самі: у мить, коли чанк доїхав, і ДО
 * заміни, запам'ятовуємо, чи тригер у фокусі, а після неї повертаємо фокус на
 * новий вузол (знаходимо за `data-lazy-hint`).
 */
export function LazyHint({
  label,
  position,
  disabled = false,
  children,
}: {
  label: string;
  position: FloatingPosition;
  /** Див. `Hint.disabled`: розмітка та сама, підказки немає. */
  disabled?: boolean;
  children: ComponentProps<typeof HintComponent>['children'];
}): JSX.Element {
  const key = useId();
  const [Hint, setHint] = useState<typeof HintComponent | null>(() => loadedHint);
  const restoreFocus = useRef(false);

  useEffect(() => {
    if (Hint !== null) return undefined;

    let live = true;
    void loadHint().then(() => {
      if (!live) return;
      const active = document.activeElement;
      restoreFocus.current = active instanceof HTMLElement && active.getAttribute(TriggerAttribute) === key;
      setHint(() => loadedHint);
    });

    return () => {
      live = false;
    };
  }, [Hint, key]);

  useLayoutEffect(() => {
    if (!restoreFocus.current) return;
    restoreFocus.current = false;
    findTrigger(key)?.focus({ preventScroll: true });
  }, [Hint, key]);

  const trigger = cloneElement(children as ReactElement<Record<string, unknown>>, {
    [TriggerAttribute]: key,
  }) as ComponentProps<typeof HintComponent>['children'];

  return Hint === null ? (
    trigger
  ) : (
    <Hint label={label} position={position} disabled={disabled}>
      {trigger}
    </Hint>
  );
}
