import { useId, useState, type JSX, type ReactNode } from 'react';
import { Badge, Button, Code, Group, Paper, Skeleton, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { ExpressionFunctionDto } from '@/api/types';
import { expressionMetadata } from '@/features/expressions/api';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PeriodProperties, SyntaxExamples, type FormulaExample } from './formulaSyntax';

/** Рядок довідки: позначення (код) і пояснення. */
function Row({ token, children }: { readonly token: string; readonly children: ReactNode }): JSX.Element {
  return (
    <Group gap="xs" wrap="wrap" align="baseline" data-syntax-token={token}>
      <Code style={{ whiteSpace: 'nowrap' }}>{token}</Code>
      <Text size="sm" style={{ flex: '1 1 16rem', minWidth: 0 }}>
        {children}
      </Text>
    </Group>
  );
}

/** Підзаголовок групи всередині діалогу (`h3`: заголовок модалки — `h2`). */
function GroupTitle({ children }: { readonly children: ReactNode }): JSX.Element {
  return (
    <Title order={3} size="h6">
      {children}
    </Title>
  );
}

/**
 * Опис прикладу.
 *
 * ⚠ Ключі ЛІТЕРАЛАМИ в окремих гілках, а не `t(\`…${id}\`)`: динамічний ключ
 * вимагає запису в `DynamicKeySites` (сторож каталогу), а дванадцять прикладів
 * такої ціни не варті — той самий прийом, що `stateLabel` у `RuleCoveragePanel`.
 */
function exampleLabel(example: FormulaExample): string {
  switch (example.id) {
    case 'argConstant':
      return t('methodologies.syntaxHelp.exArgConstant');
    case 'dotted':
      return t('methodologies.syntaxHelp.exDotted');
    case 'formulaRefs':
      return t('methodologies.syntaxHelp.exFormulaRefs');
    case 'ternary':
      return t('methodologies.syntaxHelp.exTernary');
    case 'if':
      return t('methodologies.syntaxHelp.exIf');
    case 'not':
      return t('methodologies.syntaxHelp.exNot');
    case 'pow':
      return t('methodologies.syntaxHelp.exPow');
    case 'round':
      return t('methodologies.syntaxHelp.exRound');
    case 'in':
      return t('methodologies.syntaxHelp.exIn');
    case 'period':
      return t('methodologies.syntaxHelp.exPeriod');
    case 'modulo':
      return t('methodologies.syntaxHelp.exModulo');
    default:
      return t('methodologies.syntaxHelp.exLogic');
  }
}

/** Арність функції словами каталогу: рівно / діапазон / не менше. */
function arity(fn: ExpressionFunctionDto): string {
  if (fn.maxArgs === null) return t('methodologies.syntaxHelp.argsAtLeast', { min: fn.minArgs });
  if (fn.maxArgs === fn.minArgs) return t('methodologies.syntaxHelp.argsExact', { count: fn.minArgs });

  return t('methodologies.syntaxHelp.argsRange', { min: fn.minArgs, max: fn.maxArgs });
}

/**
 * Перелік функцій діалекту — З СЕРВЕРА (`DialectCatalog`), той самий, що
 * підсвічує редактор. Вантажиться лише коли довідку відкрито.
 */
function FunctionList({ versionId }: { readonly versionId: number }): JSX.Element {
  const metadata = useQuery({
    queryKey: ['expressions', 'metadata', 'Methodology', versionId],
    queryFn: () => expressionMetadata('Methodology', { methodologyVersionId: versionId }),
    staleTime: 5 * 60 * 1000,
  });

  if (metadata.error !== null) {
    return <ErrorAlert error={metadata.error} onRetry={() => void metadata.refetch()} />;
  }

  if (metadata.data === undefined) {
    return <Skeleton height={60} />;
  }

  const functions = [...metadata.data.functions].sort((a, b) => a.name.localeCompare(b.name));

  if (functions.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        {t('methodologies.syntaxHelp.functionsEmpty')}
      </Text>
    );
  }

  return (
    <Group gap="xs" wrap="wrap" data-testid="syntax-help-functions">
      {functions.map((fn) => (
        <Group key={fn.name} gap="xs" wrap="nowrap" data-function={fn.name}>
          <Code>{`${fn.name}()`}</Code>
          <Text size="xs" c="dimmed">
            {arity(fn)}
          </Text>
          {fn.tier === 'Extension' && (
            <Badge size="xs" variant="default" title={t('methodologies.syntaxHelp.extensionHint')}>
              {t('methodologies.syntaxHelp.extension')}
            </Badge>
          )}
        </Group>
      ))}
    </Group>
  );
}

