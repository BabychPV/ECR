import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Timestamp } from '@/shared/ui/Timestamp';
import { formatDate, formatDateTime } from '@/shared/format';

/**
 * `D15-09`, крок `UI-07`: момент часу читабельний на екрані й точний у розмітці.
 *
 * ⛔ Чому набір виглядає саме так. Спокуса — перевірити дослівний рядок
 * («Sep 19, 2026, 6:51 PM»). Це був би тест НЕ продукту, а поточної мови
 * набору й версії ICU у Node: він почервонів би від оновлення Node, нічого не
 * зламавши. Тому очікуване береться з того самого `shared/format`, а окремим
 * твердженням доводиться, що результат ВІДРІЗНЯЄТЬСЯ від сирого входу — інакше
 * рівність лишилася б зеленою й на компоненті, який нічого не форматує.
 */

function show(node: React.ReactElement): void {
  render(node);
}

const Moment = '2026-09-19T18:51:58.275Z';

describe('Timestamp: видиме читабельне, точне — в розмітці', () => {
  it('показує момент мовою набору, а не сирий ISO', () => {
    show(<Timestamp value={Moment} />);

    const node = screen.getByText(formatDateTime(Moment));

    // ⛔ Головне твердження: видимий текст НЕ дорівнює тому, що прийшло.
    expect(node.textContent).not.toBe(Moment);
    expect(node.textContent).not.toMatch(/T\d{2}:\d{2}/);
  });

  it('точне значення лишається в розмітці — доказ не втрачено', () => {
    /*
     * ⛔ Це та половина, заради якої аргумент «у журналі аудиту потрібен сирий
     * ISO» перестає бути аргументом ПРОТИ форматування: значення нікуди не
     * діло́ся, воно в `dateTime` і в `title`.
     */
    show(<Timestamp value={Moment} />);

    const node = document.querySelector('time');

    expect(node?.getAttribute('datetime')).toBe(Moment);
    expect(node?.getAttribute('title')).toBe(Moment);
  });

  it('dateOnly не вигадує години', () => {
    show(<Timestamp value="2026-01-01" dateOnly />);

    const node = screen.getByText(formatDate('2026-01-01'));

    expect(node.textContent).not.toMatch(/\d{1,2}:\d{2}/);
  });

  it('календарна дата не зʼїжджає на добу', () => {
    /*
     * ⛔ Зміряна пастка, а не гіпотеза: `new Date('2026-01-01')` специфікація
     * велить читати як UTC, і в мінусовому зсуві це дає попередній день
     * (`Intl(en, America/New_York)` → «Dec 31, 2025»). Тут перевіряється
     * наслідок, який видно в БУДЬ-ЯКОМУ поясі: розібрана календарна дата — це
     * МІСЦЕВА північ, тобто рівно той самий день і нульова година.
     */
    show(<Timestamp value="2026-01-01" />);

    const node = document.querySelector('time');
    const shown = node?.textContent ?? '';

    expect(shown).toContain(formatDate('2026-01-01'));
    expect(shown, 'календарну дату розібрано як UTC — година «поїхала»').toBe(
      formatDateTime(new Date(2026, 0, 1)),
    );
  });

  it('немає моменту — видима позначка, а не порожня комірка', () => {
    show(<Timestamp value={null} />);

    // ⚠ Порожня комірка читається двояко («немає» чи «не завантажилось») —
    // те саме вже ловили на `DocumentsPage` (UI-walkthrough F6).
    expect(screen.getByText('—')).toBeDefined();
    expect(document.querySelector('time')).toBeNull();
  });

  it('зіпсоване значення показується як є, а не ховається за тире', () => {
    /*
     * ⛔ Інакше зіпсоване значення сервера виглядало б точно так само, як
     * відсутнє, і дефект даних читався б як порожня комірка.
     */
    show(<Timestamp value="не дата" />);

    expect(screen.getByText('не дата')).toBeDefined();
    expect(screen.queryByText('—')).toBeNull();
    expect(document.querySelector('time')).toBeNull();
  });
});
