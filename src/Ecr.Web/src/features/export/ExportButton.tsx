import { useEffect, useRef, useState, type JSX } from 'react';
import { Anchor, Button, Divider, Group, Loader, SegmentedControl } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type { ExportRequest, JobStatus } from '@/api/types';
import { outcomeOf, pollInterval } from '@/features/workflow/jobFollow';
import { showApiError } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Що і за який період експортувати. */
export interface ExportButtonProps {
  /** Документ. */
  documentId: number;
  /** Період. */
  periodKey: number;
  /** Мова книги; береться з профілю. */
  language: string;
}

/**
 * Формат вивантаження (ФВ-4.2). Ті самі три значення, що приймає сервер
 * (`POST …/export` тіло `ExportRequest.format`); четвертого немає й додавати
 * нема звідки — перелік валідних значень визначає СЕРВЕР (422
 * `err.ECR-REQ-0422.exportFormatUnknown` на будь-яке інше), тому клієнт не
 * дублює цю перевірку — він лише не дає обрати нічого поза цими трьома.
 */
type ExportFormat = 'xlsx' | 'csv' | 'json';

/**
 * Підпис посилання на готовий файл — ЗА ФОРМАТОМ, у якому його будували.
 *
 * ⛔ `V-10`: посилання на ZIP-архів CSV і на JSON підписувалося «Download the
 * workbook» — людина чекала книгу Excel і отримувала архів.
 *
 * ⚠ Літерали, а не ключ, складений із формату: сторож
 * `EndpointCoverageTests` перевіряє в каталозі лише ключі-літерали.
 */
function exportReadyLabel(format: ExportFormat): string {
  switch (format) {
    case 'csv':
      return t('document.exportReadyCsv');
    case 'json':
      return t('document.exportReadyJson');
    default:
      return t('document.exportReady');
  }
}

/**
 * Підписи перемикача формату — самі значення, а не переклад лейблів, беруться
 * з каталогу (`t()`), щоб не заводити четвертий літерал в UI поруч із трьома
 * дозволеними значеннями контракту.
 */
function exportFormatOptions(): { value: ExportFormat; label: string }[] {
  return [
    { value: 'xlsx', label: t('document.exportFormatXlsx') },
    { value: 'csv', label: t('document.exportFormatCsv') },
    { value: 'json', label: t('document.exportFormatJson') },
  ];
}

/**
 * Експорт у `.xlsx` разом із **посиланням на готовий файл**.
 *
 * ⛔ Побудова повертає `202` з `jobId`, а не файл: книга на 500×60×12
 * будується довше за будь-який розумний таймаут проксі. Але до аудиту на
 * цьому все й закінчувалося — екран показував «поставлено в чергу як
 * `4f2c…`», і **забрати книгу було нічим**: адреса `GET /documents/{id}/export/{exportId}`
 * не мала в клієнті жодного споживача, а екран задач знає лише про `jobId`.
 * Користувач отримував GUID і не мав куди його ввести.
 *
 * ⚠ Ключ файлу приходить у повідомленні прогресу на 100 % — так його передає
 * `ExcelExportJob`. Тому тут не «показати jobId», а дочекатися завершення і
 * дати посилання.
 *
 * ⚠ Посилання — звичайне `<a>`, а не завантаження через `fetch`: автентифікація
 * на cookie, і навігація тим самим походженням несе її сама. Обгортка через
 * `fetch` + `blob:` дала б ту саму книгу вдвічі дорожче і зламала б «зберегти
 * як» у браузері.
 *
 * ⛔ Q-234: опитування задачі перевикористовує `jobFollow.ts` (`pollInterval`,
 * `outcomeOf`), а не власну копію правила. До цього тут була саме та копія, з
 * якої `jobFollow.ts` і виник (коментар там прямо посилався на ці рядки як на
 * зразок) — але сама кнопка лишилася на старій версії й не отримала фікс,
 * заради якого модуль узагалі виділили: `outcomeOf` розрізняє «немає відповіді»
 * і «відповідь — відмова» (`job.isError`, той самий `Q-156` — `GET /jobs/{id}`
 * вимагає `System.ViewHealth`). Стара умова `job.data?.state !== 'Failed'`
 * на відмові читання залишала `job.data` порожнім НАЗАВЖДИ: `building`
 * лишався `true`, `refetchInterval` бачив `undefined` замість `'Queued'` й
 * зупиняв опитування — кнопка крутилася «Формується…» вічно, без жодного
 * повідомлення, і побудований файл забрати було нічим. Не гіпотетично: та сама
 * пастка, від якої `outcomeOf` захищає «Перерахувати» на `PeriodsPage`, тут
 * стояла незахищеною поруч.
 */
