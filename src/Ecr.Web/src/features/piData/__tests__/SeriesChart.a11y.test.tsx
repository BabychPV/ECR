import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render } from '@testing-library/react';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { SeriesChart } from '../SeriesChart';
import type { RawSeriesView } from '../api';

/**
 * D-463: графік «Даних з PI» в обох темах — без блокуючих порушень axe; `role="img"` з підсумком словами, а
 * значення кошика доступне з клавіатури (стрілки) через живий регіон.
 */
const Series: RawSeriesView = {
  fromUtc: '2026-01-01T00:00:00Z',
  toUtc: '2027-01-01T00:00:00Z',
  bucketSeconds: 63072,
  totalPoints: 3,
  numericPoints: 3,
  buckets: [
    { startUtc: '2026-01-01T00:00:00Z', points: 1, min: '1', max: '1', avg: '1' },
    { startUtc: '2026-01-01T17:31:12Z', points: 2, min: '2', max: '4', avg: '3' },
  ],
};

afterEach(cleanup);

describe('SeriesChart — доступність', () => {
  it.each(Themes)('тема %s: axe без порушень, клавіатура читає кошик', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <SeriesChart series={Series} unit="kg" initialWidth={800} />
      </Shell>,
    );

    const readout = container.querySelector('[data-pi-chart-readout]');
    expect(readout?.getAttribute('aria-live')).toBe('polite');

    // ⚠ jsdom не має розмітки — ширина до виміру задана явно (`initialWidth`), інакше SVG не малюється.
    const svg = container.querySelector('svg[role="img"]');
    expect(svg).not.toBeNull();
    expect(svg?.getAttribute('aria-label')).toContain('piData.chartSummary');
    expect(container.querySelectorAll('path').length).toBeGreaterThan(0);
    fireEvent.keyDown(svg as Element, { key: 'End' });
    expect(readout?.textContent).toContain('piData.chartBucket');

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
