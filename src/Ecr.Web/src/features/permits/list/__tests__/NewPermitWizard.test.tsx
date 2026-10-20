import { useState, type JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { FixtureTypes, makeType } from '../../types/__tests__/typesFixture';
import { NewPermitWizard } from '../NewPermitWizard';

/**
 * Майстер «New permit» (PM-07): типи (неопубліковані недоступні), крок «Details» з відмовою без номера,
 * підсумок, `POST /api/v1/permits`, перехід на чернетку, відмова сервера лишає майстер відкритим.
 *
 * Мутаційний доказ (вручну, відновлено): без `disabled={!ready}` неопублікований тип обирається (валить «недоступний»);
 * `checkDetails` у `validate` прибрати — порожній номер доходить до підсумку (валить «без номера»); `navigate(...)`
 * після створення прибрати — валить «веде на сторінку дозволу»; `api.fail` в `onError` замінити на `api.close()` —
 * валить «відмова сервера».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => 'en',
}));

const notify = vi.hoisted(() => ({ showDone: vi.fn() }));
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: notify.showDone,
}));

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const types = (items = FixtureTypes): Response => json({ items, nextCursor: null, totalCount: items.length });

let posts: { url: string; body: unknown }[] = [];

function stub(options: { items?: typeof FixtureTypes; create?: () => Response; typesReply?: () => Response } = {}): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.startsWith('/api/v1/permit-types')) return options.typesReply?.() ?? types(options.items);
      posts.push({ url, body: JSON.parse((init?.body as string | undefined) ?? 'null') as unknown });

      return options.create?.() ?? json({ id: 41 }, 201);
    }),
  );
}

function Where(): null {
  (globalThis as { __where?: string }).__where = useLocation().pathname;

  return null;
}

const onClose = vi.fn();

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/permits']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Where />
          <Routes>
            <Route path="*" element={<NewPermitWizard opened onClose={onClose} />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

beforeEach(() => {
  // Лише `Date` (без фальшивих таймерів): `waitFor` і Mantine працюють як завжди.
  vi.setSystemTime(new Date(2026, 9, 19, 12, 0, 0));
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
  posts = [];
  delete (globalThis as { __where?: string }).__where;
});

const where = (): string | undefined => (globalThis as { __where?: string }).__where;

describe('NewPermitWizard: крок «Type»', () => {
  it('опубліковані типи вибираються, неопублікований недоступний і пояснений; нічого не передвибрано', async () => {
    stub();
    show();

    const group = await screen.findByTestId('permit-type-pick');
    const radios = within(group).getAllByRole('radio') as HTMLInputElement[];
    expect(radios.map((r) => [r.value, r.disabled, r.checked])).toEqual([
      ['AirEmission', false, false],
      ['WaterDischarge', false, false],
      ['WasteStorage', true, false],
    ]);
    expect(group.textContent).toContain('permits.list.wizard.notPublished');
    expect(group.textContent).toContain('Emission limits (6) · Sources (4)');
  });

  it('PA-U-08: «Next» без явного вибору типу не пускає далі й нічого не створює', async () => {
    stub();
    show();
    await screen.findByTestId('permit-type-pick');
    fireEvent.click(screen.getByTestId('wizard-next'));

    expect((await screen.findByTestId('wizard-step-error')).textContent).toContain('permits.list.wizard.typeRequired');
    expect(screen.queryByRole('textbox', { name: /permits\.list\.wizard\.number/ })).toBeNull();
    expect(posts).toEqual([]);
  });

  it('жодного опублікованого типу: пояснення, а «Next» не пускає далі', async () => {
    stub({ items: [makeType('Draft1', { status: 'Draft' })] });
    show();

    expect(await screen.findByText('permits.list.wizard.noTypes')).toBeTruthy();
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect((await screen.findByTestId('wizard-step-error')).textContent).toContain('permits.list.wizard.typeRequired');
  });

  it('типи ще вантажаться — повідомлення, без майстра; помилка — з повтором', async () => {
    let calls = 0;
    stub({
      typesReply: () => {
        calls += 1;

        return calls === 1 ? json({ title: 'Unavailable', status: 503, errorCode: 'ECR-SYS-0503', correlationId: 'c' }, 503) : types();
      },
    });
    show();

    expect(screen.getByText('permits.list.wizard.loading')).toBeTruthy();
    expect(screen.queryByTestId('wizard-next')).toBeNull();
    expect((await screen.findByRole('alert')).textContent).toContain('Unavailable');

    fireEvent.click(screen.getByRole('button', { name: 'common.retry' }));
    expect(await screen.findByTestId('permit-type-pick')).toBeTruthy();
  });
});

describe('NewPermitWizard: фокус (PA-U-03)', () => {
  it('типи вантажаться після відкриття, Esc повертає фокус на кнопку-виклик, а не на BODY', async () => {
    // Типи приходять із затримкою: спершу модалка «loading», потім майстер.
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        await new Promise((resolve) => setTimeout(resolve, 150));

        return types();
      }),
    );
    function Host(): JSX.Element {
      const [open, setOpen] = useState(false);

      return (
        <MantineProvider theme={testTheme}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <button type="button" onClick={() => setOpen(true)}>
              Open wizard
            </button>
            {open && <NewPermitWizard opened onClose={() => setOpen(false)} />}
          </QueryClientProvider>
        </MantineProvider>
      );
    }
    render(
      <MemoryRouter>
        <Host />
      </MemoryRouter>,
    );
    const trigger = screen.getByRole('button', { name: 'Open wizard' });
    trigger.focus();
    fireEvent.click(trigger);
    await screen.findByTestId('permit-type-pick');

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(trigger));
  });
});

describe('NewPermitWizard: крок «Details» і створення', () => {
  async function toDetails(): Promise<void> {
    stub();
    show();
    await screen.findByTestId('permit-type-pick');
    fireEvent.click(screen.getByRole('radio', { name: /Air emission permit/ }));
    fireEvent.click(screen.getByTestId('wizard-next'));
    await screen.findByRole('textbox', { name: /permits\.list\.wizard\.number/ });
  }

  it('без номера далі не пускає: банер кроку й помилка під полем; запиту немає', async () => {
    await toDetails();

    fireEvent.click(screen.getByTestId('wizard-next'));

    expect((await screen.findByTestId('wizard-step-error')).textContent).toContain('permits.list.wizard.numberRequiredText');
    expect(screen.getByText('permits.list.wizard.numberRequired')).toBeTruthy();
    expect(screen.queryByTestId('wizard-summary')).toBeNull();
    expect(posts).toEqual([]);
  });

  it('підсумок показує введене; «Create permit» шле POST із типом, номером і датами за замовчуванням, веде на чернетку', async () => {
    await toDetails();

    fireEvent.change(screen.getByRole('textbox', { name: /permits\.list\.wizard\.number/ }), { target: { value: '  KZ-77 ' } });
    fireEvent.change(screen.getByRole('textbox', { name: /permits\.list\.wizard\.location/ }), { target: { value: 'Offshore complex' } });
    fireEvent.click(screen.getByTestId('wizard-next'));

    const summary = await screen.findByTestId('wizard-summary');
    expect(summary.textContent).toContain('Air emission permit');
    expect(summary.textContent).toContain('KZ-77');
    expect(summary.textContent).toContain('Offshore complex');

    fireEvent.click(screen.getByTestId('wizard-apply'));
    await waitFor(() => expect(where()).toBe('/admin/permits/41'));

    expect(posts).toEqual([
      {
        url: '/api/v1/permits',
        body: {
          typeCode: 'AirEmission',
          number: 'KZ-77',
          location: 'Offshore complex',
          issueDate: null,
          validFrom: '2026-10-19',
          validTo: '2027-10-19',
          reportingFlags: null,
        },
      },
    ]);
    expect(notify.showDone).toHaveBeenCalledWith('permits.list.done.created');
  });

  it('вибраний інший тип їде в запит', async () => {
    await toDetails();
    fireEvent.click(screen.getByTestId('wizard-back'));
    fireEvent.click(await screen.findByRole('radio', { name: /Water discharge permit/ }));
    fireEvent.click(screen.getByTestId('wizard-next'));
    fireEvent.change(await screen.findByRole('textbox', { name: /permits\.list\.wizard\.number/ }), { target: { value: 'W-1' } });
    fireEvent.click(screen.getByTestId('wizard-next'));
    await screen.findByTestId('wizard-summary');
    fireEvent.click(screen.getByTestId('wizard-apply'));

    await waitFor(() => expect(posts).toHaveLength(1));
    expect((posts[0]?.body as { typeCode: string }).typeCode).toBe('WaterDischarge');
  });

  it('відмова сервера (номер зайнятий): майстер лишається, банер із причиною, переходу немає', async () => {
    stub({
      create: () =>
        json({ title: 'Permit already exists', status: 409, errorCode: 'ECR-PRM-4095', correlationId: 'c' }, 409),
    });
    show();
    await screen.findByTestId('permit-type-pick');
    fireEvent.click(screen.getByRole('radio', { name: /Air emission permit/ }));
    fireEvent.click(screen.getByTestId('wizard-next'));
    fireEvent.change(await screen.findByRole('textbox', { name: /permits\.list\.wizard\.number/ }), { target: { value: 'KZ-1' } });
    fireEvent.click(screen.getByTestId('wizard-next'));
    await screen.findByTestId('wizard-summary');
    fireEvent.click(screen.getByTestId('wizard-apply'));

    await waitFor(() => expect(posts).toHaveLength(1));
    expect((await screen.findByTestId('wizard-step-error')).textContent).toContain('Permit already exists');
    expect(where()).toBe('/admin/permits');
    expect(notify.showDone).not.toHaveBeenCalled();
  });
});
