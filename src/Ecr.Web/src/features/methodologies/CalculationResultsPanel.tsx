import { useEffect, useState, type JSX } from 'react';
import { Link, useInRouterContext } from 'react-router-dom';
import { notifications } from '@mantine/notifications';
import { Alert, Anchor, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { CalculationResultDto, RegistryDefDto } from '@/api/types';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import './calcResults.css';
import { useCalculationIssues } from '@/features/calculationIssues/useCalculationIssues';
import { UnmatchedRowsBannerSelector } from '@/features/documents/unmatchedRowsModel';
import { resultsHref } from '@/features/calcResults/DocumentResultsLinks';
import { calculationResults } from './api';
import { RecalculateHintId, calculationResultsKey } from './calculationResultsKey';

/** Пам'ять вибору «блок згорнуто/розгорнуто» (глобально, як `ecr.tableNavigator.hidden`): `1` згорнуто, `0` розгорнуто. */
export const ResultsCollapsedKey = 'ecr.calcResults.collapsed';
export const ResultsBodyId = 'ecr-calc-results-body';

function readCollapsed(): boolean | null {
  try {
    const value = window.localStorage.getItem(ResultsCollapsedKey);
    return value === '1' ? true : value === '0' ? false : null;
  } catch {
    return null;
  }
}

function writeCollapsed(collapsed: boolean): void {
  try {
    window.localStorage.setItem(ResultsCollapsedKey, collapsed ? '1' : '0');
  } catch {
    // Сховище недоступне — вибір просто не запам'ятається.
  }
}

/** Шеврон контуром (без бібліотеки іконок у чанку). */
function Chevron({ path }: { readonly path: string }): JSX.Element {
  return (
    <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.75} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
      <path d={path} />
    </svg>
  );
}

/**
 * Коди довідників, змінених після прогону (RT-25), без повторів і впорядковані.
 *
 * ⚠ Сервер кладе той самий перелік у кожен рядок (свіжість — на документ, не на число), тож
 * без зведення банер повторив би кожен довідник стільки разів, скільки чисел у панелі.
 */
export function changedRegistriesOf(list: readonly CalculationResultDto[]): string[] {
  return [...new Set(list.flatMap((result) => result.changedRegistries ?? []))].sort();
}

/**
 * Назва довідника для банера застарілості: назва мовою користувача з каталогу довідників, а код —
 * лише запасний варіант (каталог ще їде, недоступний без права або довідника там немає).
 */
export function registryDisplayName(code: string, registries: readonly RegistryDefDto[] | undefined): string {
  const name = localized(registries?.find((registry) => registry.code === code)?.nameL10n);
  return name === '' ? code : name;
}

/**
 * Стан чисел методологій документа за цей період під аркушами: скільки їх, чи застаріли, які рядки розрахунок не взяв, —
 * і перехід до огляду «Calculation results — усі місяці».
 *
 * ⛔ Окрема панель, а не колонка в сітці, і це `D-69`: результат методології **не** потрапляє в `doc.CellValue`.
 *
 * ✎ 12.11 (живий прохід координатора): тут стояв перелік чисел місяця з ГОЛИМ ідентифікатором екземпляра таблиці
 * (56724…) у колонці «Table» і числами всіх аркушів упереміш. Огляд «усі місяці» (`/documents/:id/results`) показує
 * ті самі числа з назвами аркушів і таблиць, по таблицях, з місяцем цього документа серед дванадцяти, — тож перелік-дубль
 * прибрано. Лишилося те, чого огляд не дає тут, біля сітки: лічильник, банер застарілості з назвами довідників (RT-25),
 * рядки без розрахунку з переходом до банера над сіткою.
 *
 * ⚠ Стан — по числах АКТУАЛЬНОГО прогону, не останнього за часом.
 */
