import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologyRulesPanel } from '@/features/methodologies/MethodologyContentPanels';
import type { MethodologyRuleDto } from '@/api/types';

/**
 * UI-аудит, lane 5: `lane5-row-rule-catchall-priority-not-warned.md` —
 * діалог правила відбору рядків сам стверджує інваріант («An empty object
 * matches the whole table - which is why such a rule must have the lowest
 * priority» / «The lower the number, the higher the priority»), але ніяк
 * його не перевіряв: catch-all (`{}`) зберігався на найвищому пріоритеті
 * (число `1`) без жодного попередження, і мовчки перекривав усі інші
 * правила назавжди.
 *
 * Тест доводить ОБИДВА боки інваріанту:
 * 1. Catch-all-чернетка НЕ на найнижчому пріоритеті серед наявних правил —
 *    попередження з'являється.
 * 2. Наявний catch-all уже стоїть на пріоритеті, нижчому за число нового
 *    (звичайного) правила, — інше попередження, «це правило ніколи не
 *    спрацює».
 * І що ЖОДНЕ з двох не з'являється там, де інваріант не порушено — інакше
 * «показувати завжди» пройшло б так само, як і правильний фікс.
 */
function rule(overrides: Partial<MethodologyRuleDto>): MethodologyRuleDto {
  return {
    id: overrides.id ?? 1,
    code: overrides.code ?? 'R1',
    matchJson: overrides.matchJson ?? '{"tableCode":"T1"}',
    priority: overrides.priority ?? 1,
    isActive: overrides.isActive ?? true,
  };
}

function mockFetch(rules: MethodologyRuleDto[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/rules')) {
        return new Response(JSON.stringify(rules), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      return new Response(JSON.stringify([]), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MethodologyRulesPanel methodologyId={1} versionId={1} editable />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('MethodologyRulesPanel: попередження про пріоритет catch-all (lane5)', () => {
  it(
    'нова catch-all-чернетка (пріоритет 1) при наявному іншому правилі на 1 — попередження є',
    async () => {
      mockFetch([rule({ id: 1, code: 'SPECIFIC', priority: 1 })]);
      show();

      fireEvent.click(await screen.findByText('⟦methodologies.addRule⟧', {}, { timeout: SlowEnvTimeout }));

      // Чернетка нового правила: matchJson='{}' , priority=1 за замовчуванням —
      // саме той стан, що й у репро аудиту.
      expect(
        await screen.findByText('⟦methodologies.catchAllNotLowestTitle⟧', {}, { timeout: SlowEnvTimeout }),
      ).not.toBeNull();
      expect(screen.queryByText('⟦methodologies.shadowedByCatchAllTitle⟧')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'нова catch-all-чернетка — єдине правило в списку — попередження НЕМАЄ',
    async () => {
      mockFetch([]);
      show();

      fireEvent.click(await screen.findByText('⟦methodologies.addRule⟧', {}, { timeout: SlowEnvTimeout }));

      // ⛔ Нічого шкодувати: правил ще немає, тож «не найнижчий пріоритет»
      // не має сенсу — попередження НЕ повинно з'явитись.
      await screen.findByText('⟦methodologies.matchJson⟧', {}, { timeout: SlowEnvTimeout });
      expect(screen.queryByText('⟦methodologies.catchAllNotLowestTitle⟧')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'наявний catch-all на нижчому числі блокує нову чернетку — інше попередження',
    async () => {
      mockFetch([rule({ id: 1, code: 'CATCHALL', matchJson: '{}', priority: 1 })]);
      show();

      fireEvent.click(await screen.findByText('⟦methodologies.addRule⟧', {}, { timeout: SlowEnvTimeout }));

      const priorityInput = await screen.findByLabelText(
        '⟦methodologies.priority⟧',
        {},
        { timeout: SlowEnvTimeout },
      );
      fireEvent.change(priorityInput, { target: { value: '2' } });

      const matchJsonInput = screen.getByLabelText('⟦methodologies.matchJson⟧');
      fireEvent.change(matchJsonInput, { target: { value: '{"tableCode":"T1"}' } });

      expect(
        await screen.findByText('⟦methodologies.shadowedByCatchAllTitle⟧', {}, { timeout: SlowEnvTimeout }),
      ).not.toBeNull();
      expect(screen.queryByText('⟦methodologies.catchAllNotLowestTitle⟧')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
