import { afterEach, describe, expect, it } from 'vitest';
import type { JSX } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { LocalDraft } from '@/features/templates/LocalDraft';
import { hasUnsavedChanges } from '@/shared/ui/unsavedSources';

/**
 * Змінена чернетка діалогу конструктора — джерело незбережених змін для
 * `UnsavedGuard` (`UI-36`, приймання 4: «незбережене → UnsavedGuard»).
 *
 * ⛔ Мутаційний доказ (локально): без `registerUnsavedSource` у `LocalDraft` —
 * червона перша перевірка; порівняння з `initial` поточного рендеру замість
 * монтувального — червона друга (новий об'єкт `initial` на кожен рендер
 * сторінки робив би діалог «зміненим» без жодного натискання).
 */

function Form({ initial }: { readonly initial: { readonly name: string } }): JSX.Element {
  return (
    <LocalDraft initial={initial}>
      {(draft, setDraft) => (
        <input aria-label="name" value={draft.name} onChange={(event) => setDraft({ name: event.currentTarget.value })} />
      )}
    </LocalDraft>
  );
}

afterEach(() => cleanup());

describe('LocalDraft: незбережене введення бачить UnsavedGuard', () => {
  it('після правки — є незбережене; після закриття діалогу (розмонтування) — немає', () => {
    const view = render(<Form initial={{ name: '' }} />);
    expect(hasUnsavedChanges()).toBe(false);

    fireEvent.change(screen.getByLabelText('name'), { target: { value: 'NEW' } });
    expect(hasUnsavedChanges()).toBe(true);

    view.unmount();
    expect(hasUnsavedChanges()).toBe(false);
  });

  it('новий об’єкт `initial` на рендері сторінки — ще не правка', () => {
    const view = render(<Form initial={{ name: '' }} />);
    view.rerender(<Form initial={{ name: '' }} />);

    expect(hasUnsavedChanges()).toBe(false);
  });
});