export function CalculationResultsPanel({
  documentId,
  periodKey,
}: {
  readonly documentId: number;
  readonly periodKey: number;
}): JSX.Element {
  // ✎ ui-sheet-bottom (рішення людини 28.10): блок ЗАВЖДИ згорнуто за замовчуванням, навіть коли числа є;
  // явний вибір людини пам'ятається.
  const [choice, setChoice] = useState<boolean | null>(readCollapsed);
  const collapsed = choice ?? true;
  // Лічильник у заголовку — лише після першого розгортання (рішення координатора): у згорнутому стані заголовок
  // без числа (навіть якщо дані вже є в кеші, наприклад після перерахунку чи імпорту).
  const [opened, setOpened] = useState(!collapsed);

  // ⛔ F-02 (четвертий раунд UX): ключ — ПІД префіксом `['document', id, period]`. Саме його інвалідує завершений
  // перерахунок (`SheetActions`) і кожна зміна робочого процесу.
  //
  // ✎ ui-sheet-bottom: запит ЛІНИВИЙ — лише після розгортання. На Land до 174 тис. результатів на (документ,
  // період), і вантажити їх при відкритті документа заради лічильника не можна; prefetch немає.
  const results = useQuery({
    queryKey: calculationResultsKey(documentId, periodKey),
    queryFn: () => calculationResults(documentId, periodKey),
    enabled: !collapsed,
  });
  const count = results.data?.length;

  // Правда про застарілість — на сервері: щойно свіжі числа дійшли, тост-підказка імпорту
  // («Перерахуйте») втрачає сенс і знімається, а не висить до перезавантаження.
  const fresh =
    results.data !== undefined && !results.data.some((r) => r.isStale) && changedRegistriesOf(results.data).length === 0;
  useEffect(() => {
    if (fresh) notifications.hide(RecalculateHintId);
  }, [fresh]);

  // ⚠ Каталог довідників — лише коли банеру є кого називати, і під спільним ключем переліку
  // (його вже читають сітка й шапка документа), щоб не множити запитів. Відмова (немає
  // `Registry.View`) — не помилка панелі: банер покаже код.
  const changedCodes = changedRegistriesOf(results.data ?? []);
  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
    enabled: changedCodes.length > 0,
    retry: false,
  });

  // ✎ 10.11 (doc 95, «Calculation results · none» без причини): рядки, які розрахунок не взяв (без правила або
  // відхилені правилом категорії), — той самий запит і ключ, що й у банера над сіткою (`useUnmatchedRows`), тож
  // другого звернення немає. Рядок рахується раз, хоч би скільки методологій його відхилили.
  const issues = useCalculationIssues(documentId, periodKey, null);
  const notCalculated = new Set(
    (issues.data?.items ?? [])
      .filter((issue) => (issue.sheetCopyId ?? null) === null)
      .map((issue) => `${issue.tableCode}\u001f${issue.rowKey}`),
  ).size;

  // Огляд року з місяцем цього документа (`?month=`). ⚠ Поза роутером (ізольовані тести панелі) — звичайне посилання.
  const href = resultsHref(documentId, periodKey, true);
  const inRouter = useInRouterContext();
  const linkTitle = t('calcResults.allMonthsHint', { year: Math.floor(periodKey / 100) });

  return (
    <Stack gap="xs">
      <button
        type="button"
        className="ecr-results-toggle"
        aria-expanded={!collapsed}
        aria-controls={ResultsBodyId}
        data-testid="calc-results-toggle"
        onClick={() => {
          writeCollapsed(!collapsed);
          setChoice(!collapsed);
          if (collapsed) setOpened(true);
        }}
      >
        <Chevron path={collapsed ? 'M9 6l6 6-6 6' : 'M6 9l6 6 6-6'} />
        <Text component="span" fw={600}>
          {t('documents.calculationResults')}
        </Text>
        {opened && count !== undefined && (
          <Text component="span" size="sm" c="dimmed" data-testid="calc-results-count">
            {`· ${count === 0 ? t('documents.calculationResultsNone') : String(count)}`}
          </Text>
        )}
        {notCalculated > 0 && (
          <Text component="span" size="sm" c="dimmed" data-testid="calc-results-not-calculated-count">
            {`· ${t('documents.calculationResultsNotCalculated', { count: String(notCalculated) })}`}
          </Text>
        )}
      </button>

      {!collapsed && notCalculated > 0 && (
        <Text size="sm" data-testid="calc-results-not-calculated">
          {t('documents.calculationResultsNotCalculatedHint', { count: String(notCalculated) })}{' '}
          <Anchor
            component="button"
            type="button"
            size="sm"
            data-testid="calc-results-not-calculated-link"
            onClick={() => {
              // Банер «рядки не взято в розрахунок» над сіткою: там кожен рядок — перехід до комірки, яку треба заповнити.
              const banner = document.querySelector<HTMLElement>(UnmatchedRowsBannerSelector);
              banner?.scrollIntoView({ block: 'nearest' });
              banner?.querySelector<HTMLButtonElement>('[aria-controls]')?.focus();
            }}
          >
            {t('documents.calculationResultsNotCalculatedShow')}
          </Anchor>
        </Text>
      )}

      <div id={ResultsBodyId} hidden={collapsed} aria-busy={!collapsed && results.isPending ? true : undefined}>
      {!collapsed && (
      <AsyncBoundary<CalculationResultDto[]>
        isPending={results.isPending}
        error={results.error}
        data={results.data}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('documents.noCalculationResults')}
        emptyHint={t('documents.noCalculationResultsHint')}
        skeleton="table"
        onRetry={() => void results.refetch()}
      >
        {(list) => (
          <Stack gap="xs">
            {/*
              ⛔ F-05 / RT-25: входи або довідник, який читає методологія, змінилися після прогону — числа вже не
              відповідають даним. Без назви довідника людина шукала б зміну в документі, якої там немає.
            */}
            {(list.some((result) => result.isStale) || changedRegistriesOf(list).length > 0) && (
              <Alert color="statusWarning" data-results-stale="" title={t('documents.calculationResultsStale')}>
                {t('documents.calculationResultsStaleHint')}
                {changedRegistriesOf(list).map((code) => (
                  <Text key={code} size="sm" mt="xs" data-results-stale-registry={code}>
                    {t('calculation.staleRegistry', { name: registryDisplayName(code, registries.data) })}
                  </Text>
                ))}
              </Alert>
            )}

            <Text size="sm">
              {inRouter ? (
                <Anchor component={Link} to={href} size="sm" title={linkTitle} data-testid="calc-results-open-year">
                  {t('calcResults.allMonths')}
                </Anchor>
              ) : (
                <Anchor href={href} size="sm" title={linkTitle} data-testid="calc-results-open-year">
                  {t('calcResults.allMonths')}
                </Anchor>
              )}
            </Text>
          </Stack>
        )}
      </AsyncBoundary>
      )}
      </div>
    </Stack>
  );
}
