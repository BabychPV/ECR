import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Button, Group, MantineProvider, Modal, Text } from '@mantine/core';
import type { JSX, ReactNode } from 'react';
import { theme } from '@/shared/theme/theme';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';

/**
 * Правило `L6` (директива №15 §0): «Незворотна дія → `ConfirmDialog` із назвою
 * об'єкта в заголовку, дієсловом на кнопці, **фокусом на Cancel**; тест:
 * `document.activeElement` = Cancel».
 *
 * ⛔ Доказ того, що поведінки НЕ БУЛО, — перший опис нижче. Він відтворює
 * рукозібране підтвердження в тому вигляді, у якому воно стоїть на сторінках
 * сьогодні (`PeriodsPage.tsx` — архівація, `MethodologyVersionsPage.tsx` —
 * видалення формули): `<Modal>` + `<Text>` + `<Group>` із «Cancel» і червоною
 * кнопкою, без `data-autofocus`. Заміряно: фокус отримує ХРЕСТИК у шапці
 * (`aria-label="Close"`), бо пастка фокуса Mantine бере перший фокусований
 * вузол у DOM. Тобто обіцянка `L6` там не виконується, і жоден із 1800+
 * тестів цього не перевіряв.
 */

function mount(node: ReactNode): JSX.Element {
  return <MantineProvider theme={theme}>{node}</MantineProvider>;
}

/** Пастка фокуса Mantine переносить фокус у `setTimeout`, не синхронно. */
async function focusSettled(): Promise<void> {
  await waitFor(() => {
    expect(document.activeElement).not.toBe(document.body);
  });
}

afterEach(() => {
  cleanup();
});

describe('L6 — чому цей компонент узагалі потрібен', () => {
  it('рукозібране підтвердження (форма чинних сторінок) фокусує ХРЕСТИК, а не Cancel', async () => {
    render(
      mount(
        <Modal opened onClose={() => {}} title="Archive period 2026-09?">
          <Text size="sm" mb="sm">
            Archiving is irreversible.
          </Text>
          <Group justify="flex-end" mt="md">
            <Button variant="default" onClick={() => {}}>
              Cancel
            </Button>
            <Button color="statusError" onClick={() => {}}>
              Archive period
            </Button>
          </Group>
        </Modal>,
      ),
    );

    await focusSettled();

    /*
     * ⛔ Твердження ПОЗИТИВНЕ — називає, що саме сфокусовано. «Не Cancel»
     * лишалося б зеленим і тоді, коли діалогу немає взагалі (урок #384).
     */
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Close' }));
    expect(document.activeElement).not.toBe(screen.getByRole('button', { name: 'Cancel' }));
  });
});

describe('L6 — ConfirmModal: фокус стоїть на безпечній дії', () => {
  function renderConfirm(
    overrides: Partial<Parameters<typeof ConfirmModal>[0]> = {},
  ): { onConfirm: ReturnType<typeof vi.fn>; onClose: ReturnType<typeof vi.fn> } {
    const onConfirm = vi.fn();
    const onClose = vi.fn();

    render(
      mount(
        <ConfirmModal
          opened
          title="Delete role “Night shift”?"
          text="The role and its grants will be removed."
          verb="Delete role"
          cancelLabel="Cancel"
          onConfirm={onConfirm}
          onClose={onClose}
          {...overrides}
        />,
      ),
    );

    return { onConfirm, onClose };
  }

  it('ФВ-14.16: document.activeElement — саме кнопка Cancel', async () => {
    renderConfirm();
    await focusSettled();

    expect(document.activeElement).toBe(screen.getByTestId('confirm-cancel'));
    expect(screen.getByTestId('confirm-cancel')).toHaveProperty('textContent', 'Cancel');
  });

  it('Enter одразу після відкриття СКАСОВУЄ, а не виконує незворотне', async () => {
    const user = userEvent.setup();
    const { onConfirm, onClose } = renderConfirm();

    await focusSettled();
    await user.keyboard('{Enter}');

    // ⛔ Ось навіщо правило існує. Діалог, у якому `Enter` виконує
    // незворотне, гірший за відсутність діалогу: він створює відчуття
    // захисту, якого немає.
    expect(onConfirm).toHaveBeenCalledTimes(0);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('хрестик лишається окремо названим («Close»), щоб не дублювати Cancel', async () => {
    renderConfirm();
    await focusSettled();

    expect(screen.getByRole('button', { name: 'Close' })).toBeDefined();
    expect(screen.getAllByRole('button', { name: 'Cancel' })).toHaveLength(1);
  });

  it('назва об’єкта в заголовку, дієслово на кнопці', async () => {
    renderConfirm();
    await focusSettled();

    expect(screen.getByText('Delete role “Night shift”?')).toBeDefined();
    expect(screen.getByTestId('confirm-verb').textContent).toBe('Delete role');
  });

  it('confirmDisabled блокує кнопку (передумова сервера), фокус лишається на Cancel', async () => {
    const user = userEvent.setup();
    const { onConfirm } = renderConfirm({ confirmDisabled: true });

    await focusSettled();

    // ⚠ Форма з `PeriodsPage.tsx`: архівувати не можна, доки є відкриті
    // періоди (сервер відповів би `409 ECR-PRD-0409`).
    expect(screen.getByTestId('confirm-verb')).toHaveProperty('disabled', true);
    expect(document.activeElement).toBe(screen.getByTestId('confirm-cancel'));

    await user.click(screen.getByTestId('confirm-verb'));
    expect(onConfirm).toHaveBeenCalledTimes(0);
  });

  it('дзеркало: без confirmDisabled кнопка активна', async () => {
    renderConfirm();
    await focusSettled();

    expect(screen.getByTestId('confirm-verb')).toHaveProperty('disabled', false);
  });

  it('підтвердження викликає onConfirm рівно раз', async () => {
    const user = userEvent.setup();
    const { onConfirm, onClose } = renderConfirm();

    await focusSettled();
    await user.click(screen.getByTestId('confirm-verb'));

    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onClose).toHaveBeenCalledTimes(0);
  });
});

describe('ConfirmModal — наслідки (consequences)', () => {
  function renderWith(consequences: readonly (string | { text: string; note?: boolean })[]): void {
    render(
      mount(
        <ConfirmModal
          opened
          title="Close period 2026-09?"
          verb="Close period"
          cancelLabel="Cancel"
          consequences={consequences}
          onConfirm={() => {}}
          onClose={() => {}}
        />,
      ),
    );
  }

  it('порожній consequences[] НЕ дає порожнього списку (D15-06)', () => {
    renderWith([]);

    expect(screen.queryByTestId('confirm-consequences')).toBeNull();
    expect(screen.queryAllByRole('listitem')).toHaveLength(0);
  });

  it('дзеркало: наслідки є — список є, по рядку на кожен', () => {
    renderWith(['Sheets become read-only.', { text: 'Recalculation stops.', note: true }]);

    expect(screen.getByTestId('confirm-consequences')).toBeDefined();
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByText('Sheets become read-only.')).toBeDefined();
  });

  it('фокус лишається на Cancel і тоді, коли наслідки є', async () => {
    renderWith(['Sheets become read-only.']);
    await focusSettled();

    expect(document.activeElement).toBe(screen.getByTestId('confirm-cancel'));
  });
});