export function ExportButton({
  documentId,
  periodKey,
  language,
}: ExportButtonProps): JSX.Element {
  const [jobId, setJobId] = useState<string | null>(null);

  // ⚠ Локальний стан, не адреса: вибір формату живе рівно доти, доки відкрита
  // ця кнопка (сам документ), і повторний вибір після перезавантаження
  // сторінки — прийнятна ціна за те, щоб не заводити ще один параметр у URL
  // заради перемикача, який мало хто чіпає частіше, ніж раз на сесію.
  const [format, setFormat] = useState<ExportFormat>('xlsx');

  // ⚠ Формат ЗАПУЩЕНОЇ побудови — окремо від перемикача: людина може
  // перемкнути формат, доки файл будується, а підпис посилання має
  // відповідати файлу, який вона отримає, а не поточному положенню перемикача.
  const [startedFormat, setStartedFormat] = useState<ExportFormat>('xlsx');

  const start = useMutation({
    mutationFn: () =>
      apiEnqueue(`/api/v1/documents/${documentId}/export`, {
        format,
        includeFormulas: true,
        includeStyles: true,
        language,
        periodKey,
      } satisfies ExportRequest),
    onSuccess: (job) => {
      setStartedFormat(format);
      setJobId(job.jobId);
    },
    onError: showApiError,
  });

  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(jobId ?? '')}`),
    enabled: jobId !== null,
    refetchInterval: (query) => pollInterval(query.state.data?.state),
    retry: false,
  });

  const outcome = jobId === null ? null : outcomeOf(job.data?.state, job.isError);
  const building = outcome === 'running';

  // ⚠ Повідомлення про відмову — ОДИН раз на задачу, а не на кожен рендер:
  // `job.data` не змінюється після кінцевого стану, і без захисту `ref`
  // тост показувався б повторно щоразу, коли компонент перемальовується з
  // будь-якої іншої причини (наприклад, зміна `periodKey` сусіднього select).
  const reported = useRef<string | null>(null);

  useEffect(() => {
    if (jobId === null || outcome === null || outcome === 'running') return;
    if (reported.current === jobId) return;

    reported.current = jobId;

    // ⛔ `unknown` (стан прочитати не вдалося, `Q-156`) навмисно без тосту:
    // причина — брак права на читання задачі, а не збій експорту, і показ
    // помилки тут звинуватив би експорт у тому, чого він не робив.
    /*
     * ⛔ `U-25`: результат — ТОСТОМ із посиланням, а не елементом у рядку
     * кнопок. Посилання «Download the workbook», вставлене в рядок поруч із
     * кнопкою, мало інший розмір і вигляд і переповнювало рядок: «Delete
     * document» переїжджав на другий рядок під поле періоду — найнебезпечніша
     * дія документа опинялася там, де її ніхто не чекає. Тост не займає
     * місця в рядку взагалі, тож стан експорту на розкладку не впливає.
     *
     * ⚠ `autoClose: false`: файл забирають тоді, коли людина повернулась до
     * вкладки, а не протягом чотирьох секунд. Закритий тост файл не губить —
     * те саме посилання лишається в «Мої задачі» (`JobFacts.JobResultLink`).
     *
     * ⚠ Адреса та сама, що й була (`a[href^="/api/v1/documents/{id}/export/"]`),
     * — на неї спирається `e2e/zz-walkthrough.spec.ts`; тост рендериться в
     * тому самому документі.
     */
    if (outcome === 'succeeded') {
      const exportKey = job.data?.message ?? '';
      if (exportKey.length > 0) {
        notifications.show({
          id: `export-ready-${jobId}`,
          color: 'statusSuccess',
          autoClose: false,
          message: (
            <Anchor
              size="sm"
              href={`/api/v1/documents/${documentId}/export/${encodeURIComponent(exportKey)}`}
              download
            >
              {exportReadyLabel(startedFormat)}
            </Anchor>
          ),
        });
      }
    }

    if (outcome === 'failed') {
      // ⛔ `error`, а не `message`: перше несе причину відмови
      // (`FinishAsync(..., errorMessage: ex.Message, ...)`), друге — останній
      // прогрес (`IJobProgress.ReportAsync`), який на відмові лишається тим,
      // яким був до неї, — часто порожнім або застарілим текстом «Виконується».
      notifications.show({
        color: 'statusError',
        message: job.data?.error ?? t('document.exportFailed'),
      });
    }
  }, [jobId, outcome, job.data?.error, job.data?.message, documentId, startedFormat]);

  return (
    /*
     * ⛔ `U-15`: експорт — ОДНА одиниця, видимо відокремлена від імпорту.
     * Раніше рядок читався «Validate · Import from Excel · Excel CSV JSON ·
     * Export»: перемикач формату стояв одразу за імпортом, без підпису, і
     * читався як налаштування імпорту, хоча керує експортом. Тепер кнопка
     * «Export» іде ПЕРШОЮ, формат — одразу за нею, обидва в спільній групі з
     * `role="group"` за вертикальним роздільником: поруч з «Import from
     * Excel» опиняється межа і слово «Export», а не голий перелік форматів.
     * Нового тексту не додано — роздільник і порядок
     * пояснюють належність, а ширина рядка не зростає.
     */
    <Group gap="xs" wrap="nowrap" role="group" aria-label={t('document.export')} data-testid="export-unit">
      {/* ⚠ Роздільник — перший елемент самої одиниці, а не сторінки: так він
          стоїть і там, де імпорту немає (оператор без `Document.Import`), і
          відокремлює експорт від того, що йде перед ним. */}
      <Divider orientation="vertical" />

      {/*
       * ⛔ `U-25`: кнопка в роботі зберігає ПІДПИС поруч зі спінером і НЕ
       * змінює ширини. `loading` Mantine ховав підпис, лишаючи сам спінер, а
       * зміна тексту «Export» → «Building...» міняла ширину — і заголовок
       * документа перестрибував на окремий рядок. Обидва варіанти підпису
       * лежать в ОДНІЙ комірці сітки (`gridArea: 1 / 1`), неактивний —
       * `visibility: hidden`: ширина кнопки = ширина довшого з двох у будь-
       * якій мові, і між станами вона не змінюється.
       */}
      <Button
        size="xs"
        variant="default"
        disabled={start.isPending || building}
        aria-busy={start.isPending || building}
        data-export-state={start.isPending || building ? 'running' : 'idle'}
        onClick={() => start.mutate()}
      >
        <span style={{ display: 'inline-grid' }}>
          <span
            aria-hidden={start.isPending || building}
            style={{ gridArea: '1 / 1', visibility: start.isPending || building ? 'hidden' : 'visible' }}
          >
            {t('document.export')}
          </span>
          <span
            aria-hidden={!(start.isPending || building)}
            data-testid="export-running-label"
            style={{
              gridArea: '1 / 1',
              display: 'inline-flex',
              alignItems: 'center',
              gap: 6,
              visibility: start.isPending || building ? 'visible' : 'hidden',
            }}
          >
            <Loader size={12} />
            {t('document.exportBuilding')}
          </span>
        </span>
      </Button>

      <SegmentedControl
        size="xs"
        aria-label={t('document.exportFormat')}
        value={format}
        onChange={(value) => setFormat(value as ExportFormat)}
        disabled={start.isPending || building}
        data={exportFormatOptions()}
      />
    </Group>
  );
}
