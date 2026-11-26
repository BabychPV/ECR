import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';

/**
 * «Код на весь екран» редактора скрипта (✎ 24.11, вимога людини).
 *
 * ⛔ Перемикається СТАН, а не розмітка: той самий екземпляр редактора лишається змонтованим (курсор, виділення,
 * прокрутка, незбережені правки й підкреслення проблем не губляться), змінюється лише його рамка.
 *
 * Клавіші: `Ctrl+Shift+F` (`⌘+Shift+F`) — увімкнути/вимкнути; `Esc` — вийти. `Esc` не перехоплюється, коли його вже
 * обробив хтось інший (підказки Monaco, `defaultPrevented`) або коли фокус у відкритому діалозі — там `Esc` закриває
 * діалог, а не повноекранний режим.
 */
export interface ScriptFullScreen {
  readonly full: boolean;
  readonly toggle: () => void;
  /** Кнопка перемикача: на неї повертається фокус після виходу. */
  readonly button: RefObject<HTMLButtonElement | null>;
}

export interface ScriptFullScreenOptions {
  /** Після входу: фокус у редактор і перерозкладка Monaco. */
  readonly onEnter?: () => void;
  /** Після виходу: перерозкладка Monaco (фокус повертає сам хук — на кнопку). */
  readonly onExit?: () => void;
}

/** Чи це клавіша перемикача `Ctrl+Shift+F`. */
export function isFullScreenShortcut(event: Pick<KeyboardEvent, 'ctrlKey' | 'metaKey' | 'shiftKey' | 'altKey' | 'code' | 'key'>): boolean {
  return (
    (event.ctrlKey || event.metaKey) &&
    event.shiftKey &&
    !event.altKey &&
    (event.code === 'KeyF' || event.key.toLowerCase() === 'f')
  );
}

function insideDialog(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest('[role="dialog"]') !== null;
}

export function useScriptFullScreen(options: ScriptFullScreenOptions = {}): ScriptFullScreen {
  const [full, setFull] = useState(false);
  const button = useRef<HTMLButtonElement | null>(null);
  const fullRef = useRef(full);
  fullRef.current = full;
  const optionsRef = useRef(options);
  optionsRef.current = options;

  const toggle = useCallback(() => {
    setFull((current) => !current);
  }, []);

  useEffect(() => {
    const onKey = (event: KeyboardEvent): void => {
      if (isFullScreenShortcut(event)) {
        event.preventDefault();
        setFull((current) => !current);
        return;
      }
      if (event.key === 'Escape' && fullRef.current && !event.defaultPrevented && !insideDialog(event.target)) {
        setFull(false);
      }
    };
    window.addEventListener('keydown', onKey);

    return () => {
      window.removeEventListener('keydown', onKey);
    };
  }, []);

  // Вхід/вихід: фокус і перерозкладка — ПІСЛЯ того, як рамка змінилася (кадр після коміту).
  const previous = useRef(full);
  useEffect(() => {
    if (previous.current === full) return undefined;
    previous.current = full;

    // ⚠ Сторінка під рамкою не прокручується, поки код на весь екран.
    const overflow = document.body.style.overflow;
    if (full) document.body.style.overflow = 'hidden';

    const frame = requestAnimationFrame(() => {
      if (full) {
        optionsRef.current.onEnter?.();
      } else {
        optionsRef.current.onExit?.();
        button.current?.focus();
      }
    });

    return () => {
      cancelAnimationFrame(frame);
      if (full) document.body.style.overflow = overflow;
    };
  }, [full]);

  return { full, toggle, button };
}
