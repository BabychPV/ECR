import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SheetDto } from '@/api/types';
import { testTheme } from '@/test/render';
import { SheetEditor } from '../SheetEditor';
import { draftOf, emptyDraft, type SheetDraft } from '../sheet';
import { saveSheet } from '../sheetApi';

/**
 * Прапорець «Can be copied in the document» у редакторі аркуша шаблону (`SheetDto.isCopyable`).
 * TODO(contract): `isCopyable` ще не в згенерованих типах — планове поле (`sheetCopyTypes.ts`).
 */
function show(draft: SheetDraft, disabled = false) {
  const onChange = vi.fn<(next: SheetDraft) => void>();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      new Response(JSON.stringify([{ code: 'en', nameNative: 'English', isDefault: true }]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <SheetEditor
          draft={draft}
          disabled={disabled}
          saving={false}
          onChange={onChange}
          onSubmit={vi.fn()}
          onCancel={vi.fn()}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return onChange;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetEditor: «можна копіювати в документі»', () => {
  it('прапорець відображає чернетку й вмикає isCopyable', () => {
    const onChange = show({ ...emptyDraft(1), code: 'GEN', nameL10n: { en: 'Sheet' } });
    const box = screen.getByRole('checkbox', { name: /sheets\.copyable/ });

    expect((box as HTMLInputElement).checked).toBe(false);
    fireEvent.click(box);

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ isCopyable: true }));
  });

  it('наявний аркуш з isCopyable=true — прапорець відмічений', () => {
    const sheet = { code: 'GEN', id: 1, isCopyable: true, isMandatory: false, isVisible: true, nameL10n: { values: { en: 'Sheet' } }, ordinal: 1, sheetGroup: null, tables: [] } as unknown as SheetDto;
    show(draftOf(sheet));

    expect((screen.getByRole('checkbox', { name: /sheets\.copyable/ }) as HTMLInputElement).checked).toBe(true);
  });

  it('версія не редагується — прапорець вимкнений', () => {
    show({ ...emptyDraft(1), code: 'GEN' }, true);

    expect((screen.getByRole('checkbox', { name: /sheets\.copyable/ }) as HTMLInputElement).disabled).toBe(true);
  });

  it('збереження надсилає isCopyable у тілі PUT', async () => {
    const calls: { url: string; body: string }[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        calls.push({ url: String(url), body: String(init?.body) });

        return new Response(JSON.stringify({ code: 'GEN' }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }),
    );

    await saveSheet(5, { ...emptyDraft(1), code: 'GEN', nameL10n: { en: 'Sheet' }, isCopyable: true });

    expect(calls).toHaveLength(1);
    expect(calls[0]?.url).toBe('/api/v1/template-versions/5/sheets/GEN');
    expect(JSON.parse(calls[0]?.body ?? '{}')).toMatchObject({ isCopyable: true });
  });
});
