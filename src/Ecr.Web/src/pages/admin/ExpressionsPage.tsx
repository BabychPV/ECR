import { useMemo, useState, type JSX } from 'react';
import { Alert, Badge, Group, List, Select, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type {
  ExpressionDialect,
  ExpressionValidationDto,
  MethodologyDto,
  TemplatePage,
  TemplateVersionPage,
} from '@/api/types';
import { ExpressionEditor } from '@/features/expressions/ExpressionEditor';
import { TestCaseRunner } from '@/features/expressions/TestCaseRunner';
import type { ExpressionPlacement } from '@/features/expressions/api';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';

/**
 * Редактор виразів (`ФВ-9.15a`, область 10 у `ФВ-14.3`).
 *
 * ⛔ Що це таке і чим воно НЕ є. Це місце, де вираз пишуть і перевіряють проти
 * ОБРАНОЇ версії — до того, як він потрапить у методологію чи в шаблон. Це не
 * менеджер формул: формули методології редагує `B-3`, формули шаблону —
 * конструктор шаблону, і жодного з них ще немає.
 *
 * ⚠ Тому головна цінність тут — не сторінка, а компонент
 * `ExpressionEditor`: діалект у нього ПАРАМЕТР (`D-113`), і коли з'являться
 * обидва конфігуратори, він стане в них без жодної правки. Сторінка робить його
 * досяжним сьогодні і дає місце, де перевірка виразу нічого не змінює в даних.
 */
export function ExpressionsPage(): JSX.Element {
  const [dialect, setDialect] = useState<ExpressionDialect>('Template');
  const [templateVersionId, setTemplateVersionId] = useState<string | null>(null);
  const [methodologyVersionId, setMethodologyVersionId] = useState<string | null>(null);
  const [expression, setExpression] = useState('');
  const [result, setResult] = useState<ExpressionValidationDto | null>(null);

  const templates = useQuery({
    queryKey: ['templates'],
    queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),
    enabled: dialect === 'Template',
  });

  const methodologies = useQuery({
    queryKey: ['methodologies'],
    queryFn: () => apiFetch<MethodologyDto[]>('/api/v1/methodologies'),
    enabled: dialect === 'Methodology',
  });

  const firstTemplateId = templates.data?.items[0]?.id;

  // ⛔ Q-275: `GET /api/v1/templates/{id}/versions` — курсорний ендпоінт
  // (Q-225), відповідь `{items, nextCursor, totalCount}`, а НЕ голий масив.
  // Тут стояв тип `TemplateVersionSummary[]`, тож `.data` при пагінованій
  // відповіді був ОБ'ЄКТОМ, не `undefined` — `?? []` не рятував, і виклик
  // `.map` нижче падав `TypeError` на кожному відкритті вкладки «Вираз
  // шаблону» (той самий дефект, що Q-274 в `CreateProjectModal.tsx`).
  // Взірець — `TemplatesPage.tsx`/`CreateProjectModal.tsx`: `TemplateVersionPage`,
  // `?limit=100`, `.data?.items`.
  const versions = useQuery({
    queryKey: ['template-versions', firstTemplateId],
    queryFn: () =>
      apiFetch<TemplateVersionPage>(
        `/api/v1/templates/${String(firstTemplateId)}/versions?limit=100`,
      ),
    enabled: firstTemplateId !== undefined,
  });

  // Яку саме версію методології обрано — потрібні обидва ідентифікатори:
  // симуляція адресується методологією, а набір тестів належить версії.
  const selectedMethodology = useMemo(() => {
    if (methodologyVersionId === null) return undefined;

    for (const methodology of methodologies.data ?? []) {
      for (const version of methodology.versions) {
        if (String(version.id) === methodologyVersionId) {
          return { methodologyId: methodology.id, versionId: version.id };
        }
      }
    }

    return undefined;
  }, [methodologies.data, methodologyVersionId]);

  // ⚠ Об'єкт розміщення мемоізується: редактор перезапитує склад мови і
  // перевірку на КОЖНУ його зміну за посиланням, і новий літерал на кожен
  // перерендер перетворив би затримку перевірки на ніщо.
  const placement = useMemo<ExpressionPlacement>(
    () => ({
      templateVersionId: numberOrUndefined(templateVersionId),
      methodologyVersionId: numberOrUndefined(methodologyVersionId),
    }),
    [templateVersionId, methodologyVersionId],
  );

  return (
    <Stack gap="md">
      <PageHeader title={t('expressions.title')} />

      <Group align="flex-end" gap="md">
        <Select
          label={t('expressions.dialect')}
          miw={220}
          allowDeselect={false}
          value={dialect}
          data={[
            { value: 'Template', label: t('expressions.dialectTemplate') },
            { value: 'Methodology', label: t('expressions.dialectMethodology') },
          ]}
          onChange={(value) => {
            // ⛔ Режиму C# у переліку НЕМАЄ — не прихований і не вимкнений
            // (`ФВ-9.15a`). Вимкнений пункт обіцяє те, чого не існує, і кожен,
            // хто його побачить, спитає, коли ввімкнуть.
            setDialect(value === 'Methodology' ? 'Methodology' : 'Template');
            setResult(null);
          }}
        />

        {dialect === 'Template' && (
          <Select
            label={t('expressions.templateVersion')}
            placeholder={t('expressions.anyVersion')}
            miw={260}
            clearable
            value={templateVersionId}
            data={(versions.data?.items ?? []).map((v) => ({
              value: String(v.id),
              label: `${v.version} · ${v.status}`,
            }))}
            onChange={setTemplateVersionId}
          />
        )}

        {dialect === 'Methodology' && (
          <Select
            label={t('expressions.methodologyVersion')}
            placeholder={t('expressions.anyVersion')}
            miw={260}
            clearable
            value={methodologyVersionId}
            data={(methodologies.data ?? []).flatMap((m) =>
              m.versions.map((v) => ({
                value: String(v.id),
                label: `${m.code} · ${v.versionNumber}`,
              })),
            )}
            onChange={setMethodologyVersionId}
          />
        )}
      </Group>

      <ExpressionEditor
        value={expression}
        onChange={setExpression}
        dialect={dialect}
        placement={placement}
        ariaLabel={t('expressions.editorLabel')}
        onValidated={setResult}
      />

      <Findings result={result} />

      {/*
        ⛔ Прогін тестів з'являється ЛИШЕ коли обрана версія методології, і це
        не зручність. Золотий набір належить версії (`calc.TestCase`); кнопка
        без неї не мала б що проганяти, а показана і бездіяльна — обіцяла б
        перевірку, якої не буде.
      */}
      {dialect === 'Methodology' && selectedMethodology !== undefined && (
        <TestCaseRunner
          methodologyId={selectedMethodology.methodologyId}
          methodologyVersionId={selectedMethodology.versionId}
          periodKey={currentPeriodKey()}
        />
      )}
    </Stack>
  );
}

