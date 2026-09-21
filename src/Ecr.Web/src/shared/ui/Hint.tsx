import {
  cloneElement,
  useEffect,
  useId,
  useState,
  type FocusEvent,
  type JSX,
  type KeyboardEvent,
  type MouseEvent,
  type ReactElement,
} from 'react';
import { Popover } from '@mantine/core';

/** Пропи тригера, які `Hint` доповнює, не затираючи власних. */
interface TriggerProps {
  readonly 'aria-describedby'?: string | undefined;
  readonly tabIndex?: number | undefined;
  readonly onFocus?: ((event: FocusEvent<HTMLElement>) => void) | undefined;
  readonly onBlur?: ((event: FocusEvent<HTMLElement>) => void) | undefined;
  readonly onMouseEnter?: ((event: MouseEvent<HTMLElement>) => void) | undefined;
  readonly onMouseLeave?: ((event: MouseEvent<HTMLElement>) => void) | undefined;
  readonly onKeyDown?: ((event: KeyboardEvent<HTMLElement>) => void) | undefined;
}

export interface HintProps {
  /** Текст підказки — уже перекладений (`t(...)`). */
  readonly label: string;

  /**
   * Тригер: один елемент, що приймає `ref` (компоненти Mantine, `<a>`,
   * `<span>`…). Його власні обробники й `aria-describedby` зберігаються.
   */
  readonly children: ReactElement<TriggerProps>;

  /**
   * `true` — тригер сам по собі не фокусований (бейдж, текст, `<code>`), і
   * `Hint` дає йому `tabIndex=0`. Для посилань і кнопок не потрібен: вони вже
   * в порядку табуляції, а зайва зупинка лише подвоїла б її.
   */
  readonly focusable?: boolean | undefined;
}

/**
 * Пояснювальна підказка, доступна з клавіатури й для екранного читача.
 *
 * ⛔ Заміна атрибута `title`, а не прикраса. `title` браузер показує лише під
 * мишею: з клавіатури його не видно ніколи, на дотиковому екрані — теж, а
 * екранні читачі читають його непослідовно. Тут три канали замість одного:
 *
 * 1. **Опис** — `aria-describedby` на тригері вказує на прихований вузол із
 *    тим самим текстом. Вузол є в DOM ЗАВЖДИ, а не лише коли підказка
 *    відкрита: читач озвучує опис разом із назвою при фокусі, без наведення.
 *    `hidden` не заважає — вузол, на який посилаються явно, входить в опис
 *    (accname 1.2, крок 2A), а сам окремо не читається вдруге.
 * 2. **Видима підказка** відкривається і на наведенні, і на ФОКУСІ.
 * 3. **Escape** закриває її, не рухаючи ні фокуса, ні миші (WCAG 1.4.13).
 *
 * ⛔ `Popover`, а не `Tooltip` Mantine — через бюджет маршруту (`D-132`).
 * `Tooltip` тягне власні взаємодії `@floating-ui/react` (`useHover`,
 * `useFocus`, `useRole`, `useDelayGroup`…), яких більше ніде на сторінці
 * зрізів немає, і `SnapshotsPage` переходила межу: 250.1 КБ gzip при 250.
 * `Popover` уже є в графі кожного екрана з `Select` (через `Combobox`), тож
 * ціна — 0.7 КБ, а не 3.5. Роль `tooltip` задана явно, ролі `Popover`
 * (`dialog`, `aria-haspopup`, `aria-expanded`) вимкнені `withRoles={false}`:
 * це підказка, а не діалог, і фокус у неї не переходить.
 *
 * ⚠ Стан відкриття — власний, а не вбудовані події Mantine: вбудований фокус
 * `Tooltip` (`useFocus`, `visibleOnly`) залежить від `:focus-visible`, а
 * `Popover` у керованому режимі не відкривається сам зовсім.
 *
 * ⚠ Наведення на саму підказку тримає її відкритою (WCAG 1.4.13, «hoverable»):
 * інакше текст, ширший за екран, не можна було б дочитати, збільшивши його.
 *
 * ⚠ Кольори — з теми: тло `Popover` Mantine (`white` / `dark.6`), текст —
 * `--mantine-color-text`, тобто `surfaces.*.text` (`cssVariables.ts`).
 * Контраст обох пар міряє `Hint.test.tsx` на ЗЛИТІЙ темі застосунку.
 */
export function Hint({ label, children, focusable = false }: HintProps): JSX.Element {
  const id = `${useId()}-hint`;
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  const [overTip, setOverTip] = useState(false);
  const [dismissed, setDismissed] = useState(false);

  const opened = (hovered || focused || overTip) && !dismissed;

  // Escape і тоді, коли фокус не на тригері (підказку відкрила миша).
  useEffect(() => {
    if (!opened) return undefined;

    const onKey = (event: globalThis.KeyboardEvent): void => {
      if (event.key === 'Escape') setDismissed(true);
    };

    document.addEventListener('keydown', onKey);

    return () => document.removeEventListener('keydown', onKey);
  }, [opened]);

  const own = children.props;
  const describedBy = [own['aria-describedby'], id].filter(Boolean).join(' ');

  const trigger = cloneElement(children, {
    'aria-describedby': describedBy,
    ...(focusable ? { tabIndex: own.tabIndex ?? 0 } : {}),
    onFocus: (event: FocusEvent<HTMLElement>) => {
      own.onFocus?.(event);
      setFocused(true);
    },
    onBlur: (event: FocusEvent<HTMLElement>) => {
      own.onBlur?.(event);
      setFocused(false);
      if (!hovered && !overTip) setDismissed(false);
    },
    onMouseEnter: (event: MouseEvent<HTMLElement>) => {
      own.onMouseEnter?.(event);
      setHovered(true);
    },
    onMouseLeave: (event: MouseEvent<HTMLElement>) => {
      own.onMouseLeave?.(event);
      setHovered(false);
      if (!focused && !overTip) setDismissed(false);
    },
    onKeyDown: (event: KeyboardEvent<HTMLElement>) => {
      own.onKeyDown?.(event);
      // ⚠ Лише коли підказка відкрита: інакше Escape належить тому, хто
      // довкола (модальне вікно, меню), і перехоплювати його не можна.
      if (event.key === 'Escape' && opened) {
        event.stopPropagation();
        setDismissed(true);
      }
    },
  });

  return (
    <>
      <Popover
        opened={opened}
        // ⛔ Без `onClose`: `Popover` кличе його на КОЖНОМУ переході
        // `opened` у `false` (`use-popover`, `useDidUpdate`), а не лише на
        // Escape/клацанні зовні — і звичайне відведення миші залишало б
        // підказку «відхиленою» назавжди. Escape обробляє сам `Hint`.
        withRoles={false}
        returnFocus={false}
        trapFocus={false}
        position="top"
        withArrow
        shadow="md"
      >
        <Popover.Target>{trigger}</Popover.Target>
        <Popover.Dropdown
          role="tooltip"
          maw={280}
          p="xs"
          fz="sm"
          data-hint-tooltip=""
          onMouseEnter={() => setOverTip(true)}
          onMouseLeave={() => {
            setOverTip(false);
            if (!focused && !hovered) setDismissed(false);
          }}
        >
          {label}
        </Popover.Dropdown>
      </Popover>
      <span id={id} hidden data-hint-text="">
        {label}
      </span>
    </>
  );
}
