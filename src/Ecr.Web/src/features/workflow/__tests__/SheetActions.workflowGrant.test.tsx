import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions, effectiveGrant, meetsGrant } from '../SheetActions';

/**
 * F9 (`docs/build/UI-WALKTHROUGH.md`): «Submit» показувався за СТАНОМ аркуша,
 * а сусідні кнопки — ще й за правом. Оператор із грантом `Write` на проєкт
 * бачив синю кнопку «Submit», на яку сервер відповідає `ECR-ACCS-0403`.
 *
 * ⛔ Що саме перевіряє сервер (прочитано, не припущено):
 * `SubmitSheetHandler` → `CanSubmitAsync` → `EditRules.CanSubmit` — поріг
 * `GrantLevel.Submit` (3); `ApproveSheetHandler` (він же обробляє відхилення,
 * `approved: false`) → `CanApproveAsync` → `EditRules.CanApprove` — поріг
 * `GrantLevel.Approve` (4). Іменованого права на ці три переходи немає
 * ЖОДНОГО: рішення цілком на рівні гранта.
 *
 * ⚠ Тести нижче — не «кнопки немає»: кожен ставить умову, за якої кнопка
 * МУСИТЬ бути, і сусідню, за якої не мусить, — інакше зелений вийшов би й
 * від компонента, що не рендерить нічого.
 */

const SlowEnvTimeout = 400_000;

const DocumentId = 1;
const SheetDefId = 42;
const PeriodKey = 202601;
const ProjectId = 7;

/** Той самий ключ, яким `DocumentPage` тримає зведення документа. */
const SummaryKey = ['document', DocumentId, PeriodKey];

const Summary = {
  businessKey: 'DOC-1',
  createdAt: '2026-01-01T00:00:00Z',
  id: DocumentId,
  nameL10n: null,
  projectId: ProjectId,
  sheetCount: 1,
  sheetStates: { S1: 'Draft' },
};

function currentUser(options: {
  grants: Record<string, string>;
  denies?: string[];
  isSimulation?: boolean;
}) {
  return {
    denies: options.denies ?? [],
    grants: options.grants,
    isSimulation: options.isSimulation ?? false,
    language: 'en',
    mustChangePassword: false,

    // ⚠ Право на перерахунок є ЗАВЖДИ: кнопка «Recalculate» служить якорем —
    // поки вона на екрані, компонент точно відрендерився і профіль доїхав,
    // тож відсутність «Submit» означає саме рішення про грант, а не те, що
    // тест зазирнув до першого рендеру.
    permissions: ['Calculation.Recalculate', 'Document.Reopen'],
    simulatedForUserId: null,
    userId: 9,
    userName: 'tester',
  };
}

