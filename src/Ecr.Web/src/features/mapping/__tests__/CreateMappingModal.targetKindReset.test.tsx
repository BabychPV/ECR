import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateMappingModal } from '@/features/mapping/CreateMappingModal';

/**
 * Аудит 2026-09-16, §10.4: `targetId` не очищався при зміні `targetKind`.
 *
 * ⛔ Сценарій дефекту: користувач обирає Kind=Column, вводить ID `42`,
 * перемикається на RegistryField, НЕ чистивши поле ID. `canSubmit` перевіряє
 * лише `targetId !== ''`, тож кнопка лишається активною, і запит іде з
 * `targetRegistryFieldDefId: 42` — числом із ЧУЖОГО простору імен. Проти
 * реєстрових полів воно ніколи не перевірялось, і може випадково збігтися з
 * непов'язаною сутністю: мапінг тихо вказує на неправильну ціль.
 *
 * ⚠ `Select` і `NumberInput` Mantine підмінені легкими заглушниками — той
 * самий прийом і та сама причина, що в `UserAccessEditor.rolesDropdown`
 * (`D1-12`: справжні поля Mantine у jsdom потребують реального layout).
 * Заглушники форвардять РІВНО ті пропси, від яких залежить фікс: `value` і
 * `onChange`, — тобто перевіряється справжній обробник `onChange` компонента,
 * не його імітація.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  function StubSelect(props: {
    label?: string;
    value?: string | null;
    onChange?: (value: string | null) => void;
    data?: readonly { value: string; label: string }[];
  }): JSX.Element {
    return (
      <select
        aria-label={props.label ?? ''}
        value={props.value ?? ''}
        onChange={(event) => props.onChange?.(event.currentTarget.value || null)}
      >
        <option value="">—</option>
        {(props.data ?? []).map((item) => (
          <option key={item.value} value={item.value}>
            {item.label}
          </option>
        ))}
      </select>
    );
  }

  function StubNumberInput(props: {
    label?: string;
    value?: number | '';
    onChange?: (value: number | string) => void;
  }): JSX.Element {
    return (
      <input
        aria-label={props.label ?? ''}
        value={props.value ?? ''}
        onChange={(event) => {
          const raw = event.currentTarget.value;
          props.onChange?.(raw === '' ? '' : Number(raw));
        }}
      />
    );
  }

  return { ...actual, Select: StubSelect, NumberInput: StubNumberInput };
});

/** Тіла запитів `POST /entity-field-maps`, у порядку надсилання. */
const sent: Record<string, unknown>[] = [];

function mockServer(): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_url: string, init?: RequestInit) => {
      sent.push(JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>);

      return new Response(JSON.stringify({ id: 1 }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <CreateMappingModal
          sourceEntityId={5}
          opened
          onClose={() => {}}
          onCreated={() => {}}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Поля форми за їхніми ключами підписів (каталог у тестах не завантажений). */
const FieldLabel = '⟦mapping.createField⟧';
const KindLabel = '⟦mapping.createKind⟧';
const TargetIdLabel = '⟦mapping.createTargetId⟧';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CreateMappingModal: зміна виду цілі скидає ID (§10.4)', () => {
  it('ID, введений для Column, НЕ переїжджає в RegistryField', async () => {
    mockServer();
    show();

    fireEvent.change(screen.getByLabelText(FieldLabel), { target: { value: 'Flare_01_CO' } });
    fireEvent.change(screen.getByLabelText(TargetIdLabel), { target: { value: '42' } });

    // Перемикання виду цілі — рівно та дія, після якої дефект спрацьовував.
    fireEvent.change(screen.getByLabelText(KindLabel), { target: { value: 'RegistryField' } });

    // ⛔ Мутаційний доказ (RED до фіксу): поле лишалося з `42`, і саме це
    // число йшло в `targetRegistryFieldDefId`.
    expect((screen.getByLabelText(TargetIdLabel) as HTMLInputElement).value).toBe('');
  });

  it('після перемикання виду кнопка заблокована, доки ID не введено заново', async () => {
    mockServer();
    show();

    fireEvent.change(screen.getByLabelText(FieldLabel), { target: { value: 'Flare_01_CO' } });
    fireEvent.change(screen.getByLabelText(TargetIdLabel), { target: { value: '42' } });

    const submit = screen.getByRole('button', { name: '⟦mapping.createSubmit⟧' });
    expect(submit).not.toHaveProperty('disabled', true);

    fireEvent.change(screen.getByLabelText(KindLabel), { target: { value: 'RegistryField' } });

    // ⛔ `canSubmit` дивиться лише на `targetId !== ''` — до фіксу кнопка
    // лишалася активною, і запит із чужим ID можна було відправити одним
    // кліком.
    expect(submit).toHaveProperty('disabled', true);
    expect(sent).toHaveLength(0);
  });

  it('заново введений ID іде вже в поле реєстрового поля, і лише в нього', async () => {
    mockServer();
    show();

    fireEvent.change(screen.getByLabelText(FieldLabel), { target: { value: 'Flare_01_CO' } });
    fireEvent.change(screen.getByLabelText(TargetIdLabel), { target: { value: '42' } });
    fireEvent.change(screen.getByLabelText(KindLabel), { target: { value: 'RegistryField' } });
    fireEvent.change(screen.getByLabelText(TargetIdLabel), { target: { value: '7' } });
    fireEvent.click(screen.getByRole('button', { name: '⟦mapping.createSubmit⟧' }));

    await waitFor(() => {
      expect(sent).toHaveLength(1);
    });

    expect(sent[0]?.targetRegistryFieldDefId).toBe(7);
    expect(sent[0]?.targetColumnDefId).toBeNull();
  });
});
