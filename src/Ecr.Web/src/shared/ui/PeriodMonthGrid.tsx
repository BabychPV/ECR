import { useEffect, useRef, type JSX, type KeyboardEvent, type RefObject } from 'react';
import { formatMonthName, formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { PeriodStateIcon, periodStateText, type PeriodStateSummary } from './periodStates';

/** Колонок у сітці (макет `.months`: 3 × 4). */
const Columns = 3;

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

  useEffect(() => {
    const close = (event: MouseEvent): void => {
      if (anchor.current !== null && !anchor.current.contains(event.target as Node)) onClose(false);
    };
    document.addEventListener('mousedown', close);

    return () => document.removeEventListener('mousedown', close);
  }, [anchor, onClose]);

  useEffect(() => {
    if (!focusOnOpen) return;
    const buttons = Array.from(grid.current?.querySelectorAll<HTMLButtonElement>('button') ?? []);
    (buttons.find((button) => button.getAttribute('aria-current') === 'true') ?? buttons[0])?.focus();
    // ⚠ Лише при відкритті: зміна значення стрілкою не має висмикувати фокус.
  }, []);

  const focusAt = (index: number): void => {
    const buttons = grid.current?.querySelectorAll<HTMLButtonElement>('button');
    if (buttons === undefined || buttons.length === 0) return;
    buttons[(index + buttons.length) % buttons.length]?.focus();
  };

  const handleKey = (event: KeyboardEvent<HTMLDivElement>): void => {
    const buttons = Array.from(grid.current?.querySelectorAll<HTMLButtonElement>('button') ?? []);
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    const move: Record<string, number> = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: Columns, ArrowUp: -Columns };

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
