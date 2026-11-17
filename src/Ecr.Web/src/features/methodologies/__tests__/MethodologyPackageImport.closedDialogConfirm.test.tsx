import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { MethodologyPackageImport } from '../MethodologyPackageImport';

// ⚠ Спінер (`usePendingLoading`) вмикається за 100 мс РЕАЛЬНОГО часу й вимикає кнопку: під навантаженням CPU тест
// доходив би до другого кліку вже після нього й був би зеленим і без захисту в обробнику. Подвійний клік людини
// швидший за 100 мс, тож тут спінера немає — доказ лише про обробник.
vi.mock('@/features/common/usePendingLoading', () => ({ usePendingLoading: () => false }));

/**
 * B149: «Import methodology package» — менеджер, що лишається відкритим після запису.
 *
 * Зонд (база 826f654e4; `ResizableModal` лишається в DOM закритим завдяки `keepMountedModals` з bab1f8ed7):
 * - подвійний клік «Import» — ЗАКРИТО: обробник звіряє `apply.isPending`; після успіху звіт уже не `dryRun`, кнопка
 *   недоступна — повтору немає;
 * - закрите вікно — ВІДКРИТО: вікно змонтоване завжди (стан `opened` у компоненті), після «Check» і закриття звіт
 *   «created» на місці, «Import» активна — клік по ній у закритому вікні слав
 *   `POST /api/v1/methodologies/import?dryRun=false` (імпорт методик) уже після закриття.
 *
 * Мутаційний доказ: прибрати `!opened ||` з обробника «Import» у `MethodologyPackageImport.tsx` → червоний другий тест
 * (`expected [ …(2) ] to deeply equal [ 'POST /api/v1/methodologies/import' ]`); без `|| apply.isPending` — червоний
 * перший (`expected [ …(3) ] to deeply equal [ …(2) ]`).
 *
 * F13: прибрати `!opened ||` з обробника «Check» → червоний «Check закритого вікна»
 * (`expected [ 'POST /api/v1/methodologies/import' ] to deeply equal []`).
 */

const Report = {
  dryRun: true,
  applied: false,
  outcome: 'created',
  methodologies: [],
  blockers: [],
  conflicts: [],
  warnings: [],
  totals: { methodologiesToCreate: 1, versionsToCreate: 1, versionsUnchanged: 0, formulas: 1, constants: 0, imports: 0 },
};
const Import = 'POST /api/v1/methodologies/import';

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

/** Відкрити й вибрати пакет: «Check» стає доступною. */
async function chosen(): Promise<HTMLElement> {
  fireEvent.click(screen.getByRole('button', { name: /methodologies\.importPackage/ }));
  const input = await waitFor(() => {
    const found = document.querySelector('input[type="file"]');
    if (found === null) throw new Error('немає поля файлу');
    return found;
  });
  const file = new File([JSON.stringify({ format: 'ecr-methodology-package', version: 1 })], 'package.json', { type: 'application/json' });
  fireEvent.change(input, { target: { files: [file] } });
  const check = await screen.findByRole('button', { name: /methodologies\.importCheck/ });
  await waitFor(() => {
    expect(check.hasAttribute('disabled')).toBe(false);
  });

  return check;
}

/** Відкрити, вибрати пакет, перевірити (`dryRun=true`): «Import» стає доступною. */
async function checked(server: ReturnType<typeof holdWrites>): Promise<HTMLElement> {
  fireEvent.click(await chosen());
  await waitFor(() => {
    expect(server.sent).toEqual([Import]);
  });
  server.release();
  // ⚠ `DisabledReason` перемонтовує кнопку, коли причина зникає: шукати її наново.
  await waitFor(() => {
    expect(screen.getByRole('button', { name: /methodologies\.importApply/ }).getAttribute('aria-disabled')).toBeNull();
  });

  return screen.getByRole('button', { name: /methodologies\.importApply/ });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: MethodologyPackageImport — один імпорт, жодного з закритого вікна', () => {
  it('подвійний клік «Import» шле один запис', async () => {
    const server = holdWrites({ read: () => [], write: (_m, _u, index) => ({ body: index === 0 ? Report : { ...Report, dryRun: false, applied: true } }) });
    renderKeepMounted(<MethodologyPackageImport />);
    const apply = await checked(server);

    fireEvent.click(apply);
    await waitFor(() => {
      expect(server.sent).toEqual([Import, Import]);
    });
    await nextTick();
    fireEvent.click(apply);
    await settle();
    expect(server.sent).toEqual([Import, Import]);
  });

  it('«Check» закритого вікна (ще в DOM) нічого не шле', async () => {
    const server = holdWrites({ read: () => [], write: () => ({ body: Report }) });
    renderKeepMounted(<MethodologyPackageImport />);
    const check = await chosen();

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    expect(check.isConnected).toBe(true);
    expect(check.hasAttribute('disabled')).toBe(false);
    fireEvent.click(check);
    await settle();
    expect(server.sent).toEqual([]);
  });

  it('«Import» закритого вікна (ще в DOM) нічого не шле', async () => {
    const server = holdWrites({ read: () => [], write: () => ({ body: Report }) });
    renderKeepMounted(<MethodologyPackageImport />);
    const apply = await checked(server);

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    expect(apply.isConnected).toBe(true);
    fireEvent.click(apply);
    await settle();
    expect(server.sent).toEqual([Import]);
  });
});
