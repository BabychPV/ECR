import { useEffect, useLayoutEffect, useRef, type JSX, type KeyboardEvent, type RefObject } from 'react';
import { formatMonthName, formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { PeriodStateIcon, periodStateText, type PeriodStateSummary } from './periodStates';

/** Колонок у сітці (макет `.months`: 3 × 4); у вузькому вікні CSS дає 2 — тоді рахуємо з розкладки. */
const Columns = 3;
/** Відступ випадайки від країв вікна, px (`max-width = вікно − 16 px`). */
const ViewportGutter = 8;

/** Скільки стовпців сітка має насправді (CSS-медіазапит може дати 2); jsdom без розкладки — `Columns`. */
function columnsOf(element: HTMLElement | null): number {
  if (element === null) return Columns;
  const template = getComputedStyle(element).gridTemplateColumns.trim();
  const count = template === '' || template === 'none' ? 0 : template.split(/\s+/).length;

  return count > 0 && count <= Columns ? count : Columns;
}

/**
 * Випадайка вибору періоду (UI-13, макет `48-period-picker-open.png`,
 * `kit.js` `PeriodPicker`): заголовок «Reporting period · 2026» і сітка
 * періодів року зі значком стану.
 *
 * ⚠ Лінивим чанком (`PeriodPicker.tsx`): у статичний бандл маршрутів іде лише
 * сам контрол, сітка — при першому відкритті (бюджет `D-132`).
 *
 * ⛔ Власне позиціонування (абсолютно під контролом), а не `Popover` Mantine:
 * той тягне `@floating-ui/react` — +8 КБ gzip до сторінки, як із `Tooltip`
 * у меню (`NavRouteLink.tsx`).
 *
 * Клавіатура: стрілки — сусідній період (вгору/вниз — на ряд), Home/End —
 * перший/останній, Enter/Space — обрати, Esc — закрити й повернути фокус на
 * контрол, Tab — закрити й іти далі.
 */
export default function PeriodMonthGrid({
  year,
  perYear,
  periodKind,
  value,
  states,
  allowed,
  anchor,
  onPick,
  onClose,
  focusOnOpen,
}: {
  year: number;
  perYear: number;
  periodKind: string | undefined;
  value: number | null;
  states: ReadonlyMap<number, PeriodStateSummary> | undefined;
  /** Допустимі періоди проєкту; решта місяців вимкнена. undefined - усі. */
  allowed?: ReadonlySet<number> | undefined;
  /** Обгортка контрола: клік поза нею закриває сітку. */
  anchor: RefObject<HTMLElement | null>;
  onPick: (periodKey: number) => void;
  /** `returnFocus` — повернути фокус на контрол (Esc), а не лишити там, куди пішла людина. */
  onClose: (returnFocus: boolean) => void;
  /** Перший фокус — на обраний період (відкрито кнопкою чи стрілкою); `false` — фокус лишається в полі, де людина друкує. */
  focusOnOpen: boolean;
}): JSX.Element {
  const keys = Array.from({ length: perYear }, (_, index) => year * 100 + index + 1);
  const grid = useRef<HTMLDivElement>(null);
  const pop = useRef<HTMLDivElement>(null);

  /*
   * ⛔ Випадайка завжди в межах вікна (живий стенд, вікно ~600 px: третій стовпець сітки
   * — березень/червень/вересень/грудень — обрізався правим краєм). Власне позиціонування
   * (без `Popover`, див. вище) тепер має й колізії: зсув уліво, щоб правий край не виходив
   * за `вікно − ViewportGutter`, але не лівіше за `ViewportGutter`; знизу бракує місця, а
   * згори є — відкривається вгору. Ширину обмежує CSS (`min(18.75rem, 100vw − 16px)`).
   */
  useLayoutEffect(() => {
    const element = pop.current;
    if (element === null) return undefined;

    const place = (): void => {
      element.style.left = '0px';
      element.style.top = '';
      element.style.bottom = '';
      const rect = element.getBoundingClientRect();
      const width = document.documentElement.clientWidth || window.innerWidth;
      const height = window.innerHeight;
      let shift = 0;
      if (rect.right > width - ViewportGutter) shift = width - ViewportGutter - rect.right;
      if (rect.left + shift < ViewportGutter) shift = ViewportGutter - rect.left;
      element.style.left = `${shift}px`;

      const anchorRect = anchor.current?.getBoundingClientRect();
      if (anchorRect !== undefined && rect.bottom > height && anchorRect.top - rect.height - 4 >= 0) {
        element.style.top = 'auto';
        element.style.bottom = 'calc(100% + 4px)';
      }
    };

    place();
    window.addEventListener('resize', place);

    return () => window.removeEventListener('resize', place);
  }, [anchor]);

  useEffect(() => {
    const close = (event: MouseEvent): void => {
      if (anchor.current !== null && !anchor.current.contains(event.target as Node)) onClose(false);
    };
    document.addEventListener('mousedown', close);

    return () => document.removeEventListener('mousedown', close);
  }, [anchor, onClose]);

  useEffect(() => {
    if (!focusOnOpen) return;
    const buttons = Array.from(grid.current?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)') ?? []);
    (buttons.find((button) => button.getAttribute('aria-current') === 'true') ?? buttons[0])?.focus();
    // ⚠ Лише при відкритті: зміна значення стрілкою не має висмикувати фокус.
  }, []);

  const focusAt = (index: number): void => {
    const buttons = grid.current?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)');
    if (buttons === undefined || buttons.length === 0) return;
    buttons[(index + buttons.length) % buttons.length]?.focus();
  };

  const handleKey = (event: KeyboardEvent<HTMLDivElement>): void => {
    const buttons = Array.from(grid.current?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)') ?? []);
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    const columns = columnsOf(grid.current);
    const move: Record<string, number> = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: columns, ArrowUp: -columns };

    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      onClose(true);
    } else if (event.key === 'Tab') {
      onClose(false);
    } else if (event.key === 'Home') {
      event.preventDefault();
      focusAt(0);
    } else if (event.key === 'End') {
      event.preventDefault();
      focusAt(buttons.length - 1);
    } else if (move[event.key] !== undefined && index >= 0) {
      event.preventDefault();
      focusAt(index + (move[event.key] ?? 0));
    }
  };

  return (
    <div
      className="ecr-period-pop"
      ref={pop}
      role="dialog"
      aria-label={t('period.choose')}
      data-testid="period-grid"
      onKeyDown={handleKey}
    >
      <div className="ecr-period-pop-title">{t('period.gridTitle', { year: String(year) })}</div>
      <div className="ecr-period-months" ref={grid}>
        {keys.map((key) => {
          const summary = states?.get(key);
          const full = formatPeriodKey(key, periodKind);
          const name = perYear === 12 ? formatMonthName(key % 100) : full;

          return (
            <button
              key={key}
              type="button"
              aria-current={key === value ? 'true' : 'false'}
              disabled={allowed !== undefined && !allowed.has(key)}
              aria-label={summary === undefined ? full : `${full}, ${periodStateText(summary)}`}
              title={summary === undefined ? undefined : periodStateText(summary)}
              onClick={() => onPick(key)}
              data-period-key={key}
            >
              <span>{name}</span>
              {summary !== undefined && <PeriodStateIcon state={summary.state} />}
            </button>
          );
        })}
      </div>
    </div>
  );
}
