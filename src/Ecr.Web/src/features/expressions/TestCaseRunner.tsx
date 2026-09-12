import { useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Stack, Table, Text } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { SimulationResultDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { showApiError } from '@/shared/ui/notify';

/**
 * Прогін золотого набору просто з редактора (`ФВ-9.15a`, `ФВ-13.7`).
 *
 * ⛔ До цього побачити «зійшлося чи ні» можна було лише СПРОБУВАВШИ
 * опублікувати: порівняння з очікуваними числами жило приватним методом
 * усередині публікації. Прогін «без запису» проганяв ті самі тести і мовчав
 * про результат — показував числа й різницю з чинною версією, але не те
 * єдине, заради чого його відкривають.
 *
 * ⚠ Прогін нічого не записує: ані в `calc.CalculationResult`, ані в історію
 * прогонів. Інакше кожне натискання «Прогнати» засмічувало б журнал, за яким
 * відновлюють числа, і питання «яким прогоном пораховано цей звіт» отримало б
 * відповіді, яких ніхто не запускав.
 */
export interface TestCaseRunnerProps {
  /** Методологія, чиї тести проганяємо. */
  readonly methodologyId: number;
  /** Версія методології. */
  readonly methodologyVersionId: number;
  /** Період, на даних якого проганяємо. */
  readonly periodKey: number;
}

/** Кнопка прогону і вердикти золотого набору. */
export function TestCaseRunner(props: TestCaseRunnerProps): JSX.Element {
  const { methodologyId, methodologyVersionId, periodKey } = props;
  const [result, setResult] = useState<SimulationResultDto | null>(null);

  const run = useMutation({
    mutationFn: () =>
      apiFetch<SimulationResultDto>(`/api/v1/methodologies/${String(methodologyId)}/simulate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ methodologyVersionId, periodKey }),
      }),
    onSuccess: setResult,
    onError: showApiError,
  });

  return (
    <Stack gap="sm">
      <Group gap="sm">
        <Button onClick={() => run.mutate()} loading={run.isPending}>
          {t('expressions.runTests')}
        </Button>

        {result !== null && <Verdict result={result} />}
      </Group>

      {result !== null && result.testCases.length === 0 && (
        // ⛔ «Тестів немає» — це НЕ «все гаразд». Публікація без зеленого
        // набору заборонена (`ФВ-9.12`), і мовчазна порожнеча тут читалася б
        // як успіх — рівно до відмови публікації.
        <Alert color="statusWarning" title={t('expressions.noTestCases')}>
          {t('expressions.noTestCasesHint')}
        </Alert>
      )}

      {result !== null && result.testCases.length > 0 && <Verdicts result={result} />}
    </Stack>
  );
}

function Verdict({ result }: { readonly result: SimulationResultDto }): JSX.Element {
  return (
    <Badge color={result.isGreen ? 'green' : 'statusError'}>
      {result.isGreen ? t('expressions.testsGreen') : t('expressions.testsRed')}
    </Badge>
  );
}

/**
 * Перелік тестів із розбіжностями.
 *
 * ⚠ Розбіжність показує ОБИДВА числа і допуск. «Тест не пройшов» без них
 * означає, що методолог відкриває вираз і гадає, у який бік помилка.
 */
function Verdicts({ result }: { readonly result: SimulationResultDto }): JSX.Element {
  return (
    <Table striped withTableBorder>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t('expressions.testCase')}</Table.Th>
          <Table.Th>{t('expressions.testOutput')}</Table.Th>
          <Table.Th>{t('expressions.testActual')}</Table.Th>
          <Table.Th>{t('expressions.testExpected')}</Table.Th>
          <Table.Th>{t('expressions.testTolerance')}</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {result.testCases.map((verdict) =>
          verdict.mismatches.length === 0 ? (
            <Table.Tr key={verdict.code}>
              <Table.Td>{verdict.code}</Table.Td>
              <Table.Td colSpan={4}>
                <Badge color="statusSuccess">{t('expressions.testsGreen')}</Badge>
              </Table.Td>
            </Table.Tr>
          ) : (
            verdict.mismatches.map((mismatch) => (
              <Table.Tr key={`${verdict.code}-${mismatch.outputCode}`}>
                <Table.Td>{verdict.code}</Table.Td>
                <Table.Td>{mismatch.outputCode}</Table.Td>
                <Table.Td>
                  {/* ⛔ Відсутній вихід — не порожня клітинка, а названа
                      відсутність: методологія, яка перестала рахувати
                      оголошений вихід, інакше виглядала б як «майже зійшлося». */}
                  {mismatch.actual === null ? (
                    <Text span c="statusError">
                      {t('expressions.testMissing')}
                    </Text>
                  ) : (
                    mismatch.actual
                  )}
                </Table.Td>
                <Table.Td>{mismatch.expected}</Table.Td>
                <Table.Td>{mismatch.tolerance}</Table.Td>
              </Table.Tr>
            ))
          ),
        )}
      </Table.Tbody>
    </Table>
  );
}