/**
 * Довідка синтаксису формули методології в діалозі формули.
 *
 * ⚠ Панель стоїть ПІД редактором і розгортається в потоці діалогу, а не
 * спливає поверх: довідку читають, коли пишуть, і закритий нею редактор
 * змусив би перемикатися туди-сюди. Закрита за замовчуванням (`KIT.md` §4,
 * прогресивне розкриття); кнопка — `aria-expanded`/`aria-controls`.
 *
 * ⚠ Структура груп — з макета редактора виразів (`docs/design/hybrid/screens-data.js`,
 * права панель «Reference»: References → Functions → Operators); ЗМІСТ — з
 * реалізації (`formulaSyntax.ts`), бо макет показує `^` і `sum()`, яких у діалекті
 * методологій немає.
 */
export function FormulaSyntaxHelp({ versionId }: { readonly versionId: number }): JSX.Element {
  const [open, setOpen] = useState(false);
  const bodyId = `${useId()}-syntax-help`;

  return (
    <Stack gap="xs">
      <Group>
        <Button
          size="xs"
          variant="subtle"
          aria-expanded={open}
          aria-controls={bodyId}
          data-testid="syntax-help-toggle"
          onClick={() => setOpen((value) => !value)}
        >
          {t('methodologies.syntaxHelp.toggle')}
        </Button>
      </Group>

      {/* ⚠ `hidden` — на звичайному `div`, а не на `Paper`: CSS Paper задає `display` і перекриває
          атрибут, тож закрита довідка лишалася видимою порожньою рамкою (e2e на стенді, 5/5). */}
      <div id={bodyId} hidden={!open} data-testid="syntax-help">
      <Paper withBorder p="sm">
        {open && (
          <Stack gap="md">
            <Text size="sm" c="dimmed">
              {t('methodologies.syntaxHelp.intro')}
            </Text>

            <Stack gap="xs">
              <GroupTitle>{t('methodologies.syntaxHelp.references')}</GroupTitle>
              <Row token="@Name">{t('methodologies.syntaxHelp.refArgument')}</Row>
              <Row token="CST.Name">{t('methodologies.syntaxHelp.refConstant')}</Row>
              <Row token="!Name">{t('methodologies.syntaxHelp.refFormula')}</Row>
              <Row token="HDR.Name">{t('methodologies.syntaxHelp.refHeader')}</Row>
              <Row token="[Period].Days">
                {t('methodologies.syntaxHelp.refPeriod', { properties: PeriodProperties.join(', ') })}
              </Row>
            </Stack>

            <Stack gap="xs">
              <GroupTitle>{t('methodologies.syntaxHelp.operators')}</GroupTitle>
              <Row token="+ - * / %">{t('methodologies.syntaxHelp.opArithmetic')}</Row>
              <Row token="= == <> != < <= > >=">{t('methodologies.syntaxHelp.opComparison')}</Row>
              <Row token="and or not && || !">{t('methodologies.syntaxHelp.opLogic')}</Row>
              <Row token="a ? b : c">{t('methodologies.syntaxHelp.opTernary')}</Row>
              <Row token="1.5 'text' true null">{t('methodologies.syntaxHelp.opLiterals')}</Row>
            </Stack>

            <Stack gap="xs">
              <GroupTitle>{t('methodologies.syntaxHelp.functions')}</GroupTitle>
              <Text size="sm" c="dimmed">
                {t('methodologies.syntaxHelp.caseSensitive')}
              </Text>
              <FunctionList versionId={versionId} />
            </Stack>

            <Stack gap="xs">
              <GroupTitle>{t('methodologies.syntaxHelp.notAllowed')}</GroupTitle>
              <Row token="^">{t('methodologies.syntaxHelp.noCaret')}</Row>
              <Row token="&">{t('methodologies.syntaxHelp.noAmpersand')}</Row>
              <Row token="Name">{t('methodologies.syntaxHelp.noBareName')}</Row>
              <Row token="[Sheet].[Row].[Column]">{t('methodologies.syntaxHelp.noCellRef')}</Row>
            </Stack>

            <Stack gap="xs">
              <GroupTitle>{t('methodologies.syntaxHelp.examples')}</GroupTitle>
              {SyntaxExamples.map((example) => (
                <Group key={example.id} gap="xs" wrap="wrap" align="baseline" data-example={example.id}>
                  <Code style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{example.code}</Code>
                  <Text size="sm" c="dimmed">
                    {exampleLabel(example)}
                  </Text>
                </Group>
              ))}
            </Stack>
          </Stack>
        )}
      </Paper>
      </div>
    </Stack>
  );
}
