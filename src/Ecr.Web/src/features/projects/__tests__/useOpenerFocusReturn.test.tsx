import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Button, MantineProvider, Modal } from '@mantine/core';
import { Suspense, lazy, useState, type JSX } from 'react';
import { theme } from '@/shared/theme/theme';
import { useOpenerFocusReturn } from '@/features/projects/useOpenerFocusReturn';

/**
 * Лінивий діалог (`D-132`) і повернення фокуса після Escape (WCAG 2.4.3).
 *
 * ⚠ Діалог тут — та сама форма, що в `PeriodsPage.tsx`: чанк вантажиться
 * `lazy()`, діалог монтується з першого відкриття вже з `opened = true`.
 * Перший опис доводить, що без хука фокус губиться (саме це впіймав
 * `keyboardPath.spec.ts`); другий — що хук його повертає.
 */

const LazyDialog = lazy(async () => {
  await Promise.resolve();

  return {
    default: ({ opened, onClose }: { opened: boolean; onClose: () => void }): JSX.Element => (
      <Modal opened={opened} onClose={onClose} title="Lazy dialog" transitionProps={{ duration: 0 }}>
        <input aria-label="Name" />
      </Modal>
    ),
  };
});

function Page({ withHook }: { withHook: boolean }): JSX.Element {
  const [opened, setOpened] = useState(false);
  const [mounted, setMounted] = useState(false);
  const focus = useOpenerFocusReturn();

  return (
    <MantineProvider theme={theme}>
      <Button
        onClick={() => {
          if (withHook) focus.remember();
          setMounted(true);
          setOpened(true);
        }}
      >
        New project
      </Button>
      {mounted && (
        <Suspense fallback={null}>
          <LazyDialog
            opened={opened}
            onClose={() => {
              setOpened(false);
              if (withHook) focus.restore();
            }}
          />
        </Suspense>
      )}
    </MantineProvider>
  );
}

async function openAndEscape(): Promise<HTMLElement> {
  const user = userEvent.setup();
  const opener = screen.getByRole('button', { name: 'New project' });

  opener.focus();
  await user.keyboard('{Enter}');
  await screen.findByRole('dialog');
  await waitFor(() => {
    expect(document.activeElement).not.toBe(opener);
  });

  await user.keyboard('{Escape}');
  await waitFor(() => {
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  return opener;
}

afterEach(() => {
  cleanup();
});

describe('лінивий діалог: фокус після Escape', () => {
  it('без хука перше відкриття губить фокус — Mantine не бачив зміни opened', async () => {
    render(<Page withHook={false} />);

    const opener = await openAndEscape();

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(document.activeElement).not.toBe(opener);
  });

  it('з хуком фокус повертається на кнопку-відкривач', async () => {
    render(<Page withHook />);

    const opener = await openAndEscape();

    await waitFor(() => {
      expect(document.activeElement).toBe(opener);
    });
  });
});
