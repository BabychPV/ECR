import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateMappingModal } from '@/features/mapping/CreateMappingModal';
import { testTheme } from '@/test/render';

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
 * ✎ Тут `Select` і `NumberInput` підмінялися легкими заглушниками — нібито
 * тому, що «справжні поля Mantine у jsdom потребують реального layout».
 * Причина була інша й уже усунена: взаємна рекурсія jsdom ↔ nwsapi на
 * станових псевдокласах (коментар у `src/test/setup.ts`). Тепер форма
 * заповнюється у справжніх компонентах — тих самих, що бачить людина.
 */

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
    <MantineProvider theme={testTheme}>
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

/**
 * Перемикає вид цілі у справжньому `Select`.
 *
 * ⚠ Опції рендеряться в порталі поза модалкою — звідси `screen`.
 */
async function pickKind(option: string): Promise<void> {
  fireEvent.click(screen.getByLabelText(KindLabel));
  fireEvent.click(await screen.findByRole('option', { name: option }));
}

const RegistryFieldOption = '⟦mapping.createKindRegistry⟧';

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
    await pickKind(RegistryFieldOption);

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

    await pickKind(RegistryFieldOption);

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
    await pickKind(RegistryFieldOption);
    fireEvent.change(screen.getByLabelText(TargetIdLabel), { target: { value: '7' } });
    fireEvent.click(screen.getByRole('button', { name: '⟦mapping.createSubmit⟧' }));

    await waitFor(() => {
      expect(sent).toHaveLength(1);
    });

    expect(sent[0]?.targetRegistryFieldDefId).toBe(7);
    expect(sent[0]?.targetColumnDefId).toBeNull();
  });
});
