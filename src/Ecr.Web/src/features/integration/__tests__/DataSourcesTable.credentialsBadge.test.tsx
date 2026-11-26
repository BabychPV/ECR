import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CredentialsStateBadge } from '@/features/integration/CredentialsStateBadge';
import type { DataSource } from '@/features/integration/dataSourceApi';

/** Бейдж стану облікових даних (P-1126-SRCCRED): лише для Rejected/Unreadable, повний текст у `title`. */
const source = (state: string): DataSource => ({ credentialsState: state }) as unknown as DataSource;

describe('CredentialsStateBadge', () => {
  it.each([
    ['Rejected', '⟦sources.credentialsRejected⟧', '⟦sources.credentialsRejectedBadge⟧'],
    ['Unreadable', '⟦sources.credentialsUnreadable⟧', '⟦sources.credentialsUnreadableBadge⟧'],
  ])('%s: видимий короткий підпис, повний текст у title', (state, text, label) => {
    render(
      <MantineProvider>
        <CredentialsStateBadge source={source(state)} />
      </MantineProvider>,
    );

    const badge = document.querySelector(`[data-credentials-state-badge="${state}"]`);
    expect(badge?.textContent).toBe(label);
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
