import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
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

describe('B149: правила доступу до періоду — жодного запису з закритого вікна', () => {
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
