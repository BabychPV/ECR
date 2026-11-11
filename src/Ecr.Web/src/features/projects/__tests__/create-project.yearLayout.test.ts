import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { EcrApiError } from '@/api/client';
import { problemText } from '@/shared/ui/problemText';

/**
 * ✎ D-474, п. 4: річний проєкт на версії `PerMonthCopies` сервер відхиляє `422 ECR-TMPL-0422`
 * (`messageKey = err.ECR-TMPL-0422.yearlyProjectNeedsYearLayout`).
 *
 * ⚠ Форма створення проєкту показує відмову загальним шляхом (`onError: showApiError` → `problemText`), окремої
 * обробки цього коду немає й не потрібно. Рендер самої форми в jsdom коштує хвилини (`create-project-modal.render.test.tsx`,
 * Q-298), тож тут перевіряється те, що потрапляє в тост: подробиця з `messageKey` показується, а не ховається.
 *
 * ⛔ Мутаційний доказ: прибрати `messageKey` з відповіді — `detail` стає `null` (сирий текст ховається), і перше
 * твердження червоніє; замінити `onError: showApiError` у формі на щось інше — червоніє друге.
 */
function yearlyRejected(): EcrApiError {
  return new EcrApiError({
    title: 'Unprocessable entity',
    detail: 'A yearly project needs a template version with the "Year, months in columns" layout.',
    status: 422,
    errorCode: 'ECR-TMPL-0422',
    correlationId: 'cid-yearly-1',
    extensions2: { messageKey: 'err.ECR-TMPL-0422.yearlyProjectNeedsYearLayout' },
  });
}

describe('CreateProjectModal: річний проєкт на версії «копія на місяць» (D-474)', () => {
  it('422 з messageKey — тост несе зрозумілий текст сервера і код', () => {
    const shown = problemText(yearlyRejected());

    expect(shown.detail).toBe('A yearly project needs a template version with the "Year, months in columns" layout.');
    expect(shown.code).toBe('ECR-TMPL-0422');
    expect(shown.suppressed).toBeNull();
  });

  it('форма створення віддає відмову саме загальному показу (`showApiError`)', () => {
    const source = readFileSync(path.resolve(__dirname, '../CreateProjectModal.tsx'), 'utf8');

    expect(source).toMatch(/onError:\s*showApiError/);
  });
});
