import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateColumnDto, TemplateHeaderGroupDto } from '@/api/types';
import { BulkGroupDialog } from '@/features/templates/ctor/BulkGroupDialog';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149: «Edit selected…» (група заголовка для вибраних колонок) шле РІВНО один `PATCH …/presentation`.
 *
 * ⛔ Діра (зонд на базі 826f654e4, справжній діалог): спінер «Apply» вмикається лише через 100 мс
 * (`usePendingLoading`), а обробник перевіряв тільки `value !== null`. Другий клік, поки `PATCH` у дорозі
 * (вікно 1) або поки діалог ще відкритий і чекає перечитування версії (вікно 2), слав другий
 * `PATCH /api/v1/template-versions/12/presentation` з тим самим тілом і ТІЄЮ САМОЮ
 * `expectedPresentationRevision` — сервер відповідав 409, і людина бачила «вигляд змінили з іншої вкладки»
 * після власного успішного застосування. Вікно 3 (закрито) — у продукті власник (`ColumnsTable`) розмонтовує
 * діалог, а тут кнопка вимкнена: група скинута, значення збігається з поточним.
 *
 * Захист: `if (!opened || value === null || patch.isPending) return;` — відмова сервера знімає `isPending`,
 * повтор після 409 працює.
 *
 * Мутаційний доказ: прибрати `|| patch.isPending` → червоний
 * (`expected [ …(2) ] to deeply equal [ Array(1) ]` — два `PATCH`). `!opened ||` — другий шар (KIT §6.9): у
 * продукті власник розмонтовує закритий діалог, а тут кнопка закритого діалогу й так вимкнена, тож окремого
 * червоного доказу цей шар не має.
 */

const columns = [
  { id: 1, code: 'SO2', dataType: 'Decimal', headerL10n: { values: { en: 'SO2' } }, headerGroupId: null },
  { id: 2, code: 'NOX', dataType: 'Decimal', headerL10n: { values: { en: 'NOX' } }, headerGroupId: null },
] as unknown as TemplateColumnDto[];

const groups = [
  { id: 5, parentId: null, code: 'POL', ordinal: 1, labelL10n: { values: { en: 'Pollutants' } } },
] as unknown as TemplateHeaderGroupDto[];

function Host(): JSX.Element {
  const [opened, setOpened] = useState(true);
  useQuery({ queryKey: queryKeys.templates.version(12), queryFn: () => apiFetch<unknown>('/api/v1/template-versions/12') });

  return (
    <BulkGroupDialog
      templateVersionId={12}
      presentationRevision={3}
      columns={columns}
      headerGroups={groups}
      opened={opened}
      onClose={() => setOpened(false)}
      onApplied={() => undefined}
    />
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: BulkGroupDialog — один PATCH на відкриття', () => {
  it('подвійний клік, очікування версії і закритий діалог — рівно один PATCH', async () => {
    const server = holdWrites({ write: () => ({ body: { presentationRevision: 4 } }) });
    const gate = gateReads();
    renderKeepMounted(<Host />);

    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('textbox', { name: /colgroups\.field/ }));
    fireEvent.click(await screen.findByRole('option', { name: 'Pollutants', hidden: true }));
    const apply = within(dialog).getByRole('button', { name: '⟦columns.bulkApply⟧' });
    await waitFor(() => {
      expect(apply).toHaveProperty('disabled', false);
    });

    await probeThreeWindows({
      server,
      gate,
      button: () => apply,
      isOpen: () => screen.queryByRole('dialog') !== null,
      expected: ['PATCH /api/v1/template-versions/12/presentation'],
      awaitedRead: '/api/v1/template-versions/12',
    });
    expect(server.bodies[0]).toEqual([
      { entityType: 'ColumnDef', entityId: 1, field: 'HeaderGroupId', value: '5' },
      { entityType: 'ColumnDef', entityId: 2, field: 'HeaderGroupId', value: '5' },
    ]);
  });
});
