import type { JSX, ReactNode } from 'react';
import { Box, Button, Card, Group, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { HealthReport } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { KeyValue } from '@/shared/ui/KeyValue';
import { showApiError, showDone } from '@/shared/ui/notify';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { hasText, t } from '@/shared/i18n';
import { fetchReadiness, type Readiness } from '@/pages/admin/healthReadiness';
import './healthPage.css';

type SystemFacts = components['schemas']['SystemFactsResponse'];
type HealthCheck = HealthReport['checks'][number];

/**
 * Команда для DBA → буфер обміну (`BE-18`, рішення `D15-12`).
 *
 * ⛔ Кнопка НЕ створює партицій: застосунок не виконує DDL (`D-66`). Вона лише
 * кладе в буфер текст, який сервер віддає як `text/plain`.
 *
 * ⚠ Голий `fetch`, а не `apiFetch`: той розбирає тіло як JSON і на простому
 * тексті впав би. Відмова однаково показується — і мережева, і буфера обміну
 * (без HTTPS або дозволу `navigator.clipboard` недоступний чи кидає).
 */
async function copyPartitionScript(): Promise<void> {
  try {
    const response = await fetch('/api/v1/health/partitions/script', { credentials: 'include' });

    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`.trim());

    await navigator.clipboard.writeText(await response.text());
    showDone(t('health.partitionScriptCopied'));
  } catch (error) {
    showApiError(error);
  }
}

/** Текст → буфер обміну; відмова буфера (без HTTPS чи дозволу) показується. */
async function copyText(text: string, done: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    showDone(done);
  } catch (error) {
    showApiError(error);
  }
}

interface DiagnosticsInput {
  readonly ready: Readiness | undefined;
  readonly db: HealthReport | undefined;
  readonly facts: SystemFacts | undefined;
  readonly checkedAt: string | null;
}

/**
 * «Copy diagnostics» (`UI-39`, приймання п. 3): лише несекретні факти.
 *
 * ⛔ Білий список, а не «усе, що прийшло»: опис і `data` кожної перевірки
 * беруться з `/health/ready` — того самого звіту, що й анонімний моніторинг
 * (`HealthResponse.WriteReadyAsync`: подробиці `db` вирізані, текст винятку
 * замінено нейтральною фразою, `A2-11`). Із `/health/db` — лише поля
 * `FieldLabelKeys` (редакція, режим, RCSI, файлові групи, партиції); відбитки
 * сертифікатів і решта службових ключів туди не потрапляють. Ні імені
 * користувача, ні рядка з'єднання, ні Correlation ID.
 *
 * ⚠ Підписи — сталі англійські ідентифікатори, а не переклад: текст іде в
 * тикет підтримки, і він мусить читатися однаково з будь-якої мови інтерфейсу.
 */
export function diagnostics({ ready, db, facts, checkedAt }: DiagnosticsInput): string {
  const lines = [`ECR diagnostics · ${checkedAt ?? 'not checked'}`];

  // ⚠ Стан — сирий член enum (`Healthy`/`Degraded`/`Unhealthy`), а не підпис
  // `StatusBadge`: це НЕ текст екрана (правило «стан як текст», `eslint.config.js`),
  // а діагностика для тикета, яку порівнюють між інсталяціями — так само, як
  // виняток `response.status` у тому самому правилі.
  if (ready !== undefined) {
    const { status: overall } = ready.report;

    lines.push(`Overall: ${overall}${ready.ready ? '' : ' (not ready)'}`);

    for (const { name, status, description } of ordered(ready.report.checks)) {
      lines.push(`${name}: ${status}${description === null ? '' : ` · ${description}`}`);
    }
  }

  const fields = db === undefined ? null : details(db);

  for (const key of Object.keys(FieldLabelKeys)) {
    if (fields !== null && key in fields) lines.push(`db.${key}: ${fieldValue(fields[key])}`);
  }

  if (typeof facts?.productVersion === 'string') {
    lines.push(`Version: ${facts.productVersion}`);
    lines.push(`Started: ${facts.startedAt}`);
    lines.push(`Environment: ${facts.environment}`);
    lines.push(
      `Notifications: ${facts.notificationTransport.isConfigured ? (facts.notificationTransport.kind ?? 'configured') : 'not configured'}`,
    );
    if (facts.logDirectory !== null) lines.push(`Log folder: ${facts.logDirectory}`);
  }

  return lines.join('\n');
}

/**
 * Порядок секцій — як у макеті (`screens-ops.js`, `/admin/health`): спершу
 * «Database / Background jobs / Data sources», далі решта перевірок у тому
 * порядку, в якому їх віддав сервер.
 */
const SectionOrder = ['db', 'jobs', 'sources'];

function ordered(checks: readonly HealthCheck[]): HealthCheck[] {
  const rank = (name: string): number => {
    const index = SectionOrder.indexOf(name);

    return index === -1 ? SectionOrder.length : index;
  };

  // ⚠ `sort` стабільний: невідомі перевірки лишаються в порядку сервера.
  return [...checks].sort((a, b) => rank(a.name) - rank(b.name));
}

/**
 * Перша причина для банера (`UI-39`, приймання п. 1): `Unhealthy` раніше за
 * `Degraded`, у межах тону — порядок секцій.
 */
function firstProblem(checks: readonly HealthCheck[]): HealthCheck | undefined {
  const sorted = ordered(checks);

  return (
    sorted.find((check) => check.status === 'Unhealthy') ??
    sorted.find((check) => check.status !== 'Healthy')
  );
}

/**
 * Операційний дашборд (`UI-39`, макет `docs/design/hybrid/screens-ops.js`,
 * `/admin/health`; `KIT.md` §3 «сторінка без таблиці»).
 *
 * Зверху — один банер «що не так» із першою причиною, нижче — секція на
 * перевірку: назва, `StatusBadge`, одне речення сервера і, для бази, перелік
 * подробиць. Дії шапки — «Check now» і «Copy diagnostics».
 *
 * ⚠ `/health/db` віддає **подробиці**: редакцію SQL Server, стан RCSI,
 * файлові групи, запас партицій і перелік того, що в цьому режимі недоступне
 * (АРХ-7 п. 5). Зведений «зелений» без цих полів був би обіцянкою, яку не
 * можна перевірити: система на Express формально жива і при цьому не вміє
 * половини того, на що розрахований регламент.
 */
export function HealthPage(): JSX.Element {
  // ⛔ `fetchReadiness`, а не `apiFetch`: `503` зі звітом — це стан системи,
  // а не відмова запиту (аудит U2, див. `healthReadiness.ts`).
  const ready = useQuery({
    queryKey: ['health', 'ready'],
    queryFn: fetchReadiness,
    refetchInterval: 30_000,
  });

  const db = useQuery({
    queryKey: ['health', 'db'],
    queryFn: () => apiFetch<HealthReport>('/health/db'),
    refetchInterval: 60_000,
  });

  // ⚠ Без `refetchInterval`: версія, час старту й середовище не міняються, доки
  // процес живий, а перезапуск сторінка й так побачить на наступному відкритті.
  const facts = useQuery({
    queryKey: ['health', 'facts'],
    queryFn: () => apiFetch<SystemFacts>('/api/v1/health/facts'),
  });

  const checkNow = async (): Promise<void> => {
    await Promise.all([ready.refetch(), db.refetch(), facts.refetch()]);
    showDone(t('health.checkedNow'));
  };

  const report = ready.data?.report;
  const problem = report === undefined ? undefined : firstProblem(report.checks);
  const checkedAt = ready.dataUpdatedAt > 0 ? new Date(ready.dataUpdatedAt).toISOString() : null;
  const dbCheck = report?.checks.find((check) => check.name === 'db');

  return (
    <>
      <PageHeader
        title={t('health.title')}
        meta={t('health.subtitle')}
        /* ⚠ Зведений статус і статус кожної перевірки — ОДИН словник
           (`health`), тож і рішення про колір одне, у наборі. Доти їх
           фарбувала власна `badgeColor` цієї сторінки — п'ята з п'яти
           розбіжних копій такого рішення (перелік — у шапці
           `StatusBadge.tsx`). */
        badge={report === undefined ? null : <StatusBadge kind="health" state={report.status} />}
        primary={{ label: t('health.checkNow'), onClick: () => void checkNow() }}
        secondary={[
          {
            label: t('health.copyDiagnostics'),
            onClick: () =>
              void copyText(
                diagnostics({ ready: ready.data, db: db.data, facts: facts.data, checkedAt }),
                t('health.diagnosticsCopied'),
              ),
          },
        ]}
      />

      <Stack gap="md" maw={1080}>
        {/* ⛔ Банер — лише коли справді щось не так: «Warnings appear only when
            there is something to do» (макет). `503` зі звітом додає рядок «не
            готова» (аудит U2) у той самий банер, а не другою смугою. */}
        {report !== undefined && (problem !== undefined || ready.data?.ready === false) && (
          <Banner
            tone={problem?.status === 'Unhealthy' || ready.data?.ready === false ? 'danger' : 'warning'}
            testId="health-banner"
            title={
              problem === undefined
                ? t('health.notReady')
                : t('health.banner.title', { check: checkLabel(problem.name) })
            }
            text={
              problem === undefined ? undefined : (
                <Stack gap="xs">
                  {problem.description !== null && <Text size="sm">{problem.description}</Text>}
                  {ready.data?.ready === false && (
                    <Text size="sm" data-health-not-ready="">
                      {t('health.notReady')}
                    </Text>
                  )}
                </Stack>
              )
            }
          />
        )}

        {/* ⚠ Секція бази стоїть ПОЗА межею `ready`: подробиці `/health/db` —
            окремий запит, і його відповідь видно навіть тоді, коли зведений
            звіт відмовив (`ФВ-B3 partial`). */}
        <HealthSection
          id="db"
          title={dbCheck === undefined ? t('health.database') : checkLabel('db')}
          check={dbCheck}
          extra={
            <Group>
              <Button variant="default" size="xs" onClick={() => void copyPartitionScript()}>
                {t('health.copyPartitionScript')}
              </Button>
            </Group>
          }
        >
          {/* ⚠ Обмеження режиму показуються переліком, а не ховаються: саме за
              ними видно, чому вночі не працює архівація або чому немає запасу
              партицій. */}
          <AsyncBoundary<HealthReport>
            isPending={db.isPending}
            error={db.error}
            data={db.data}
            isEmpty={(dbReport) => details(dbReport) === null}
            emptyTitle={t('health.noDbDetails')}
            skeleton="table"
            onRetry={() => void db.refetch()}
          >
            {(dbReport) => (
              <Box className="ecr-health-kv">
                <KeyValue
                  items={Object.entries(details(dbReport) ?? {}).map(([key, value]) => ({
                  label: fieldLabel(key),
                    value: fieldValue(value),
                  }))}
                />
              </Box>
            )}
          </AsyncBoundary>
        </HealthSection>

        {/*
         * ⛔ Через `<AsyncBoundary>`, а не через `?? {}`. Саме `?? {}` і був
         * `A7-04`: невдалий запит давав порожній дашборд, а порожній дашборд і
         * здорова система виглядали однаково. Тут порожньо ≠ помилка (`ФВ-14.22`).
         */}
        <AsyncBoundary<HealthReport>
          isPending={ready.isPending}
          error={ready.error}
          data={report}
          isEmpty={(value) => value.checks.length === 0}
          emptyTitle={t('health.noChecks')}
          emptyHint={t('health.noChecksHint')}
          onRetry={() => void ready.refetch()}
        >
          {(value) => (
            <Stack gap="md">
              {ordered(value.checks)
                .filter((check) => check.name !== 'db')
                .map((check) => (
                  /* ⛔ Заголовок — `checkLabel`, а не `{check.name}` (`U-14`):
                     `db`, `jobs`, `sources` — внутрішні ідентифікатори з
                     `Program.cs` (`AddCheck<…>("db", …)`). */
                  <HealthSection
                    key={check.name}
                    id={check.name}
                    title={checkLabel(check.name)}
                    check={check}
                  />
                ))}
            </Stack>
          )}
        </AsyncBoundary>

        {/* ⚠ Довідкові факти, не вміст екрана: доки їх немає (ще вантажаться,
            відмова, відповідь не тієї форми) — секція не малюється взагалі
            (`D15-06`), а про справжню біду вже кажуть дві межі поруч. */}
        {typeof facts.data?.productVersion === 'string' && (
          <Card withBorder component="section" aria-labelledby="health-facts-h" data-health-facts="">
            <Stack gap="sm">
              <Title order={2} size="h4" fz="md" id="health-facts-h">
                {t('health.facts')}
              </Title>
              <Box className="ecr-health-kv">
                <KeyValue
                  items={[
                    ...factItems(facts.data),
                    {
                      label: t('health.checkedAt'),
                      value: checkedAt === null ? null : <Timestamp value={checkedAt} />,
                    },
                  ]}
                />
              </Box>
            </Stack>
          </Card>
        )}
      </Stack>
    </>
  );
}

interface HealthSectionProps {
  readonly id: string;
  readonly title: string;
  readonly check: HealthCheck | undefined;
  readonly children?: ReactNode;
  readonly extra?: ReactNode;
}

/**
 * Секція перевірки (`screens-ops.js`, `sec(…)`): заголовок, `StatusBadge`
 * (тихий для `Healthy`), час відповіді праворуч, одне речення людською мовою.
 *
 * ⚠ Речення — опис САМОЇ перевірки з сервера (рядок каталогу, `health.*` у
 * `09-seed.sql`), а не вигадане клієнтом: клієнт не знає, чому `Degraded`.
 */
function HealthSection({ id, title, check, children, extra }: HealthSectionProps): JSX.Element {
  const headingId = `health-${id}-h`;

  return (
    <Card withBorder component="section" aria-labelledby={headingId} data-health-section={id}>
      <Stack gap="sm">
        <Group gap="sm" wrap="wrap">
          {/* ⚠ `order={2}` під h1 сторінки (axe `heading-order`, прохід a11y
              batch-4: було h1 → h4); `size="h4"` тримає вигляд макета. */}
          <Title order={2} size="h4" fz="md" id={headingId}>
            {title}
          </Title>
          {check !== undefined && (
            <StatusBadge kind="health" state={check.status} quiet={check.status === 'Healthy'} />
          )}
          {check !== undefined && check.durationMs > 0 && (
            <Text size="xs" c="dimmed" ff="monospace" ml="auto">
              {t('health.durationMs', { ms: Math.max(1, Math.round(check.durationMs)) })}
            </Text>
          )}
        </Group>
        {check?.description != null && <Text>{check.description}</Text>}
        {children}
        {extra}
      </Stack>
    </Card>
  );
}

/**
 * Факти про процес у вигляді пар для `KeyValue`.
 *
 * ⛔ «Не налаштовано» — це ТЕКСТ, а не пропущений рядок: відсутній транспорт
 * означає, що сповіщення накопичуються в черзі й нікуди не йдуть, і саме це
 * адміністратор має прочитати. А от «налаштовано» без виду транспорту рядка не
 * дає — називати нема чого (пару без значення `KeyValue` не малює).
 */
function factItems(facts: SystemFacts): { label: string; value: ReactNode }[] {
  const transport = facts.notificationTransport;

  return [
    { label: t('health.facts.productVersion'), value: facts.productVersion },
    /*
     * ⛔ `Timestamp`, а не голий `formatDateTime`: це було ЄДИНЕ місце в
     * застосунку, де момент уже форматувався — і саме тому єдине, де точне
     * значення справді ВТРАЧАЛОСЯ. «Sep 19, 2026, 6:51 PM» у довідці про
     * систему годиться, доки адміністратор просто дивиться; щойно він звіряє
     * час старту з журналом чи з тикетом, округлена до хвилини форма стає
     * непридатною. Тепер точний рядок лишається в `dateTime`/`title`.
     *
     * ⚠ `SystemFactsResponse.startedAt` НЕ nullable (`schema.d.ts:12390`),
     * тож прочерк тут не з'явиться — а якби з'явився, він порушив би
     * `D15-06`, який `KeyValue` виконує ВІДСУТНІСТЮ рядка. Це справжнє
     * протиріччя між двома компонентами набору: `Timestamp` за замовчуванням
     * малює тире, `KeyValue` тире не терпить. Тут воно не виникає — і це
     * названо, щоб наступний, хто покладе `Timestamp` у `KeyValue` з
     * nullable-полем, побачив пастку до того, як у неї впаде.
     */
    { label: t('health.facts.startedAt'), value: <Timestamp value={facts.startedAt} /> },
    { label: t('health.facts.environment'), value: facts.environment },
    {
      label: t('health.facts.notificationTransport'),
      value: transport.isConfigured ? transport.kind : t('health.facts.transportNotConfigured'),
    },
    // `null` — файл журналу не пишеться; пару без значення `KeyValue` не малює.
    { label: t('health.facts.logDirectory'), value: facts.logDirectory },
  ];
}

/**
 * Подробиці перевірки бази.
 *
 * ⚠ Пошук за іменем `db`, а не за позицією: `/health/db` сьогодні містить одну
 * перевірку, але позиційне звернення розсипалося б мовчки від першої ж другої.
 */
function details(report: HealthReport): Record<string, unknown> | null {
  const check = report.checks.find((candidate) => candidate.name === 'db');

  return check === undefined || Object.keys(check.data).length === 0 ? null : check.data;
}

/**
 * Технічне ім'я поля `/health/db` → ключ каталогу з людським підписом.
 *
 * ⛔ Аудит-пас 8, lane6, п.7: до фіксу рядок панелі показував буквально
 * `edition`, `effectiveMode`, `rcsi` тощо (`DatabaseHealthCheck.cs` — сталий
 * camelCase-словник) — не текст із каталогу з іншою мовою, а взагалі не
 * підпис. Дев'ять полів фіксовані контрактом `/health/db`, тому мапа тут, а
 * не вгадування: невідоме поле показує сам ключ (той самий принцип запасного
 * варіанту, що й `reasonOf` у `permissions.ts` — краще показати ім'я поля,
 * ніж вигадати підпис).
 */
const FieldLabelKeys: Record<string, string> = {
  edition: 'health.database.edition',
  effectiveMode: 'health.database.effectiveMode',
  majorVersion: 'health.database.majorVersion',
  rcsi: 'health.database.rcsi',
  archiveBatchSize: 'health.database.archiveBatchSize',
  filegroups: 'health.database.filegroups',
  missingFilegroups: 'health.database.missingFilegroups',
  partitionsAhead: 'health.database.partitionsAhead',
  limitations: 'health.database.limitations',
};

function fieldLabel(key: string): string {
  const translationKey = FieldLabelKeys[key];

  return translationKey === undefined ? key : t(translationKey);
}

/**
 * Людська назва перевірки стану (`U-14`).
 *
 * ⛔ Перевірки реєструються іменами `db`, `jobs`, `sources` (`Program.cs`), і
 * саме вони стояли заголовками карток — внутрішній ідентифікатор над реченням,
 * написаним для людини. Той самий клас, що #437/#438/#440/#441: значення
 * сервера не є текстом інтерфейсу й не перекладається (`D-95`).
 *
 * ⛔ Запасний варіант — САМ ІДЕНТИФІКАТОР, а не `⟦health.check.…⟧`: набір
 * перевірок задає сервер (`AddCheck<…>`), і четверта перевірка з'явиться в
 * звіті раніше, ніж рядок під неї в `09-seed.sql`. Позначений ключ на місці
 * зрозумілого `smtp` був би погіршенням, а не сигналом — той самий аргумент,
 * що у `permissionLabel.ts`.
 */
export function checkLabel(name: string): string {
  const key = `health.check.${name}`;

  return hasText(key) ? t(key) : name;
}

/**
 * Знак «значення є, і воно порожнє» (UI-прохід, F7).
 *
 * ⛔ Порожня клітинка читається ДВОЯКО: «відсутніх файлових груп немає» і «цей
 * рядок не завантажився». Це той самий клас, що `A7-04` вище на цій же
 * сторінці, лише на один рядок дрібніший: порожнеча ≠ помилка мусить бути
 * видно, а не додумуватись (`ФВ-14.22`).
 *
 * ⚠ Тире, а не `t('…')`: рядки цього застосунку йдуть із серверного каталогу
 * (`09-seed.sql`), ключа під «немає» там немає, а голий `t()` без рядка показав
 * би `⟦…⟧` — тобто замінив би одну незрозумілу клітинку на іншу. Той самий
 * аргумент, що в `passwordToggleProps` (`pages/LoginPage.tsx`). Знак
 * нейтральний до мови, тож заміна його рядком каталогу пізніше нічого тут не
 * перебудовує.
 */
const EmptyValue = '—';

/**
 * Значення поля `/health/db` у вигляді, придатному для клітинки.
 *
 * ⛔ Сирий `String(value)` і був дефектом: `missingFilegroups` і `limitations`
 * — це СПИСКИ (`DatabaseHealthCheck.cs`, `data["missingFilegroups"] = missing`),
 * а `String([])` — порожній рядок. Здорова система (жодної відсутньої групи,
 * жодного обмеження режиму) малювала два порожні рядки серед восьми
 * заповнених. Непорожній список він же зліплював без пробілів (`A,B`).
 */
function fieldValue(value: unknown): string {
  if (Array.isArray(value)) {
    return value.length === 0 ? EmptyValue : value.map(String).join(', ');
  }

  if (value === null || value === undefined) return EmptyValue;

  const text = String(value);

  return text.trim().length === 0 ? EmptyValue : text;
}

/*
 * ✎ Тут стояла `badgeColor(status)` — власна трійка кольорів цієї сторінки.
 * Її рішення не втрачене: «три стани, а не два; `Degraded` — це не помилка,
 * система працює, але чогось у ній бракує, і червоний навчив би оператора не
 * дивитися на червоне» — воно перенесене в `statusTable.health`
 * (`shared/ui/StatusBadge.tsx`) разом із самим поясненням. Різниця в тому, що
 * тепер воно ОДНЕ на застосунок, а не п'яте з п'яти копій, які вже встигли
 * розійтися між сторінками.
 */