function mockFetch(me: ReturnType<typeof currentUser>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(me), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

/**
 * Рендерить панель так, як це робить `DocumentPage`: зведення документа вже
 * лежить у кеші під своїм ключем.
 *
 * @param options.seedSummary `false` — кеш порожній (перевірка «відмова
 * закрита, а не відкрита»).
 */
function show(options: {
  grants: Record<string, string>;
  denies?: string[];
  isSimulation?: boolean;
  state: string;
  seedSummary?: boolean;
}): void {
  mockFetch(currentUser(options));

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (options.seedSummary !== false) client.setQueryData(SummaryKey, Summary);

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions
          documentId={DocumentId}
          sheetDefId={SheetDefId}
          periodKey={PeriodKey}
          state={options.state}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Чекає на якір і повертає керування, коли профіль уже застосовано. */
async function anchor(): Promise<void> {
  await screen.findByRole('button', { name: /recalculate/i }, { timeout: SlowEnvTimeout });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('effectiveGrant: дзеркало EditRules.Effective для аркуша', () => {
  const me = (grants: Record<string, string>, denies: string[] = []) =>
    currentUser({ grants, denies });

  it('немає жодного гранта — None', () => {
    expect(effectiveGrant(me({}), ProjectId, SheetDefId)).toBe('None');
  });

  it('грант на проєкт — він і діє', () => {
    expect(effectiveGrant(me({ 'Project:7': 'Write' }), ProjectId, SheetDefId)).toBe('Write');
  });

  it('грант на аркуш перекриває грант на проєкт — навіть УНИЗ', () => {
    // ⛔ Саме вниз: «найдрібніший оголошений виграє» — це спосіб ЗВУЗИТИ
    // права точково, а не лише розширити. Реалізація «беремо максимум»
    // виглядала б правильною на розширенні й мовчки ламалася б тут.
    const grant = effectiveGrant(me({ 'Project:7': 'Manage', 'Sheet:42': 'Read' }), ProjectId, SheetDefId);

    expect(grant).toBe('Read');
  });

  it('явна заборона перемагає будь-який дозвіл (ФВ-6.6)', () => {
    const grant = effectiveGrant(me({ 'Project:7': 'Manage' }, ['Project:7']), ProjectId, SheetDefId);

    expect(grant).toBe('None');
  });

  it('заборона на АРКУШІ теж перемагає грант на проєкті', () => {
    const grant = effectiveGrant(me({ 'Project:7': 'Manage' }, ['Sheet:42']), ProjectId, SheetDefId);

    expect(grant).toBe('None');
  });

  it('невідома назва рівня — None, а не «пропустимо»', () => {
    expect(effectiveGrant(me({ 'Project:7': 'Superuser' }), ProjectId, SheetDefId)).toBe('None');
  });

  it('проєкт невідомий — None', () => {
    expect(effectiveGrant(me({ 'Project:7': 'Manage' }), null, SheetDefId)).toBe('None');
  });

  it('профіль без полів `grants`/`denies` — None, а не падіння', () => {
    // ⛔ Профіль приходить мережею, і тип тут нічого не гарантує. Падіння в
    // цій функції знесло б через межу помилок усю шапку документа — рівно це
    // й сталося на першому повному прогоні (`renderFeedback.test.tsx`, чия
    // заглушка `/me` цих полів не має): «Cannot read properties of undefined
    // (reading 'includes')», і замість сітки — екран помилки.
    const broken = { language: 'en', permissions: [], userId: 1 } as unknown as Parameters<
      typeof effectiveGrant
    >[0];

    expect(effectiveGrant(broken, ProjectId, SheetDefId)).toBe('None');
  });

  it('пороги: Write < Submit < Approve', () => {
    expect(meetsGrant('Write', 'Submit')).toBe(false);
    expect(meetsGrant('Submit', 'Submit')).toBe(true);
    expect(meetsGrant('Submit', 'Approve')).toBe(false);
    expect(meetsGrant('Approve', 'Approve')).toBe(true);
    expect(meetsGrant('Manage', 'Approve')).toBe(true);
  });
});

describe('SheetActions: «Submit» закрито тим самим порогом, що й у сервера', () => {
  it(
    'грант Write (рівно випадок зі знімка 07-document-open.png) — кнопки «Submit» немає',
    async () => {
      show({ grants: { 'Project:7': 'Write' }, state: 'Draft' });
      await anchor();

      // ⛔ Мутаційний доказ: поверни умову показу на саме `isAllowed('submit',
      // state)` — і цей рядок стане червоним, бо кнопка з'явиться.
      expect(screen.queryByRole('button', { name: /submit/i })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'грант Submit — кнопка «Submit» на місці',
    async () => {
      show({ grants: { 'Project:7': 'Submit' }, state: 'Draft' });

      expect(
        await screen.findByRole('button', { name: /submit/i }, { timeout: SlowEnvTimeout }),
      ).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'грант Manage на проєкт, але Write на цей аркуш — «Submit» немає',
    async () => {
      show({ grants: { 'Project:7': 'Manage', 'Sheet:42': 'Write' }, state: 'Draft' });
      await anchor();

      expect(screen.queryByRole('button', { name: /submit/i })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'симуляція «очима користувача» — «Submit» немає навіть із Manage',
    async () => {
      // ⚠ Той самий перший крок, що й у сервера: `EditRules.CanSubmit`
      // починається з `profile.IsSimulation` → `SimulationReadOnly`.
      show({ grants: { 'Project:7': 'Manage' }, isSimulation: true, state: 'Draft' });
      await anchor();

      expect(screen.queryByRole('button', { name: /submit/i })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'зведення документа ще не в кеші — «Submit» немає (відмова закрита)',
    async () => {
      show({ grants: { 'Project:7': 'Manage' }, state: 'Draft', seedSummary: false });
      await anchor();

      expect(screen.queryByRole('button', { name: /submit/i })).toBeNull();
    },
    SlowEnvTimeout,
  );
});

describe('SheetActions: «Approve»/«Reject» — поріг Approve, а не Submit', () => {
  it(
    'грант Submit на поданому аркуші — ані «Approve», ані «Reject»',
    async () => {
      show({ grants: { 'Project:7': 'Submit' }, state: 'Submitted' });
      await anchor();

      // ⛔ Мутаційний доказ на ПОРІГ, а не на наявність перевірки: постав у
      // `RequiredGrant` для `approve`/`reject` рівень `Submit` — обидва рядки
      // почервоніють, бо кнопки з'являться.
      expect(screen.queryByRole('button', { name: /approve/i })).toBeNull();
      expect(screen.queryByRole('button', { name: /reject/i })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'грант Approve на поданому аркуші — обидві кнопки на місці',
    async () => {
      show({ grants: { 'Project:7': 'Approve' }, state: 'Submitted' });

      expect(
        await screen.findByRole('button', { name: /approve/i }, { timeout: SlowEnvTimeout }),
      ).toBeTruthy();
      expect(screen.getByRole('button', { name: /reject/i })).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'заборона на проєкт при гранті Manage — жодної кнопки робочого процесу',
    async () => {
      show({ grants: { 'Project:7': 'Manage' }, denies: ['Project:7'], state: 'Submitted' });
      await anchor();

      expect(screen.queryByRole('button', { name: /approve/i })).toBeNull();
      expect(screen.queryByRole('button', { name: /reject/i })).toBeNull();
    },
    SlowEnvTimeout,
  );
});