/**
 * Поточний період у форматі `PeriodKey` (`R-A6`: рік × 100 + порядковий).
 *
 * ⚠ Місяць, а не квартал: прогін іде на даних періоду, і найдрібніший період
 * дає найшвидшу відповідь. Для методологій із іншою періодичністю це
 * найближче наближення, яке не потребує окремого поля на цій сторінці.
 */
function currentPeriodKey(): number {
  const now = new Date();

  return now.getUTCFullYear() * 100 + now.getUTCMonth() + 1;
}

/**
 * Зауваження і межі перевірки.
 *
 * ⛔ Пропущені перевірки показуються ЗАВЖДИ, а не лише коли зауважень немає.
 * Порожній перелік без версії шаблону означає «синтаксис цілий», а не «вираз
 * правильний»; сплутати ці два твердження означає пообіцяти публікацію, якої не
 * буде — і дізнатися про це вже на публікації.
 */
function Findings({ result }: { readonly result: ExpressionValidationDto | null }): JSX.Element {
  return (
    <AsyncBoundary<ExpressionValidationDto>
      isPending={false}
      error={null}
      data={result ?? undefined}
      isEmpty={() => false}
      emptyTitle={t('expressions.startTyping')}
      emptyHint={t('expressions.startTypingHint')}
      skeleton="none"
    >
      {(value) => (
        <Stack gap="xs">
          <Group gap="xs">
            {value.diagnostics.length === 0 ? (
              <Badge color="statusSuccess">{t('expressions.noFindings')}</Badge>
            ) : (
              <Badge color="statusError">
                {t('expressions.findings', { count: value.diagnostics.length })}
              </Badge>
            )}

            {value.resultType !== null && (
              <Text size="sm" c="dimmed">
                {t('expressions.resultType', { type: value.resultType })}
              </Text>
            )}
          </Group>

          {value.diagnostics.length > 0 && (
            <List size="sm" spacing="xs">
              {value.diagnostics.map((d, index) => (
                <List.Item key={`${d.code}-${String(d.position)}-${String(index)}`}>
                  <Text span size="sm" fw={600}>
                    {d.code}
                  </Text>{' '}
                  {d.message}
                </List.Item>
              ))}
            </List>
          )}

          {value.skippedChecks.length > 0 && (
            <Alert color="statusWarning" title={t('expressions.skippedTitle')}>
              {value.skippedChecks.map((check) => t(`expressions.check.${check}`)).join('; ')}
            </Alert>
          )}
        </Stack>
      )}
    </AsyncBoundary>
  );
}

function numberOrUndefined(value: string | null): number | undefined {
  if (value === null) return undefined;

  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}
