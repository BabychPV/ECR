import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateColumnDto } from '@/api/types';
import { PresentationEditor } from '@/features/templates/PresentationEditor';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { holdReads } from '@/test/heldReads';

/**
 * B149 (група 3): правка презентації колонки опублікованої версії не шле `PATCH` удруге.
 *
 * ⚠ Тіло тут НЕ знімається: поля й кнопки — прямі діти `Modal`, тож закритий діалог тримає «Save» у DOM
 * (`isConnected === true`). Від другого запиту береже інше:
 * - вікно «ще відкритий»: `onSuccess` чекає перечитування структури ВСЕРЕДИНІ мутації — `isPending` тримається,
 *   кнопка `loading` → `disabled`;
 * - після закриття `column = null`, тож перелік змін `changes()` порожній — кнопка `disabled`, і React клік по
 *   вимкненій кнопці до обробника не пускає. (Без цього вона послала б `PATCH` із `[]` — та сама адреса.)
 * Вердикт: ЗАКРИТО; цей тест — регресія.
 */

const Column = {
  id: 31,
  code: 'Qty',
  headerL10n: { values: { en: 'Quantity' } },
  displayFormat: null,
  ordinal: 0,
  isHidden: false,
  widthPx: null,
  headerGroupId: null,
} as unknown as TemplateColumnDto;

const Languages = [{ code: 'en', isDefault: true, isActive: true, nameNative: 'English' }];

function Host(): JSX.Element {
  const [column, setColumn] = useState<TemplateColumnDto | null>(Column);
  // Структура версії, як на `TemplateVersionPage`: її й перечитує `onSuccess`.
  useQuery({
    queryKey: queryKeys.templates.version(1),
    queryFn: () => apiFetch<unknown>('/api/v1/template-versions/1/structure'),
  });

  return <PresentationEditor templateVersionId={1} column={column} onClose={() => setColumn(null)} />;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: презентація колонки — один PATCH на відкриття', () => {
  it('ні під час перечитування (діалог відкритий), ні після закриття другого PATCH немає', async () => {
    const server = holdWrites({
      read: (url) => (url.endsWith('/api/v1/languages') ? Languages : {}),
      write: () => ({ body: { presentationRevision: 2 } }),
    });
    const reads = holdReads((url) => url === '/api/v1/template-versions/1/structure');
    renderKeepMounted(<Host />);

    fireEvent.change(screen.getByLabelText('⟦version.displayFormat⟧'), { target: { value: 'N2' } });
    const save = screen.getByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['PATCH /api/v1/template-versions/1/presentation']);
    });

    // Сервер прийняв; мутація чекає перечитування структури — діалог ще відкритий, кнопка зайнята.
    reads.arm();
    server.release();
    await waitFor(() => {
      expect(reads.waiting()).toBe(1);
    });
    expect(screen.getByRole('dialog')).not.toBeNull();
    expect(save.isConnected).toBe(true);
    expect(save).toHaveProperty('disabled', true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['PATCH /api/v1/template-versions/1/presentation']);

    // Перечитано, діалог закрито: кнопка ЛИШАЄТЬСЯ в DOM, але вимкнена (змін немає — колонку знято).
    reads.open();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(save.isConnected).toBe(true);
    expect(save).toHaveProperty('disabled', true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['PATCH /api/v1/template-versions/1/presentation']);
  });
});
