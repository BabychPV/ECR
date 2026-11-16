import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { UiStringsCsvPanel } from '@/features/localization/UiStringsCsvPanel';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149: «Apply» звіту імпорту рядків інтерфейсу (CSV) застосовує файл РІВНО один раз.
 *
 * ⛔ Діра (зонд на базі 826f654e4, справжня панель): спінер «Apply» вмикається лише через 100 мс
 * (`usePendingLoading`), а обробник перевіряв тільки `file !== null`. Другий клік подвійного кліку, поки
 * запис у дорозі, слав другий `POST /api/v1/ui-strings/import?lang=ru&dryRun=false` з тим самим файлом
 * (вікно 1); те саме — поки панель чекає перечитування каталогу `['ui-strings']` (вікно 2, діалог відкритий).
 * Після закриття тіло діалогу розмонтоване разом зі звітом — кнопка від'єднана (вікно 3 закрите й на базі).
 *
 * Захист: `if (file === null || apply.isPending) return;` — один запис на відкриття; відмова сервера знімає
 * `isPending`, тож повтор після помилки працює.
 *
 * Мутаційний доказ: прибрати `|| apply.isPending` → червоний
 * (`expected [ …(3) ] to deeply equal [ …(2) ]` — перевірка + ДВА застосування).
 */

function Catalog(): null {
  useQuery({ queryKey: ['ui-strings', 'editor'], queryFn: () => apiFetch<unknown>('/api/v1/ui-strings/editor') });
  return null;
}

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return {
      userId: 1,
      userName: 'admin',
      language: 'en',
      permissions: ['System.ManageLocalization'],
      isSimulation: false,
      denies: [],
      grants: {},
      mustChangePassword: false,
      simulatedForUserId: null,
    };
  }
  if (url.endsWith('/api/v1/languages')) {
    return [
      { code: 'en', nameNative: 'English', isDefault: true, isActive: true },
      { code: 'ru', nameNative: 'Русский', isDefault: false, isActive: true },
    ];
  }

  return {};
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: UiStringsCsvPanel «Apply» — один запис на перевірений файл', () => {
  it('подвійний клік, очікування каталогу і закритий діалог — рівно один POST dryRun=false', async () => {
    const report = { added: 1, updated: 0, unchanged: 0, errors: [], revision: 5 };
    const server = holdWrites({ read, write: () => ({ body: report }) });
    const gate = gateReads();
    renderKeepMounted(
      <>
        <Catalog />
        <UiStringsCsvPanel />
      </>,
    );

    const picker = await screen.findByTestId('ui-strings-csv-file');
    fireEvent.change(picker, { target: { files: [new File(['key,value\na,b'], 'ru.csv', { type: 'text/csv' })] } });
    await waitFor(() => {
      expect(server.sent).toHaveLength(1);
    });
    server.release();
    const apply = await screen.findByRole('button', { name: '⟦import.apply⟧' });

    // Перевірка (dryRun=true) + рівно одне застосування.
    const importUrl = 'POST /api/v1/ui-strings/import';
    await probeThreeWindows({
      server,
      gate,
      button: () => apply,
      isOpen: () => screen.queryByRole('dialog') !== null,
      expected: [importUrl, importUrl],
      awaitedRead: '/api/v1/ui-strings/editor',
      afterClose: 'detached',
    });
  });
});
