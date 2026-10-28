import { useEffect, useRef, useState, type JSX } from 'react';
import { Menu, Tabs } from '@mantine/core';
import { t } from '@/shared/i18n';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import './documentSheetStrip.css';

export interface SheetStripItem {
  readonly code: string;
  readonly name: string;
  readonly state: string;
}

export interface SheetStripProps {
  readonly sheets: readonly SheetStripItem[];
  readonly activeCode: string | undefined;
  readonly onSelect: (code: string) => void;
}

/** Сусідній аркуш у заданому напрямку; `null` — на краю (кнопка вимкнена). */
export function neighbourSheet(
  sheets: readonly { readonly code: string }[],
  activeCode: string | undefined,
  step: -1 | 1,
): string | null {
  const index = sheets.findIndex((sheet) => sheet.code === activeCode);
  if (index < 0) return null;
  return sheets[index + step]?.code ?? null;
}

const Chevron = ({ path }: { readonly path: string }): JSX.Element => (
  <svg width={16} height={16} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.75} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
    <path d={path} />
  </svg>
);

/**
 * Смуга аркушів (✎ ui-sheet-focus, C): «‹ ›» ліворуч, вкладки з тінню на обрізаному краї і прокруткою
 * колесом, «…» — список усіх аркушів зі станами. Вкладки лишаються `role="tablist"` Mantine зі стрілками
 * (roving tabindex) — клавіатурна навігація не змінена.
 *
 * ⚠ У режимі «на весь екран» смуга `inert` разом з усією сторінкою: перехід між аркушами — після виходу.
 */
export function SheetStrip({ sheets, activeCode, onSelect }: SheetStripProps): JSX.Element {
  const list = useRef<HTMLDivElement>(null);
  const [edges, setEdges] = useState({ left: false, right: false });

  const previous = neighbourSheet(sheets, activeCode, -1);
  const next = neighbourSheet(sheets, activeCode, 1);

  // Стан тіней: чи є що прокручувати з кожного боку.
  useEffect(() => {
    const element = list.current;
    if (element === null) return;

    const measure = (): void => {
      const left = element.scrollLeft > 1;
      const right = element.scrollLeft + element.clientWidth < element.scrollWidth - 1;
      setEdges((current) => (current.left === left && current.right === right ? current : { left, right }));
    };

    measure();
    element.addEventListener('scroll', measure, { passive: true });
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    observer?.observe(element);

    // Колесо миші по смузі гортає її вбік (вертикальна дельта → горизонтальна).
    const onWheel = (event: WheelEvent): void => {
      if (element.scrollWidth <= element.clientWidth || Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return;
      event.preventDefault();
      element.scrollLeft += event.deltaY;
    };
    element.addEventListener('wheel', onWheel, { passive: false });

    return () => {
      element.removeEventListener('scroll', measure);
      element.removeEventListener('wheel', onWheel);
      observer?.disconnect();
    };
  }, [sheets.length]);

  // Активна вкладка завжди у видимій зоні смуги.
  useEffect(() => {
    list.current?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]')?.scrollIntoView?.({
      inline: 'nearest',
      block: 'nearest',
    });
  }, [activeCode]);

  return (
    <div className="doc-sheet-bar" data-edge-left={edges.left ? '' : undefined} data-edge-right={edges.right ? '' : undefined}>
      <button
        type="button"
        className="doc-sheet-nav"
        disabled={previous === null}
        aria-label={t('documents.sheets.prev')}
        title={t('documents.sheets.prev')}
        data-testid="sheet-prev"
        onClick={() => {
          if (previous !== null) onSelect(previous);
        }}
      >
        <Chevron path="M15 6l-6 6 6 6" />
      </button>
      <button
        type="button"
        className="doc-sheet-nav"
        disabled={next === null}
        aria-label={t('documents.sheets.next')}
        title={t('documents.sheets.next')}
        data-testid="sheet-next"
        onClick={() => {
          if (next !== null) onSelect(next);
        }}
      >
        <Chevron path="M9 6l6 6-6 6" />
      </button>

      <Tabs.List ref={list} className="doc-sheet-strip" aria-label={t('documents.sheets')} data-doc-sheet-strip="">
        {sheets.map((s) => (
          <Tabs.Tab key={s.code} value={s.code} className="doc-sheet-tab">
            {s.name} <StatusBadge kind="sheet" state={s.state} />
          </Tabs.Tab>
        ))}
      </Tabs.List>

      <Menu position="top-end" withinPortal returnFocus>
        <Menu.Target>
          <button
            type="button"
            className="doc-sheet-nav"
            aria-label={t('documents.sheets.all')}
            title={t('documents.sheets.all')}
            data-testid="sheet-all"
          >
            <Chevron path="M5 12h.01M12 12h.01M19 12h.01" />
          </button>
        </Menu.Target>
        <Menu.Dropdown mah="60vh" style={{ overflowY: 'auto' }} aria-label={t('documents.sheets.all')}>
          {sheets.map((s) => (
            <Menu.Item
              key={s.code}
              data-active={s.code === activeCode ? '' : undefined}
              fw={s.code === activeCode ? 600 : undefined}
              rightSection={<StatusBadge kind="sheet" state={s.state} />}
              onClick={() => {
                onSelect(s.code);
              }}
            >
              {s.name}
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>
    </div>
  );
}
