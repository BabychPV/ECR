import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { apiFetch } from '@/api/client';
import { CreateProjectModal } from '@/features/projects/CreateProjectModal';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149, група «скидання після успіху → кнопка вимкнена»: «New project», що закрився (ще в DOM) або ще чекає
 * власника (`await onCreated` — у `ProjectsPage` це перечитування переліку проєктів), не створює вдруге.
 *
 * Зонд: ЗАКРИТО. Вікно 2 — `onSuccess` ще не скінчився (`await onCreated`), `isPending` тримає кнопку
 * `loading` (вимкнена); вікно 3 — код, назву, пояс, версію й політику скинуто → `disabled`. Один
 * `POST /api/v1/projects`.
 *
 * F13 (база 0c1ec8ad9): «заповнено, закрито БЕЗ запису» було ВІДКРИТО — `ProjectsPage` тримає діалог змонтованим,
 * закриття форму не скидає, «Save» закритого `Modal` слала `POST /api/v1/projects`. Захист в обробнику
 * (`if (!opened || create.isPending) return`). Мутації: без `!opened ||` — червоний другий тест; рецензентова
 * `disabled={incomplete && false}` (разом зі зняттям захисту, тобто як на базі) — червоний перший (вікно 3 тепер
 * після завершення мутації: `expected [ 'POST /api/v1/projects', …(1) ] to deeply equal [ 'POST /api/v1/projects' ]`).
 *
 * ⚠ Перелік поясів підмінено одним значенням: справжні ~600 опцій `Select` роблять рендер у jsdom хвилинним
 * (див. `create-project-modal.render.test.tsx`), а логіка діалогу від їх кількості не залежить.
 */

vi.mock('@/features/projects/timeZones', () => ({ timeZones: () => ['Europe/Kyiv'] }));

function Host(): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <CreateProjectModal
      opened={opened}
      onClose={() => setOpened(false)}
      // Як у `ProjectsPage`: власник чекає перечитування переліку (тут — одне читання за воротами).
      onCreated={async () => {
        await apiFetch<unknown>('/api/v1/projects');
      }}
    />
  );
}

function read(url: string): unknown {
  if (url.endsWith('/api/v1/templates')) return { items: [{ id: 3, code: 'AIR', nameL10n: { values: {} } }], nextCursor: null, totalCount: 1 };
  if (url.endsWith('/api/v1/templates/3/versions')) {
    return {
      items: [{ id: 10, version: '1.0', status: 'Published', presentationRevision: 0, publishedAt: '2026-09-01T00:00:00Z' }],
      nextCursor: null,
      totalCount: 1,
    };
  }
  if (url.endsWith('/api/v1/projects/period-policies')) return [{ id: 7, code: 'STD', graceOffsetDays: 15, hardCloseOffsetDays: 45 }];
  if (url.endsWith('/api/v1/languages')) return [{ code: 'en', nameNative: 'English', isDefault: true, isActive: true }];

  return {};
}

async function choose(label: RegExp, option: string): Promise<void> {
  fireEvent.click(screen.getByRole('textbox', { name: label }));
  fireEvent.click(await screen.findByRole('option', { name: option, hidden: true }));
}

/** Заповнити всі обов'язкові поля: «Save» стає доступною. */
async function fill(): Promise<HTMLElement> {
  fireEvent.change(screen.getByLabelText(/periods\.code/), { target: { value: 'AIR-2027' } });
  fireEvent.change(await screen.findByLabelText(/periods\.name/), { target: { value: 'Air 2027' } });
  await choose(/periods\.timeZone/, 'Europe/Kyiv');
  await choose(/periods\.policy/, 'STD · +15/45');

  const save = screen.getByRole('button', { name: '⟦common.save⟧' });
  await waitFor(() => {
    expect(save).toHaveProperty('disabled', false);
  });

  return save;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: CreateProjectModal — один POST /projects на відкриття', () => {
  it('подвійний клік, очікування власника і закритий діалог — рівно один POST', async () => {
    const server = holdWrites({ read, write: () => ({ body: { projectId: 41 } }) });
    const gate = gateReads();
    renderKeepMounted(<Host />);
    const save = await fill();

    await probeThreeWindows({
      server,
      gate,
      button: () => save,
      isOpen: () => screen.queryByRole('dialog') !== null,
      expected: ['POST /api/v1/projects'],
      awaitedRead: '/api/v1/projects',
      // `onClose()` стоїть ДО `await onCreated`: поки власник чекає, діалог уже закритий, а запит — ні.
      openWhileAwaiting: false,
    });
    expect(server.bodies[0]).toMatchObject({ code: 'AIR-2027', timeZoneId: 'Europe/Kyiv', templateVersionId: 10, periodPolicyId: 7 });
  });

  it('форму заповнено, вікно закрито без запису — «Save» закритого вікна нічого не шле', async () => {
    const server = holdWrites({ read, write: () => ({ body: { projectId: 41 } }) });
    renderKeepMounted(<Host />);
    const save = await fill();

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([]);
  });
});
