import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { showDone } from '@/shared/ui/notify';
import { NewTypeWizard } from '../NewTypeWizard';
import { FixtureTypes } from './typesFixture';

/**
 * Майстер «New permit type» (PM-08a): перевірка назви, підсумок, створення, копія наявного типу,
 * відображення `409 ECR-PRM-4091` на поле назви, перехід у редактор з тостом.
 *
 * Мутаційний доказ (вручну, відновлено): без `copyFromCode` у тілі падає «копія наявного типу»; без гілки
 * `ECR-PRM-4091` падає «409: код зайнятий»; без `checkNewTypeName` у `validate` падають чотири тести перевірки;
 * `navigate` на код із запиту замість коду з відповіді сервера валить «створення» (відповідь навмисно інша).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
}));
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

let posted: Record<string, unknown> | null = null;

function stubServer(reply: (body: Record<string, unknown>) => Response): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).endsWith('/api/v1/permit-types') && init?.method === 'POST') {
        posted = JSON.parse(String(init.body)) as Record<string, unknown>;

        return reply(posted);
      }

      return json(null);
    }),
  );
}

function Where(): null {
  (globalThis as { __where?: string }).__where = useLocation().pathname;

  return null;
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/permits/types']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route
              path="*"
              element={
                <>
                  <Where />
                  <NewTypeWizard opened types={FixtureTypes} onClose={() => undefined} />
                </>
              }
            />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.mocked(showDone).mockClear();
  posted = null;
  delete (globalThis as { __where?: string }).__where;
});

const where = (): string | undefined => (globalThis as { __where?: string }).__where;
const next = (): HTMLElement => screen.getByTestId('wizard-next');
const nameInput = (): HTMLInputElement => screen.getByLabelText(/permits\.types\.wizard\.name\b/, { selector: 'input' });

function typeName(value: string): void {
  fireEvent.change(nameInput(), { target: { value } });
}

describe('NewTypeWizard: перевірка назви', () => {
  it('порожня назва: банер кроку, підпис під полем і фокус на полі', async () => {
    show();
    // ⚠ Пастка фокуса Mantine ставить початковий фокус на тіло кроку із запізненням; без очікування вона
    // перебила б фокус, який майстер ставить на хибне поле.
    await waitFor(() => expect(document.activeElement).toBe(screen.getByTestId('wizard-step-body')));
    fireEvent.click(next());

    expect((await screen.findByTestId('wizard-step-error')).textContent).toContain('permits.types.wizard.nameRequiredText');
    expect(screen.getByText('permits.types.wizard.nameRequired')).toBeTruthy();
    expect(document.activeElement).toBe(nameInput());
    expect(screen.queryByTestId('wizard-summary')).toBeNull();
  });

  it('дубль назви (регістр не важливий): помилка, на підсумок не пускає', async () => {
    show();
    typeName('  AIR EMISSION permit ');
    fireEvent.click(next());

    expect(await screen.findByText('permits.types.wizard.nameTaken')).toBeTruthy();
    expect(screen.getByTestId('wizard-step-error').textContent).toContain('permits.types.wizard.nameTakenText');
  });

  it('назва без латинських літер: код не вийде — пояснення замість мовчазної відмови сервера', async () => {
    show();
    typeName('Дозвіл на шум');
    fireEvent.click(next());

    expect(await screen.findByText('permits.types.wizard.codeInvalid')).toBeTruthy();
  });

  it('зайнятий код від іншої назви: помилка з кодом', async () => {
    show();
    typeName('waste Storage');
    fireEvent.click(next());

    expect((await screen.findByTestId('wizard-step-error')).textContent).toContain('"code":"WasteStorage"');
  });

  it('код показується підказкою під підписом і оновлюється з назвою', () => {
    show();
    expect(screen.queryByText(/permits\.types\.wizard\.code /)).toBeNull();
    typeName('Noise permit');
    expect(screen.getByText('permits.types.wizard.code {"code":"NoisePermit"}')).toBeTruthy();
    typeName('Noise permit 2');
    expect(screen.getByText('permits.types.wizard.code {"code":"NoisePermit2"}')).toBeTruthy();
  });
});

describe('NewTypeWizard: створення', () => {
  it('підсумок за макетом, тіло запиту, тост і перехід у редактор за кодом з ВІДПОВІДІ сервера', async () => {
    stubServer(() => json({ code: 'NoisePermitSrv' }, 201));
    show();
    typeName('Noise permit');
    fireEvent.click(next());

    const summary = await screen.findByTestId('wizard-summary');
    expect(summary.textContent).toContain('Noise permit');
    expect(summary.textContent).toContain('permits.category.Air');
    for (const field of ['Number', 'Location', 'Issued', 'ValidFrom', 'ValidTo', 'ActualEnd']) {
      expect(summary.textContent).toContain(`permits.types.wizard.field${field}`);
    }
    expect(summary.textContent).toContain('permits.types.wizard.sumStartsValue');

    fireEvent.click(screen.getByTestId('wizard-apply'));
    await waitFor(() => expect(where()).toBe('/admin/permits/types/NoisePermitSrv'));

    expect(posted).toEqual({ code: 'NoisePermit', nameL10n: { en: 'Noise permit' }, category: 'Air' });
    expect(showDone).toHaveBeenCalledWith('permits.types.created');
  });

  it('копія наявного типу: у «Start from» назви наявних, у запит іде copyFromCode', async () => {
    stubServer(() => json({ code: 'Noise' }, 201));
    show();
    typeName('Noise');
    fireEvent.click(screen.getByLabelText(/permits\.types\.wizard\.start\b/, { selector: 'input' }));
    const options = await screen.findAllByRole('option');
    expect(options.map((option) => option.textContent)).toEqual([
      'permits.types.wizard.startBlank',
      'permits.types.wizard.startCopy {"name":"Air emission permit"}',
      'permits.types.wizard.startCopy {"name":"Water discharge permit"}',
      'permits.types.wizard.startCopy {"name":"Waste storage permit"}',
    ]);
    fireEvent.click(options[2]!);
    fireEvent.click(next());
    fireEvent.click(await screen.findByTestId('wizard-apply'));

    await waitFor(() => expect(posted).not.toBeNull());
    expect(posted).toEqual({
      code: 'Noise',
      nameL10n: { en: 'Noise' },
      category: 'Air',
      copyFromCode: 'WaterDischarge',
    });
  });

  it('категорія з вибору йде в запит', async () => {
    stubServer(() => json({ code: 'Noise' }, 201));
    show();
    typeName('Noise');
    fireEvent.click(screen.getByLabelText(/permits\.types\.wizard\.category\b/, { selector: 'input' }));
    fireEvent.click(await screen.findByRole('option', { name: 'permits.category.Waste' }));
    fireEvent.click(next());
    fireEvent.click(await screen.findByTestId('wizard-apply'));

    await waitFor(() => expect(posted?.['category']).toBe('Waste'));
  });
});

describe('NewTypeWizard: відмови сервера', () => {
  const taken = (): Response =>
    json(
      { title: 'Taken', status: 409, detail: 'exists', errorCode: 'ECR-PRM-4091', correlationId: 'c-1' },
      409,
    );

  it('409 ECR-PRM-4091: банер у підсумку, «Back» веде до назви з помилкою «код зайнятий», переходу немає', async () => {
    stubServer(taken);
    show();
    typeName('Noise permit');
    fireEvent.click(next());
    fireEvent.click(await screen.findByTestId('wizard-apply'));

    const banner = await screen.findByTestId('wizard-step-error');
    expect(banner.textContent).toContain('permits.types.wizard.codeTakenText {"code":"NoisePermit"}');

    fireEvent.click(screen.getByTestId('wizard-back'));
    expect(await screen.findByText('permits.types.wizard.codeTaken')).toBeTruthy();
    expect(nameInput().value).toBe('Noise permit');
    expect(where()).toBe('/admin/permits/types');
    expect(showDone).not.toHaveBeenCalled();
  });

  it('інша відмова (403): текст відмови в банері, поле назви без помилки', async () => {
    stubServer(() =>
      json({ title: 'Forbidden', status: 403, detail: 'No right', errorCode: 'ECR-AUTH-0403', correlationId: 'c-2' }, 403),
    );
    show();
    typeName('Noise permit');
    fireEvent.click(next());
    fireEvent.click(await screen.findByTestId('wizard-apply'));

    const banner = await screen.findByTestId('wizard-step-error');
    expect(within(banner).queryByText(/codeTaken/)).toBeNull();
    expect(banner.textContent).not.toContain('permits.types.wizard.codeTakenText');
    expect(where()).toBe('/admin/permits/types');
  });
});
