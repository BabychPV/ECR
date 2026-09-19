import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MappingGaps } from '@/features/mapping/MappingGaps';
import { MappingRows } from '@/features/mapping/MappingRows';
import { formatDateTime } from '@/shared/format';
import { testTheme } from '@/test/render';

/**
 * `UI-07`: мітки телеметрії в перегляді мапінгу — з СЕКУНДАМИ.
 *
 * ⛔ Це не «ще одна сторінка під набір». Дефолт `Timestamp` — `timeStyle:
 * 'short'`, тобто без секунд, і для «коли задача почалася» цього досить. Тут
 * ні: мітка точки джерела відрізняється від сусідньої саме секундою, і без
 * них два різні виміри в одній хвилині виглядають ОДНАКОВО — тобто перегляд
 * мапінгу перестає показувати те, заради чого його відкривають (розрив у
 * серії, `ІНТ-3.3`).
 *
 * ⚠ Очікуваний текст береться з `formatDateTime` того ж модуля з тими самими
 * опціями, а не пишеться літералом: літерал був би перевіркою версії ICU у
 * Node. Щоб рівність не стала порожньою, окремо доводиться, що секунди
 * СПРАВДІ видно і що результат відрізняється від дефолтної форми без них.
 */

const Precise = { dateStyle: 'medium', timeStyle: 'medium' } as const;

/** Мітка з НЕНУЛЬОВИМИ секундами — інакше доказ порожній. */
const Moment = '2026-09-19T18:51:58Z';

function show(node: React.ReactElement): void {
  render(<MantineProvider theme={testTheme}>{node}</MantineProvider>);
}

function timeNode(): HTMLElement | null {
  return document.querySelector(`time[datetime="${Moment}"]`);
}

/** Мінімальний перегляд: лише те, що читають ці два компоненти. */
const preview = {
  sourceEntityId: 1,
  code: 'SRC',
  displayName: null,
  fromUtc: '2026-09-19T00:00:00Z',
  toUtc: '2026-09-20T00:00:00Z',
  pointsSeen: 1,
  isTruncated: false,
  fields: [],
  rows: [
    {
      timestamp: Moment,
      sourcePath: 'AF\\Site\\Tag',
      valueNumeric: 12.4,
      valueString: null,
      quality: 'Good',
      targetRowKey: 'R1',
      targetColumnCode: 'C1',
      outcome: 'Mapped',
    },
  ],
  unmappedSourceFields: [{ sourcePath: 'AF\\Site\\Orphan', pointCount: 3, lastSeenUtc: Moment }],
  uncoveredColumns: [],
};

describe('Перегляд мапінгу: мітки телеметрії', () => {
  it('остання мітка незамапленого поля показує секунди', () => {
    show(<MappingGaps preview={preview as never} />);

    const node = timeNode();

    expect(node, 'мітку не намальовано елементом <time>').not.toBeNull();
    expect(node?.textContent).toBe(formatDateTime(Moment, Precise));

    /*
     * ⛔ Обидві половини нижче обов'язкові. Перша доводить, що секунди на
     * екрані Є; друга — що це НЕ дефолтна форма (інакше рівність вище
     * лишилася б зеленою й без `precise`, якби дефолт колись змінили).
     */
    expect(node?.textContent).toMatch(/\d{1,2}:\d{2}:\d{2}/);
    expect(node?.textContent).not.toBe(formatDateTime(Moment));
  });

  it('мітка точки в переліку рядків показує секунди', () => {
    show(<MappingRows preview={preview as never} />);

    const node = timeNode();

    expect(node, 'мітку не намальовано елементом <time>').not.toBeNull();
    expect(node?.textContent).toBe(formatDateTime(Moment, Precise));
    expect(node?.textContent).toMatch(/\d{1,2}:\d{2}:\d{2}/);
  });

  it('точне значення лишається в розмітці', () => {
    show(<MappingRows preview={preview as never} />);

    expect(timeNode()?.getAttribute('datetime')).toBe(Moment);
    expect(timeNode()?.getAttribute('title')).toBe(Moment);
  });
});