describe('ConfirmModal — typeToConfirm', () => {
  function renderTyped(): ReturnType<typeof vi.fn> {
    const onConfirm = vi.fn();

    render(
      mount(
        <ConfirmModal
          opened
          title="Delete registry “EQUIP”?"
          verb="Delete registry"
          cancelLabel="Cancel"
          typeToConfirm={{ value: 'EQUIP', label: 'Type EQUIP to confirm' }}
          onConfirm={onConfirm}
          onClose={() => {}}
        />,
      ),
    );

    return onConfirm;
  }

  it('кнопка вимкнена, доки назву не введено дослівно', async () => {
    const user = userEvent.setup();
    const onConfirm = renderTyped();

    await focusSettled();
    expect(screen.getByTestId('confirm-verb')).toHaveProperty('disabled', true);

    await user.type(screen.getByTestId('confirm-type-to-confirm'), 'EQUI');
    expect(screen.getByTestId('confirm-verb')).toHaveProperty('disabled', true);

    await user.type(screen.getByTestId('confirm-type-to-confirm'), 'P');
    expect(screen.getByTestId('confirm-verb')).toHaveProperty('disabled', false);

    await user.click(screen.getByTestId('confirm-verb'));
    expect(onConfirm).toHaveBeenCalledTimes(1);
  });

  it('поле має ПІДПИС (ФВ-14.20), а не самий placeholder', async () => {
    renderTyped();
    await focusSettled();

    expect(screen.getByLabelText('Type EQUIP to confirm')).toBe(
      screen.getByTestId('confirm-type-to-confirm'),
    );
  });

  it('ФОКУС однаково на Cancel, а не в полі введення', async () => {
    renderTyped();
    await focusSettled();

    /*
     * ⛔ Саме тут `data-autofocus` і не замінюється «порядком у DOM»: без
     * нього першим фокусованим вузлом тіла був би цей `TextInput`, і людина
     * опинялася б у полі, ще не прочитавши заголовка.
     */
    expect(document.activeElement).toBe(screen.getByTestId('confirm-cancel'));
    expect(document.activeElement).not.toBe(screen.getByTestId('confirm-type-to-confirm'));
  });

  it('без typeToConfirm поля немає взагалі, і кнопка одразу активна', async () => {
    render(
      mount(
        <ConfirmModal
          opened
          title="Delete registry “EQUIP”?"
          verb="Delete registry"
          cancelLabel="Cancel"
          onConfirm={() => {}}
          onClose={() => {}}
        />,
      ),
    );

    await focusSettled();

    expect(screen.queryByTestId('confirm-type-to-confirm')).toBeNull();
    expect(screen.getByTestId('confirm-verb')).toHaveProperty('disabled', false);
  });
});

describe('ConfirmModal — попереднє введення не переїжджає в наступний діалог', () => {
  it('поле очищається при КОЖНОМУ відкритті', async () => {
    const user = userEvent.setup();

    function Host(): JSX.Element {
      return (
        <ConfirmModal
          opened={opened}
          title="Delete registry “EQUIP”?"
          verb="Delete registry"
          cancelLabel="Cancel"
          typeToConfirm={{ value: 'EQUIP', label: 'Type EQUIP to confirm' }}
          onConfirm={() => {}}
          onClose={() => {}}
        />
      );
    }

    let opened = true;
    const view = render(mount(<Host />));

    await focusSettled();
    await user.type(screen.getByTestId('confirm-type-to-confirm'), 'EQUIP');
    expect(screen.getByTestId('confirm-verb')).toHaveProperty('disabled', false);

    opened = false;
    view.rerender(mount(<Host />));
    opened = true;
    view.rerender(mount(<Host />));

    await waitFor(() => {
      expect(screen.getByTestId('confirm-type-to-confirm')).toHaveProperty('value', '');
    });

    // ⛔ Інакше назва об'єкта, набрана для ПОПЕРЕДНЬОГО видалення, робила б
    // кнопку наступного активною ще до того, як хтось прочитав заголовок.
    expect(screen.getByTestId('confirm-verb')).toHaveProperty('disabled', true);
  });
});
