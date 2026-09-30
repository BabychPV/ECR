import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { CreateRegistryModal, createRegistryMissingFields } from '@/features/registries/CreateRegistryModal';
import { testTheme } from '@/test/render';

/**
 * U-18 (UX-PASS 2026-09-23): «New registry» і «New project» — два діалоги
 * створення з різними контрактами.
 *
 * ⛔ Що доводиться — рівно три пункти знахідки, кожен окремим твердженням:
 *  1. підтвердження каже, що станеться («Save»), а не повторює заголовок
 *     («New registry»), і поруч є «Cancel», що закриває діалог;
 *  2. обов'язкові поля позначені (зірочка + `required`);
 *  3. бракуюче НАЗВАНЕ: рядок «Still needed: …» із підписами полів, і він
 *     зникає разом із тим, як кнопка стає доступною.
 *
 * ⚠ Каталог підвантажується справжнім `loadCatalog` із заглушеної відповіді —
 * тож перевіряються людські підписи, а не позначені ключі.
 *
 * Мутаційно перевірено: повернути підпис кнопки `t('registries.newRegistry')`
 * → червоний (пункт 1); прибрати кнопку «Cancel» → червоний (пункт 1); зняти
 * `required` з «Name» → червоний (пункт 2); прибрати `<StillNeeded …/>` →
 * червоний (пункт 3).
 */
const Strings: Record<string, string> = {
  'registries.newRegistryTitle': 'New registry',
  'registries.newRegistry': 'New registry',
  'registries.code': 'Code',
  'registries.registryCodeHint': 'Latin letters, digits and underscore.',
  'registries.name': 'Name',
  'registries.temporalField': 'Temporal',
  'registries.temporalFieldHint': 'Records carry a validity window.',
  'common.save': 'Save',
  'common.cancel': 'Cancel',
  'common.stillNeeded': 'Still needed: {fields}',
};

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/ui-strings/')
        ? { languageCode: 'en', revision: 1, strings: Strings }
        : null;

      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

async function show(onClose: () => void = () => {}): Promise<HTMLElement> {
  respond();
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CreateRegistryModal opened onClose={onClose} onCreated={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return screen.findByRole('dialog');
}

/** Поле за підписом — з перевіркою, що підпис несе зірочку. */
function requiredField(dialog: HTMLElement, label: string): HTMLInputElement {
  const input = within(dialog).getByLabelText(new RegExp(`^${label}`)) as HTMLInputElement;
  const labelElement = dialog.querySelector(`label[for="${input.id}"]`);

  expect(labelElement?.querySelector('.mantine-InputWrapper-required'), `${label}: зірочка`).not.toBeNull();
  expect(input.required, `${label}: required`).toBe(true);

  return input;
}

describe('CreateRegistryModal: той самий контракт, що й «New project» (U-18)', () => {
  it('підтвердження — «Save», а «Cancel» закриває діалог', async () => {
    const onClose = vi.fn();
    const dialog = await show(onClose);

    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDefined();
    expect(within(dialog).queryByRole('button', { name: 'New registry' })).toBeNull();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('обов\'язкові поля позначені, бракуюче названо, і перелік іде в ногу з кнопкою', async () => {
    const dialog = await show();
    const save = within(dialog).getByRole('button', { name: 'Save' });

    const code = requiredField(dialog, 'Code');
    const name = requiredField(dialog, 'Name');

    expect(within(dialog).getByText('Still needed: Code, Name')).toBeDefined();
    expect(save.hasAttribute('disabled')).toBe(true);

    fireEvent.change(code, { target: { value: 'FUEL' } });
    expect(within(dialog).getByText('Still needed: Name')).toBeDefined();
    expect(save.hasAttribute('disabled')).toBe(true);

    fireEvent.change(name, { target: { value: 'Fuel types' } });
    expect(dialog.querySelector('[data-still-needed]')).toBeNull();
    expect(save.hasAttribute('disabled')).toBe(false);
  });
});

describe('createRegistryMissingFields', () => {
  it('пробіли не рахуються заповненням; порядок — як полів на екрані', () => {
    expect(createRegistryMissingFields({ code: ' ', name: '\t' })).toEqual(['code', 'name']);
    expect(createRegistryMissingFields({ code: 'FUEL', name: '' })).toEqual(['name']);
    expect(createRegistryMissingFields({ code: 'FUEL', name: 'Fuel' })).toEqual([]);
  });
});
