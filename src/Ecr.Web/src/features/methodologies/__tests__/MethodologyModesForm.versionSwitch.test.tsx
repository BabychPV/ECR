import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MethodologyDraftVersionDto } from '@/api/types';
import { MethodologyModesForm } from '@/features/methodologies/MethodologyContentPanels';

/**
 * Аудит 2026-09-16, §10.5 (High, тиха псування даних): форма режимів
 * показувала й могла зберегти режими НЕ ТІЄЇ версії.
 *
 * ⛔ Локальний стан (`numericMode`/`calendarMode`/`traceLevel`) наповнювався
 * лише один раз — початковим значенням `useState` при монтуванні. Форма
 * рендериться з пропу `version = selected` того самого списку
 * (`MethodologyVersionsPage.tsx`), і перемикання версії в списку МІНЯЄ ПРОП,
 * не перемонтовуючи компонент: у `Select`-ах лишалися значення попередньої
 * версії, візуально не відрізнити від справжніх.
 *
 * **Сценарій:** адмін відкриває версію A (Legacy/Actual/Off), клацає версію B
 * (Strict/Fixed365/Full) — Select-и досі показують A. «Зберегти режими»
 * надсилає `saveMethodologyModes(methodologyId, B.id, { …значення A })`, тихо
 * перезаписуючи режими B значеннями A. Ціна названа у власних коментарях
 * коду: обидва перші режими «тихо змінюють УСІ числа версії, не змінивши
 * жодної формули» (`ФВ-9.9`, `ФВ-16.11`).
 *
 * ⚠ `Select` Mantine підмінено легким `<select>` — той самий прийом і та сама
 * причина, що в `CreateMappingModal.targetKindReset` (`D1-12`). Форвардяться
 * рівно `label`/`value`/`onChange`/`data`, тобто перевіряється справжній стан
 * компонента, а не імітація.
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
        {(props.data ?? []).map((item) => (
          <option key={item.value} value={item.value}>
            {item.label}
          </option>
        ))}
      </select>
    );
  }

  return { ...actual, Select: StubSelect };
});

function version(
  id: number,
  numericMode: 'Legacy' | 'Strict',
  calendarMode: 'Actual' | 'Fixed365' | 'Fixed360',
  traceLevel: 'Off' | 'ErrorsOnly' | 'Full',
): MethodologyDraftVersionDto {
  return {
    calendarMode,
    createdByUserId: 1,
    effectiveFrom: null,
    id,
    isEditable: true,
    level: 'Configuration',
    numericMode,
    status: 'Draft',
    traceLevel,
    versionNumber: `v${id}`,
  };
}

const VersionA = version(11, 'Legacy', 'Actual', 'Off');
const VersionB = version(22, 'Strict', 'Fixed365', 'Full');

/** Адреси й тіла надісланих `PUT …/modes`. */
const saved: { url: string; body: Record<string, unknown> }[] = [];

function mockServer(): void {
  saved.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'PUT' && url.includes('/modes')) {
        saved.push({ url, body: JSON.parse(String(init.body)) as Record<string, unknown> });
      }

      return new Response(JSON.stringify(VersionB), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

const NumericLabel = '⟦methodologies.numericMode⟧';
const CalendarLabel = '⟦methodologies.calendarMode⟧';
const TraceLabel = '⟦methodologies.traceLevel⟧';

function show(): { switchToB: () => void } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  function Harness({ selected }: { selected: MethodologyDraftVersionDto }): JSX.Element {
    return (
      <MantineProvider>
        <QueryClientProvider client={client}>
          <MethodologyModesForm methodologyId={7} version={selected} editable />
        </QueryClientProvider>
      </MantineProvider>
    );
  }

  const view = render(<Harness selected={VersionA} />);

  // ⛔ Саме `rerender`, а не повторний `render`: перемикання версії в списку
  // МІНЯЄ ПРОП того самого змонтованого компонента. Новий `render` створив би
  // новий екземпляр і дефект не відтворився б узагалі.
  return { switchToB: () => view.rerender(<Harness selected={VersionB} />) };
}

function valueOf(label: string): string {
  return (screen.getByLabelText(label) as HTMLSelectElement).value;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyModesForm: перемикання версії перемальовує режими (§10.5)', () => {
  it('після переходу з версії A на B у полях стоять режими B', () => {
    mockServer();
    const { switchToB } = show();

    expect(valueOf(NumericLabel)).toBe('Legacy');
    expect(valueOf(CalendarLabel)).toBe('Actual');
    expect(valueOf(TraceLabel)).toBe('Off');

    switchToB();

    // ⛔ Мутаційний доказ (RED до фіксу): у полях лишалися Legacy/Actual/Off —
    // режими ЧУЖОЇ версії, візуально не відрізнити від справжніх.
    expect(valueOf(NumericLabel)).toBe('Strict');
    expect(valueOf(CalendarLabel)).toBe('Fixed365');
    expect(valueOf(TraceLabel)).toBe('Full');
  });

  it('«Зберегти режими» після переходу надсилає режими B, а не A', async () => {
    mockServer();
    const { switchToB } = show();

    switchToB();
    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.saveModes⟧' }));

    await waitFor(() => expect(saved).toHaveLength(1));

    // Адреса завжди була правильною — і саме тому дефект був тихим: запит ішов
    // у версію B зі значеннями A.
    expect(saved[0]?.url).toContain('/versions/22/modes');
    expect(saved[0]?.body).toEqual({
      numericMode: 'Strict',
      calendarMode: 'Fixed365',
      traceLevel: 'Full',
    });
  });

  it('правка, зроблена ПІСЛЯ переходу, не втрачається через синхронізацію', async () => {
    mockServer();
    const { switchToB } = show();

    switchToB();
    fireEvent.change(screen.getByLabelText(TraceLabel), { target: { value: 'ErrorsOnly' } });

    // ⚠ Зворотний бік фіксу: синхронізація мусить спрацювати РІВНО на зміну
    // версії, а не на кожен рендер — інакше вона затирала б власний вибір
    // адміна за мить після кліку.
    expect(valueOf(TraceLabel)).toBe('ErrorsOnly');

    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.saveModes⟧' }));
    await waitFor(() => expect(saved).toHaveLength(1));

    expect(saved[0]?.body).toEqual({
      numericMode: 'Strict',
      calendarMode: 'Fixed365',
      traceLevel: 'ErrorsOnly',
    });
  });
});
