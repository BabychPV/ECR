import { cleanup, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { afterEach, describe, expect, it } from 'vitest';
import { testTheme } from '@/test/render';
import { AssignmentsMatrix } from '../AssignmentsMatrix';
import type { AssignmentRecipient } from '../types';
import { assignment } from './fixture';

/**
 * RC20 D-2: матриця називає виконавця, який не бачить документ/аркуш (`visibility: NotVisible`), - інакше власник
 * вважав би, що передача діє. `Visible` і `Unknown` (групи AD невідомі) не позначаються.
 */
function recipient(userId: number, displayName: string, visibility: string): AssignmentRecipient {
  return {
    userId,
    displayName,
    status: 'Assigned',
    isActive: true,
    filledAt: null,
    returnCount: 0,
    returnReason: null,
    returnedAt: null,
    visibility,
  };
}

afterEach(cleanup);

describe('AssignmentsMatrix — видимість виконавця', () => {
  it('позначає лише NotVisible і не плутає Visible та Unknown з відмовою', () => {
    const march = assignment({
      id: 5,
      slice: { monthNumber: 3 },
      recipients: [
        recipient(1, 'Visible Vira', 'Visible'),
        recipient(2, 'Hidden Hanna', 'NotVisible'),
        recipient(3, 'Unknown Ulas', 'Unknown'),
      ],
    });

    render(
      <MantineProvider theme={testTheme}>
        <AssignmentsMatrix view="months" assignments={[march]} actions={undefined} />
      </MantineProvider>,
    );

    const marks = screen.getAllByTestId('recipient-not-visible');
    expect(marks).toHaveLength(1);
    expect(marks[0]?.textContent).toContain('⟦editAssign.notVisible⟧');
    // Підказка доступна не лише через title: читалка бачить її текстом.
    expect(within(marks[0] as HTMLElement).getByText('⟦editAssign.notVisibleHint⟧')).toBeDefined();
    // Позначка стоїть біля Hidden Hanna, а не біля інших.
    expect(marks[0]?.closest('div')?.textContent).toContain('Hidden Hanna');
  });
});
