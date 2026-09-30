import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { PipelineStepCard } from '@/features/pipeline/PipelineStepCard';
import type { PipelineStepState } from '@/features/pipeline/pipelineSteps';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';

/**
 * Кроки конвеєра (`ФВ-14.3`, область 9) — axe без блокуючих порушень в обох
 * темах (`ФВ-14.16`), зокрема підсвічений крок «звужує до нуля» і бейдж
 * кожного стану.
 *
 * ⚠ Картки перевіряються окремо від сторінки: маршрут `/admin/pipeline` в
 * a11y-наборі сканується без обраної сутності, тобто без кроків, а вміст
 * кроків (розклад, мапінги) уже має власні a11y-тести.
 */
const States: readonly PipelineStepState[] = ['ok', 'zero', 'idle', 'warn', 'off', 'error'];

afterEach(cleanup);

describe('Кроки конвеєра — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: усі стани, один крок підсвічено', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        {States.map((state, index) => (
          <PipelineStepCard
            key={state}
            step={{ key: 'map', state, points: state === 'zero' ? 0 : 12 }}
            index={index + 1}
            title={`Step ${state}`}
            hint="What the step does."
            zeroHint="Collected rows fall under no active mapping."
          />
        ))}
      </Shell>,
    );

    // Підсвітка справді на екрані — інакше зелений axe нічого б не доводив.
    expect(container.querySelectorAll('[data-narrowed="true"]')).toHaveLength(1);

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
