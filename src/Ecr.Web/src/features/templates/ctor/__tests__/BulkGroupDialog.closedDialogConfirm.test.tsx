import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateColumnDto, TemplateHeaderGroupDto } from '@/api/types';
import { BulkGroupDialog } from '@/features/templates/ctor/BulkGroupDialog';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
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
 *
 * F13: захист не блокує законного: «Cancel» → повторне відкриття → рівно один PATCH; відмова сервера (422) →
 * повторний «Apply» шле PATCH знову. Мутація «обробник замикається після помилки» (`|| patch.isError`) → червоний
 * тест повтору.
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
    <>
      <BulkGroupDialog
        templateVersionId={12}
        presentationRevision={3}
        columns={columns}
        headerGroups={groups}
        opened={opened}
        onClose={() => setOpened(false)}
        onApplied={() => undefined}
      />
      <button type="button" onClick={() => setOpened(true)}>
        reopen
      </button>
    </>
  );
}

const Patch = 'PATCH /api/v1/template-versions/12/presentation';

// ⚠ Код відмови — з каталогу (`ECR-REQ-0422`, `err.ECR-REQ-0422.malformedRequest`); не 409 — та гілка інша (`stale`).
const Refused = {
  status: 422,
  body: {
    title: 'Unprocessable',
    status: 422,
    errorCode: 'ECR-REQ-0422',
    correlationId: 'cid-refused',
    messageKey: 'err.ECR-REQ-0422.malformedRequest',
  },
};

/** Вибрати групу «Pollutants» у відкритому діалозі: «Apply» стає доступною. */
async function pickGroup(): Promise<HTMLElement> {
  const dialog = await screen.findByRole('dialog');
  fireEvent.click(within(dialog).getByRole('textbox', { name: /colgroups\.field/ }));
  fireEvent.click(await screen.findByRole('option', { name: 'Pollutants', hidden: true }));
  const apply = within(dialog).getByRole('button', { name: '⟦columns.bulkApply⟧' });
  await waitFor(() => {
    expect(apply).toHaveProperty('disabled', false);
  });

  return apply;
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

  it('закрити («Cancel») і відкрити знову: «Apply» відкритого вікна шле рівно один PATCH', async () => {
    const server = holdWrites({ write: () => ({ body: { presentationRevision: 4 } }) });
    renderKeepMounted(<Host />);
    await pickGroup();

    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /common\.cancel/ }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(server.sent).toEqual([]);

    fireEvent.click(screen.getByRole('button', { name: 'reopen' }));
    const apply = await pickGroup();
    fireEvent.click(apply);
    await waitFor(() => {
      expect(server.sent).toEqual([Patch]);
    });
    fireEvent.click(apply);
    await settle();
    expect(server.sent).toEqual([Patch]);
    server.release();
  });

  it('після відмови сервера повторний «Apply» шле PATCH знову', async () => {
    const server = holdWrites({ write: (_m, _u, index) => (index === 0 ? Refused : { body: { presentationRevision: 4 } }) });
    renderKeepMounted(<Host />);
    const apply = await pickGroup();

    fireEvent.click(apply);
    await waitFor(() => {
      expect(server.sent).toEqual([Patch]);
    });
    server.release();
    // Відмову показано в діалозі, діалог лишився відкритим.
    await waitFor(() => {
      expect(document.querySelector('[data-bulk-error="server"]')).not.toBeNull();
    });
    expect(screen.queryByRole('dialog')).not.toBeNull();

    fireEvent.click(apply);
    await waitFor(() => {
      expect(server.sent).toEqual([Patch, Patch]);
    });
  });
});
