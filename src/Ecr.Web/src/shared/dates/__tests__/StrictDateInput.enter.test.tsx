import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithMantine } from '@/test/render';
import { StrictDateInput } from '@/shared/dates/StrictDateInput';

/**
 * A3-01: недійсна дата, набрана з клавіатури, пояснюється одразу після Enter/Escape, а не лише після
 * виходу з поля. Приймальний прохід: «Enter/Tab → текст лишається, Save неактивний, aria-invalid=false».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  language: () => 'en',
}));

afterEach(cleanup);

function field(): HTMLInputElement {
  return screen.getByLabelText('Date');
}

describe('StrictDateInput: відмова після Enter/Tab (A3-01)', () => {
  it.each([['{Enter}'], ['{Escape}'], ['{Tab}']])('31.02.2026 + %s — aria-invalid і текст відмови', async (key) => {
    const user = userEvent.setup();
    renderWithMantine(<StrictDateInput label="Date" value={null} onChange={() => undefined} />);

    await user.click(field());
    await user.type(field(), `31.02.2026${key}`);

    expect(field().getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByText('⟦dates.invalid (value=31.02.2026)⟧')).toBeTruthy();
  });
});
