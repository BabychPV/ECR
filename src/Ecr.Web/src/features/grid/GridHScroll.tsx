import { useEffect, useRef, useState, type JSX, type PointerEvent as ReactPointerEvent, type WheelEvent as ReactWheelEvent } from 'react';
import { t } from '@/shared/i18n';
import './gridHScroll.css';

/** Власний горизонтальний скрол RevoGrid, який віддає позицію (`scrollLeft`) — штатний «віртуальний» скрол сітки. */
/** Найменша ширина повзунка, px (щоб за дуже широкої таблиці його було чим узяти). */
export const ThumbMinWidth = 24;

export const GridScrollSelector = 'revogr-scroll-virtual.horizontal';

/** Ширина внутрішнього блока нативної смуги так, щоб її повзунок мав ту саму частку, що й у сітки. */
export function spacerWidth(trackClient: number, gridClient: number, gridScrollWidth: number): number {
  if (trackClient <= 0 || gridClient <= 0 || gridScrollWidth <= gridClient) return 0;
  return (trackClient * gridScrollWidth) / gridClient;
}

/** Переведення позиції з однієї смуги в іншу за діапазонами (0 — прокручувати нічого). */
export function mapScroll(position: number, fromRange: number, toRange: number): number {
  if (fromRange <= 0 || toRange <= 0) return 0;
  return Math.min(toRange, Math.max(0, (position * toRange) / fromRange));
}

export interface GridHScrollProps {
  /** Хост сітки, чия прокрутка зараз керується смугою (вибрана таблиця); `null` — ще немає. */
  readonly resolveHost: () => HTMLElement | null;
  /** Корінь, у якому з'являється сітка (для очікування її створення). */
  readonly container: () => HTMLElement | null;
  /** Зміна цього значення перепідключає смугу до іншого хоста (вибрана таблиця, аркуш). */
  readonly watch: unknown;
}

/**
 * Горизонтальна прокрутка таблиці в нижньому рядку (✎ ui-sheet-bottom): нативний scrollbar-контейнер,
 * синхронізований зі штатним скролом RevoGrid (`revogr-scroll-virtual.horizontal`), який CSS виводить із
 * потоку й ховає. Прокрутка колесом/тачпадом/Shift+колесо по сітці лишається за RevoGrid; смуга лише
 * відбиває її положення і керує ним.
 *
 * ⚠ Позиції переводяться ПРОПОРЦІЙНО діапазонам (смуга вужча за сітку): ехо-події (смуга вже там, де їй
 * належить за положенням сітки, з точністю до 1 px смуги) нічого не роблять — інакше округлення давало б
 * «тремтіння» сітки.
 */
