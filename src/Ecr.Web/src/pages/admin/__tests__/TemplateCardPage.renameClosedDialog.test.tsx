import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { TemplateCardPage } from '@/pages/admin/TemplateCardPage';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B145b: діалог перейменування шаблону, що вже закрився, не перейменовує вдруге.
 *
 * ⛔ Діра (зонд на базі 812a049c9, справжня сторінка): після відповіді сервера сторінка скидає чернетку й закриває
 * діалог (`?dialog=` прибрано), власний `Modal` ще ~200 мс у DOM. Значення форми падає на назву картки, тож «Save»
 * активна — другий клік слав другий `PUT /api/v1/templates/7` з поточною назвою.
 *
 * Захист — в обробнику кнопки: вона діє лише поки діалог відкритий (`dialog === 'rename'`) і не під час запиту.
 *
 * Мутаційний доказ: прибрати `dialog !== 'rename' ||` у `TemplateCardPage.tsx` → червоний
 * (`expected [ …(2) ] to deeply equal [ 'PUT /api/v1/templates/7' ]`).
 */

const Card = {
  code: 'TPL-EMIS',
  createdAt: '2026-01-01T00:00:00Z',
  dependents: { documents: 5, projects: 2, publishedVersions: 1, versions: 3 },
  id: 7,
  isActive: true,
  nameL10n: { values: { en: 'Stationary sources' } },
};

const Me = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Template.Edit'],
  simulatedForUserId: null,
  userId: 1,
  userName: 'tester',
};

function read(url: string): unknown {
  if (url.includes('/api/v1/me')) return Me;
  if (url.includes('/languages')) return [{ code: 'en', nameNative: 'English', isDefault: true }];
  if (url.endsWith('/versions')) return { items: [], nextCursor: null };
  if (/\/api\/v1\/templates\/\d+$/.test(url)) return Card;

  return [];
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: Rename шаблону, що закрився (ще в DOM), не перейменовує вдруге', () => {
  it('один PUT; клік по «Save» закритого діалогу нічого не шле', async () => {
    const server = holdWrites({ read, write: () => ({ body: Card }) });
    renderKeepMounted(
      <Routes>
        <Route path="/admin/templates/:id" element={<TemplateCardPage />} />
      </Routes>,
      '/admin/templates/7?dialog=rename',
    );

    const save = await screen.findByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['PUT /api/v1/templates/7']);
    });

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(save).toHaveProperty('disabled', false);
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM і не вимкнена.
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['PUT /api/v1/templates/7']);
    expect(server.bodies).toEqual([{ nameL10n: { en: 'Stationary sources' } }]);
  });
});
