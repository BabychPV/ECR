import { describe, expect, it } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TextInput } from '@mantine/core';
import type { JSX } from 'react';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Wizard, type WizardStep } from '@/shared/ui/Wizard';

/**
 * Доступність `Wizard` в обох темах (`ФВ-14.16`, `D-127`).
 *
 * ⛔ Окремий файл саме тут, а не рядок у `accessibility.part*.a11y.test.tsx`:
 * ті скануть МАРШРУТИ, а майстер поки не стоїть на жодному — його підключає
 * інтегратор (`KitchenSinkPage`, крок `UI-06`). Без цього файлу гейти
 * `a11y (dark)`/`a11y (light)` лишалися б зеленими, НЕ побачивши нового
 * компонента взагалі, — тобто «зелено» означало б «не перевіряли».
 *
 * ⚠ Скануються ДВА стани, не один: крок із банером помилки (там з'являється
 * жива область `role="alert"` і фокус їде на поле) і крок `Review`. Стан
 * «щойно відкрили» найдешевший і найменш показовий.
 */

interface Draft {
  readonly name: string;
}

const steps: readonly WizardStep<Draft>[] = [
  {
    id: 'name',
    label: 'Details',
    hint: 'Shown in the list of versions.',
    render: ({ data, update }) => (
      <TextInput
        id="field-name"
        label="Name"
        value={data.name}
        onChange={(event) => update({ name: event.currentTarget.value })}
      />
    ),
    validate: (data) =>
      data.name.trim().length === 0 ? { message: 'Name is required.', focus: '#field-name' } : null,
  },
];

function mount(colorScheme: 'light' | 'dark'): JSX.Element {
  return (
    <Shell colorScheme={colorScheme}>
      <Wizard<Draft>
        opened
        title="Publish version 3"
        initialData={{ name: '' }}
        steps={steps}
        summary={(data) => <span>{data.name}</span>}
        reviewText="Check before publishing."
        applyLabel="Publish version"
        labels={{ back: 'Back', next: 'Next', review: 'Review', cancel: 'Cancel' }}
        onApply={() => {}}
        onClose={() => {}}
        onExitUnsaved={() => true}
      />
    </Shell>
  );
}

describe('Wizard — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: крок із банером помилки — без блокуючих порушень`, async () => {
      const user = userEvent.setup();
      render(mount(colorScheme));

      await waitFor(() => {
        expect(document.activeElement).not.toBe(document.body);
      });

      await user.click(screen.getByTestId('wizard-next'));
      expect(screen.getByTestId('wizard-step-error')).toBeDefined();

      const violations = await findViolations(screen.getByRole('dialog'));
      expect(violations, describeViolations(violations)).toHaveLength(0);

      cleanup();
    });

    it(`${colorScheme}: крок Review — без блокуючих порушень`, async () => {
      const user = userEvent.setup();
      render(mount(colorScheme));

      await waitFor(() => {
        expect(document.activeElement).not.toBe(document.body);
      });

      await user.type(screen.getByLabelText('Name'), 'Version 3');
      await user.click(screen.getByTestId('wizard-next'));
      expect(screen.getByTestId('wizard-summary')).toBeDefined();

      const violations = await findViolations(screen.getByRole('dialog'));
      expect(violations, describeViolations(violations)).toHaveLength(0);

      cleanup();
    });
  }
});