export function GridHScroll({ resolveHost, container, watch }: GridHScrollProps): JSX.Element {
  const track = useRef<HTMLDivElement>(null);
  const inner = useRef<HTMLDivElement>(null);
  const rail = useRef<HTMLDivElement>(null);
  const thumb = useRef<HTMLDivElement>(null);
  const drag = useRef<{ startX: number; startLeft: number } | null>(null);
  const [scrollable, setScrollable] = useState(false);

  useEffect(() => {
    const trackEl = track.current;
    const innerEl = inner.current;
    const railEl = rail.current;
    const thumbEl = thumb.current;
    if (trackEl === null || innerEl === null || railEl === null || thumbEl === null) return;

    let detach: (() => void) | null = null;
    let waiting: MutationObserver | null = null;

    const attach = (source: HTMLElement): (() => void) => {
      let nudges: number[] = [];

      const ranges = (): { grid: number; track: number } => ({
        grid: source.scrollWidth - source.clientWidth,
        track: trackEl.scrollWidth - trackEl.clientWidth,
      });

      // ⚠ Без прапорців «ігнорувати ехо»: вони «застрягали» (подія не прийшла — прапорець лишався й ковтав
      // справжню). Замість них — мертва зона: смуга вже стоїть там, де їй «належить» стояти за положенням сітки
      // (з точністю до одного пікселя смуги), — отже подія є ехом і нічого не робить.
      const expectedTrack = (): number => {
        const { grid, track: own } = ranges();
        return mapScroll(source.scrollLeft, grid, own);
      };

      const fromSource = (): void => {
        const target = expectedTrack();
        if (Math.abs(trackEl.scrollLeft - target) < 1) return;
        trackEl.scrollLeft = target;
      };

      // Власний повзунок (✎ ui-sheet-bottom): нативний скролбар на overlay-системах (macOS, headless) не малюється,
      // тож доріжка й повзунок — звичайні елементи, положення яких рахується з метрик нативного контейнера.
      const paint = (): void => {
        const range = trackEl.scrollWidth - trackEl.clientWidth;
        const railWidth = railEl.clientWidth;
        if (range <= 1 || railWidth <= 0 || trackEl.scrollWidth <= 0) {
          thumbEl.style.display = 'none';
          return;
        }
        const width = Math.max(ThumbMinWidth, (railWidth * trackEl.clientWidth) / trackEl.scrollWidth);
        const left = (trackEl.scrollLeft / range) * (railWidth - width);
        thumbEl.style.display = 'block';
        thumbEl.style.width = `${String(width)}px`;
        thumbEl.style.transform = `translateX(${String(Math.max(0, left))}px)`;
      };

      const layout = (): void => {
        const can = source.scrollWidth - source.clientWidth > 1 && source.clientWidth > 0;
        setScrollable(can);
        innerEl.style.width = `${String(can ? spacerWidth(trackEl.clientWidth, source.clientWidth, source.scrollWidth) : 0)}px`;
        fromSource();
        paint();
      };

      const onSource = (): void => {
        fromSource();
      };

      const onTrack = (): void => {
        if (Math.abs(trackEl.scrollLeft - expectedTrack()) < 1) return;
        const { grid, track: own } = ranges();
        const target = mapScroll(trackEl.scrollLeft, own, grid);
        if (Math.abs(source.scrollLeft - target) < 0.5) return;
        source.scrollLeft = target;
        // ⚠ RevoGrid відкидає нативну подію scroll, що прийшла за < 10 мс після його власної прокрутки колесом, і не
        // повторює її (LocalScrollTimer.isReady) — тоді сітка стояла б, хоч смуга вже зсунулась. Повторюємо подію
        // трохи згодом: якщо перша спрацювала, друга — дублікат (та сама координата) і ігнорується.
        for (const timer of nudges) window.clearTimeout(timer);
        nudges = [40, 160].map((delay) => window.setTimeout(() => source.dispatchEvent(new Event('scroll')), delay));
      };
      source.addEventListener('scroll', onSource, { passive: true });
      trackEl.addEventListener('scroll', onTrack, { passive: true });
      trackEl.addEventListener('scroll', paint, { passive: true });
      const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(layout);
      observer?.observe(source);
      observer?.observe(trackEl);
      observer?.observe(railEl);
      if (source.firstElementChild !== null) observer?.observe(source.firstElementChild);
      layout();

      return () => {
        source.removeEventListener('scroll', onSource);
        trackEl.removeEventListener('scroll', onTrack);
        trackEl.removeEventListener('scroll', paint);
        for (const timer of nudges) window.clearTimeout(timer);
        observer?.disconnect();
        setScrollable(false);
        innerEl.style.width = '0px';
        thumbEl.style.display = 'none';
      };
    };

    const tryBind = (): boolean => {
      const source = resolveHost()?.querySelector<HTMLElement>(GridScrollSelector) ?? null;
      if (source === null) return false;
      detach = attach(source);
      return true;
    };

    if (!tryBind()) {
      // Сітка (веб-компонент) з'являється після завантаження чанка й першого зрізу.
      const root = container();
      if (root !== null) {
        waiting = new MutationObserver(() => {
          if (tryBind()) {
            waiting?.disconnect();
            waiting = null;
          }
        });
        waiting.observe(root, { childList: true, subtree: true });
      }
    }

    return () => {
      waiting?.disconnect();
      detach?.();
    };
    // `resolveHost` і `container` — замикання батька, що читають refs; перепідключення робить `watch`.
  }, [watch]);

  /** Перетягування повзунка: зсув курсора переводиться в зсув нативного контейнера за ставленням діапазонів. */
  const onThumbDown = (event: ReactPointerEvent<HTMLDivElement>): void => {
    const trackEl = track.current;
    if (trackEl === null) return;
    event.stopPropagation();
    drag.current = { startX: event.clientX, startLeft: trackEl.scrollLeft };
    event.currentTarget.setPointerCapture?.(event.pointerId);
  };

  const onThumbMove = (event: ReactPointerEvent<HTMLDivElement>): void => {
    const trackEl = track.current;
    const railEl = rail.current;
    const thumbEl = thumb.current;
    if (drag.current === null || trackEl === null || railEl === null || thumbEl === null) return;
    const free = railEl.clientWidth - thumbEl.offsetWidth;
    if (free <= 0) return;
    const range = trackEl.scrollWidth - trackEl.clientWidth;
    trackEl.scrollLeft = drag.current.startLeft + ((event.clientX - drag.current.startX) * range) / free;
  };

  const onThumbUp = (event: ReactPointerEvent<HTMLDivElement>): void => {
    drag.current = null;
    event.currentTarget.releasePointerCapture?.(event.pointerId);
  };

  /** Клік по вільній частині доріжки — на сторінку (ширину видимої частини) в той бік. */
  const onRailDown = (event: ReactPointerEvent<HTMLDivElement>): void => {
    const trackEl = track.current;
    const thumbEl = thumb.current;
    if (trackEl === null || thumbEl === null || event.target !== event.currentTarget) return;
    const thumbBox = thumbEl.getBoundingClientRect();
    trackEl.scrollLeft += (event.clientX < thumbBox.left ? -1 : 1) * trackEl.clientWidth;
  };

  /** Колесо над доріжкою гортає так само, як над сіткою. */
  const onRailWheel = (event: ReactWheelEvent<HTMLDivElement>): void => {
    const trackEl = track.current;
    if (trackEl === null) return;
    trackEl.scrollLeft += event.deltaX !== 0 ? event.deltaX : event.deltaY;
  };

  return (
    <div className="ecr-hscroll-wrap">
      <div
        ref={track}
        className="ecr-hscroll"
        role="group"
        aria-label={t('grid.hscroll.label')}
        tabIndex={scrollable ? 0 : -1}
        data-testid="grid-hscroll"
        data-scrollable={scrollable ? 'true' : 'false'}
      >
        <div ref={inner} className="ecr-hscroll-inner" />
      </div>
      <div
        ref={rail}
        className="ecr-hscroll-rail"
        aria-hidden="true"
        data-testid="grid-hscroll-rail"
        data-scrollable={scrollable ? 'true' : 'false'}
        onPointerDown={onRailDown}
        onWheel={onRailWheel}
      >
        <div
          ref={thumb}
          className="ecr-hscroll-thumb"
          data-testid="grid-hscroll-thumb"
          onPointerDown={onThumbDown}
          onPointerMove={onThumbMove}
          onPointerUp={onThumbUp}
          onPointerCancel={onThumbUp}
        />
      </div>
    </div>
  );
}