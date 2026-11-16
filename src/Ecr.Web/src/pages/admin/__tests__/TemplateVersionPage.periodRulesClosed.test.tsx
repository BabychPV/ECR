import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B149: «Period access rules» на сторінці версії шаблону — жодного запису з закритого вікна.
 *
 * Зонд (база 826f654e4, справжня сторінка, `keepMounted`): вікно правил змонтоване завжди (`opened={periodRulesOpen}`),
 * обидві форми після закриття на місці. Подвійний клік — ЗАКРИТО (`loading={saving}` вимикає кнопки з наступним
 * рендером; після створення форма скидається ключем). Закрите вікно — ВІДКРИТО: введений id правила лишається, «Save»
 * активна — клік у закритому `Modal` слав `PUT /api/v1/template-versions/1/period-access-rules/7` уже після закриття
 * (так само «Add» — `POST …/period-access-rules`, «Delete» іде через `ConfirmModal` із захистом у `shared/ui`).
 *
 * Мутаційний доказ: прибрати `!periodRulesOpen ||` з `onSave` у `TemplateVersionPage.tsx` → червоний
 * (`expected [ Array(1) ] to deeply equal []`).
 *
 * F13: те саме для `onSubmit` нового правила («Add») і `onDelete` — кожен має власний тест, червоний без свого
 * `!periodRulesOpen ||`. «Delete» закритого вікна відкриває ВЛАСНЕ підтвердження (`ConfirmModal` з `opened = true`),
 * тож захист `shared/ui` там не допомагає — лишається лише захист `onDelete`.
 */

function structureDto(): unknown {
  return {
    isEditable: true,
    presentationRevision: 0,
    groupRules: [],
    templateVersionId: 1,
    sheets: [
      {
        id: 1, code: 'SHEET', nameL10n: { values: { en: 'Sheet' } }, isMandatory: true, isVisible: true, ordinal: 1, sheetGroup: null,
        tables: [{ id: 10, code: 'TBL', nameL10n: { values: { en: 'Table' } }, layoutKind: 'Static', maxDynamicRows: null, ordinal: 1, rowMode: 'Fixed', rows: [], columns: [] }],
      },
    ],
  };
}

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return { userId: 0, userName: 'test', language: 'en', permissions: ['Template.View', 'Template.Edit', 'Template.Publish'], isSimulation: false, denies: [], grants: {} };
  }
  if (url.endsWith('/api/v1/languages')) return [{ code: 'en', nameNative: 'English', isDefault: true }];
  if (url.includes('/structure')) return structureDto();
  if (url.includes('/versions')) {
    return { items: [{ id: 1, version: '1.0', status: 'Draft', presentationRevision: 0, clonedFromVersionId: null, publishedAt: null }], nextCursor: null, totalCount: null };
  }

  return [];
}

beforeEach(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    },
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderPage(): void {
  renderKeepMounted(
    <Routes>
      <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
    </Routes>,
    '/admin/templates/1/versions/1',
  );
}

async function openRules(): Promise<void> {
  fireEvent.click(await screen.findByTestId('version-more'));
  fireEvent.click(await screen.findByRole('menuitem', { name: /periodRules\.title/ }));
  await screen.findByRole('dialog');
}

/** Форма нового правила: ціль — аркуш (без цілі «Add» заблокована). Повертає «Add». */
async function fillCreate(): Promise<HTMLElement> {
  const sheet = (await screen.findAllByRole('textbox', { name: /periodRules\.sheet/ }))[0]!;
  fireEvent.click(sheet);
  // ⚠ Не будь-яка `option`: `NativeSelect` форми (вид правила тощо) теж має `<option>` — лише список `Select` аркуша.
  const list = await screen.findByRole('listbox', { hidden: true });
  fireEvent.click(within(list).getAllByRole('option', { hidden: true })[0]!);
  const add = screen.getByRole('button', { name: /periodRules\.add/ });
  await waitFor(() => {
    expect(add).toHaveProperty('disabled', false);
  });

  return add;
}

/** Закрити вікно правил Escape-ом; `Modal` лишається в DOM (`keepMounted`). */
async function closeRules(): Promise<void> {
  fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
  await waitFor(() => {
    expect(screen.queryByRole('dialog')).toBeNull();
  });
}

describe('B149: правила доступу до періоду — жодного запису з закритого вікна', () => {
  it('«Add» нового правила в закритому вікні (ще в DOM) нічого не шле', async () => {
    const server = holdWrites({ read });
    renderPage();
    await openRules();
    const add = await fillCreate();

    await closeRules();
    expect(add.isConnected).toBe(true);
    expect(add).toHaveProperty('disabled', false);
    fireEvent.click(add);
    await settle();
    expect(server.sent).toEqual([]);
  }, 60_000);

  it('«Delete» правила, підтверджене після закриття вікна правил, нічого не шле', async () => {
    const server = holdWrites({ read });
    renderPage();
    await openRules();
    fireEvent.change(await screen.findByRole('textbox', { name: /periodRules\.manageId/ }), { target: { value: '7' } });
    const remove = screen.getByRole('button', { name: /periodRules\.delete/ });
    await waitFor(() => {
      expect(remove).toHaveProperty('disabled', false);
    });

    await closeRules();
    // Кнопка закритого вікна ще в DOM і активна: клік відкриває ВЛАСНЕ підтвердження (його `opened` — істина),
    // тож захист `ConfirmModal` (`if (opened)`) запис не зупиняє — лишається лише `!periodRulesOpen` в `onDelete`.
    expect(remove.isConnected).toBe(true);
    fireEvent.click(remove);
    const confirm = await screen.findByRole('dialog');
    fireEvent.click(within(confirm).getByRole('button', { name: /periodRules\.delete/ }));
    await settle();
    expect(server.sent).toEqual([]);
  }, 60_000);

  it('«Save» правила в закритому вікні (ще в DOM) нічого не шле', async () => {
    const server = holdWrites({ read });
    renderKeepMounted(
      <Routes>
        <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
      </Routes>,
      '/admin/templates/1/versions/1',
    );

    fireEvent.click(await screen.findByTestId('version-more'));
    fireEvent.click(await screen.findByRole('menuitem', { name: /periodRules\.title/ }));
    fireEvent.change(await screen.findByRole('textbox', { name: /periodRules\.manageId/ }), { target: { value: '7' } });
    const save = screen.getByRole('button', { name: /periodRules\.save/ });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    expect(save.isConnected).toBe(true);
    expect(save).toHaveProperty('disabled', false);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([]);
  }, 60_000);
});
