import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CredentialsStateBadge } from '@/features/integration/CredentialsStateBadge';
import type { DataSource } from '@/features/integration/dataSourceApi';

/** Бейдж стану облікових даних (P-1126-SRCCRED): лише для Rejected/Unreadable, повний текст у `title`. */
const source = (state: string): DataSource => ({ credentialsState: state }) as unknown as DataSource;

describe('CredentialsStateBadge', () => {
  it.each([
    ['Rejected', '⟦sources.credentialsRejected⟧'],
    ['Unreadable', '⟦sources.credentialsUnreadable⟧'],
  ])('%s показує бейдж із повним текстом стану', (state, text) => {
    render(
      <MantineProvider>
        <CredentialsStateBadge source={source(state)} />
      </MantineProvider>,
    );

    const badge = document.querySelector(`[data-credentials-state-badge="${state}"]`);
    expect(badge?.getAttribute('title')).toBe(text);
    expect(screen.getByLabelText(new RegExp(text.replace(/[⟦⟧.]/g, '.')))).toBeTruthy();
  });

  it('Ok бейджа не малює', () => {
    render(
      <MantineProvider>
        <CredentialsStateBadge source={source('Ok')} />
      </MantineProvider>,
    );

    expect(document.querySelector('[data-credentials-state-badge]')).toBeNull();
  });
});
