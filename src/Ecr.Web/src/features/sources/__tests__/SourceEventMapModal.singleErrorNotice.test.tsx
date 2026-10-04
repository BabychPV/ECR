import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { createQueryClient } from '@/app/queryClient';
import { testTheme } from '@/test/render';
import { SourceEventMapModal } from '../SourceEventMapModal';
import type { SourceEventMap } from '../sourceEventsApi';

/**
 * L9-01: відмова збереження мапінгу подій показується ОДИН раз — у `ErrorAlert`
 * форми. Без `meta.handled` глобальна сітка (`createQueryClient`) вважала зміну
 * «німою» і кидала ще й тост: два сповіщення на одну відмову.
 */

const Stored: SourceEventMap = {
  id: 12,
  sourceEntityId: 42,
  documentId: 100,
  tableDefId: 200,
  volumeMode: 'None',
  filterAttribute: null,
  filterScope: null,
  filterValue: null,
  isActive: true,
  fields: [
    { id: 1, targetColumnDefId: 1, sourceAttribute: '$start', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: [] },
    { id: 2, targetColumnDefId: 2, sourceAttribute: '$end', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: [] },
  ],
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input), 'http://localhost').pathname;
      const method = init?.method ?? 'GET';

      if (path === '/api/v1/data-sources/7/event-templates') return json([{ templateName: 'FLARE_EVENTS', attributes: [] }]);
      if (path === '/api/v1/documents/100/tables') {
        return json([{ tableDefId: 200, tableInstanceId: 300, allowsDynamicRows: true, tableCode: 'FLARE_RECORD', sheetCode: 'S1' }]);
      }
      if (path === '/api/v1/documents/100/tables/300') {
        return json({
          columns: [
            { id: 1, code: 'Start', header: 'Start', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
            { id: 2, code: 'End', header: 'End', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
          ],
        });
      }
      if (path === '/api/v1/source-event-maps/12' && method === 'PUT') {
        return json(
          {
            type: 'about:blank',
            title: 'Conflict',
            status: 409,
            errorCode: 'ECR-SRC-0409',
            detail: 'Mapping has links.',
            correlationId: 'corr-l9-01',
          },
          409,
        );
      }

      return json([]);
    }),
  );
}

afterEach(() => {
  cleanup();
  notifications.clean();
  vi.unstubAllGlobals();
});

describe('SourceEventMapModal: одна відмова — одне сповіщення', () => {
  it('відмова PUT показана в ErrorAlert форми і не дублюється тостом', async () => {
    respond();

    render(
      <MantineProvider theme={testTheme}>
        <Notifications />
        <QueryClientProvider client={createQueryClient()}>
          <MemoryRouter>
            <SourceEventMapModal
              dataSourceId={7}
              sourceEntityId={42}
              template="FLARE_EVENTS"
              map={Stored}
              documents={[{ id: 100, businessKey: 'DOC-000123' } as never]}
              onClose={vi.fn()}
            />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    const form = await screen.findByRole('dialog');
    const save = within(form).getByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => expect(save.hasAttribute('disabled')).toBe(false));
    fireEvent.click(save);

    await waitFor(() => expect(within(form).getAllByRole('alert').length).toBeGreaterThan(0));
    // Дати сітці шанс спрацювати: тост з'являється в тому самому циклі, що й помилка.
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(document.querySelectorAll('.mantine-Notification-root')).toHaveLength(0);
  });
});
