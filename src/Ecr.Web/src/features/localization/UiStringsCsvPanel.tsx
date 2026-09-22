import { useRef, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Modal, Select, Stack, Table, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  FallbackExportFileName,
  fetchUiStringExport,
  importUiStrings,
  isApplicable,
  rowErrorText,
  translationLanguages,
  type UiStringImportReport,
} from '@/features/localization/api';
import { useLanguages } from '@/shared/i18n/useLanguages';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import { can, useSession } from '@/shared/session/useSession';
import { t } from '@/shared/i18n';

/**
 * Обмін перекладом інтерфейсу через CSV (`BE-13` ч.2).
 *
 * ⛔ Перегляд перед записом — не крок майстра, який можна пропустити, а сам
 * механізм захисту, і причина та сама, що в імпорті аркуша
 * (`features/import/ImportPanel.tsx`): файл, застосований наосліп, непомітно
 * перезаписує чужу роботу. Тому перший виклик ЗАВЖДИ `dryRun=true`, а
 * `dryRun=false` надсилає лише кнопка, яка з'являється після звіту.
 *
 * ⛔ Помилки стрічок показані ТЕКСТОМ каталогу, а не `messageKey`. Сирий
 * `err.ECR-REQ-0422.uiStringDuplicateKey` у таблиці — це та сама `D-138`, що
 * колись показала `login.title` замість назви системи: виглядає технічно і
 * тому «так і задумано», а насправді людина не знає, що робити з файлом.
 *
 * ⛔ Без права `System.ManageLocalization` панелі немає зовсім. Сам маршрут
 * `/admin/ui-strings` уже під цим правом (`app/routes.ts`), і перевірка тут
 * виглядає зайвою — але вона не про маршрут, а про те, що сервер відповість
 * `403` обом діям. Обіцянка компонента має триматися там, де його змонтують,
 * а не там, де його змонтували вперше.
 */

/**
 * Написи, яких у каталозі (`09-seed.sql`) ще немає.
 *
 * ⛔ Літерали, а не `t()`, і з тієї ж причини, що `notificationCloseButtonProps`
 * у `shared/ui/notify.ts` та `moreLabel` у `shared/ui/PageHeader.tsx`: рядок
 * під цей напис у сіді заводить ІНШИЙ пакет, а `t()` на незаведений ключ дає
 * читалці `⟦…⟧` і робить червоним сторожа
 * `EndpointCoverageTests.Кожен_рядок_якого_просить_клієнт_є_в_каталозі`.
 *
 * ⚠ Зібрані в ОДНЕ місце навмисно: коли рядки з'являться в сіді, заміна на
 * `t()` — це правка одного об'єкта, а не полювання по розмітці.
 */
const Literal = {
  exportCsv: 'Export CSV',
  importCsv: 'Import CSV…',
  counts: 'added {added}, updated {updated}, unchanged {unchanged}',
  blockedHint: 'Nothing has been written: fix the rows listed below and pick the file again.',
  ready: 'The file is valid: nothing to fix.',
} as const;

/** Підставляє числа звіту в літерал підсумку. */
function countsText(report: UiStringImportReport): string {
  return Literal.counts
    .replace('{added}', String(report.added))
    .replace('{updated}', String(report.updated))
    .replace('{unchanged}', String(report.unchanged));
}

export function UiStringsCsvPanel(): JSX.Element | null {
  const session = useSession();
  const languages = useLanguages();
  const queryClient = useQueryClient();
  const picker = useRef<HTMLInputElement>(null);

  const targets = translationLanguages(languages.data);

  /*
   * ⚠ Обрана мова — ОДНА на обидві дії. Два окремих поля («мова експорту» і
   * «мова імпорту») дали б стан, у якому вивантажують казахську, а
   * завантажують російську, і помітити це можна лише після запису.
   */
  const [chosen, setChosen] = useState<string | null>(null);
  const lang = chosen ?? targets[0]?.code ?? null;

  /*
   * ⛔ Зберігається сам `File`, а не його вміст і не «ознака, що файл був»:
   * «Застосувати» має надіслати ТОЙ САМИЙ файл, який щойно показали у звіті.
   * Перечитування з поля дало б інший файл, якби його встигли підмінити.
   */
  const [file, setFile] = useState<File | null>(null);
  const [report, setReport] = useState<UiStringImportReport | null>(null);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<unknown>(null);

  const preview = useMutation({
    mutationFn: (input: { lang: string; file: File }) =>
      importUiStrings({ lang: input.lang, file: input.file, dryRun: true }),
    onSuccess: setReport,
    onError: showApiError,
  });

  const apply = useMutation({
    mutationFn: (input: { lang: string; file: File }) =>
      importUiStrings({ lang: input.lang, file: input.file, dryRun: false }),
    onSuccess: async (result) => {
      // Каталог редактора перечитується цілком: імпорт зачіпає ключі, яких
      // немає на екрані, і часткове оновлення показало б половину змін.
      await queryClient.invalidateQueries({ queryKey: ['ui-strings'] });

      setReport(null);
      setFile(null);
      showDone(t('uiStrings.saved', { revision: result.revision }));
    },
    onError: showApiError,
  });

  // ⛔ Після всіх хуків, а не перед ними: ранній вихід над `useState` міняв би
  // кількість хуків між рендерами, щойно відповідь `/me` доїде.
  if (!can(session.data, 'System.ManageLocalization')) return null;

  // ⚠ Мов, придатних для перекладу, може не бути взагалі (реєстр із самим
  // еталоном або відмова `GET /languages`). Елемент без даних не малюється
  // (`D15-06`): обидві дії однаково вимагають мови-цілі.
  if (lang === null) return null;

  const runExport = async (): Promise<void> => {
    if (exporting) return;
    setExporting(true);
    setExportError(null);

    try {
      const downloaded = await fetchUiStringExport(lang);
      saveBlob(downloaded.blob, downloaded.fileName ?? FallbackExportFileName);
    } catch (failure) {
      setExportError(failure);
    } finally {
      setExporting(false);
    }
  };

  const close = (): void => {
    setReport(null);
    setFile(null);
  };

  return (
    <>
      <Stack gap="xs" mb="md">
        <Group gap="xs" align="end" justify="flex-end">
          <Select
            size="xs"
            miw={160}
            label={t('uiStrings.language')}
            data={targets.map((language) => ({
              value: language.code,
              label: language.nameNative,
            }))}
            value={lang}
            onChange={(value) => setChosen(value)}
            allowDeselect={false}
            data-testid="ui-strings-csv-language"
          />

          <Button size="xs" variant="default" loading={exporting} onClick={() => void runExport()}>
            {Literal.exportCsv}
          </Button>

          {/* ⚠ Прихований `input[type=file]` за кнопкою: рідний елемент не
              піддається оформленню, але саме він дає діалог вибору файлу і
              працює з клавіатури (той самий прийом, що в `ImportPanel`). */}
          <input
            ref={picker}
            type="file"
            accept=".csv,text/csv"
            hidden
            aria-hidden="true"
            tabIndex={-1}
            data-testid="ui-strings-csv-file"
            onChange={(event) => {
              const picked = event.currentTarget.files?.[0];

              if (picked !== undefined) {
                setFile(picked);

                // ⛔ ЗАВЖДИ перевірка, ніколи запис: `dryRun` тут літерал у
                // виклику, а не прапорець зі стану. Стан, який можна виставити
                // не туди, і є той спосіб, яким «застосувати без перегляду»
                // повертається в код непомітно.
                preview.mutate({ lang, file: picked });
              }

              // Скидання дозволяє обрати ТОЙ САМИЙ файл удруге: без нього
              // повторний вибір не викликає `change`, і кнопка «не працює».
              event.currentTarget.value = '';
            }}
          />

          <Button
            size="xs"
            variant="default"
            loading={preview.isPending}
            onClick={() => picker.current?.click()}
          >
            {Literal.importCsv}
          </Button>
        </Group>

        {exportError !== null && <ErrorAlert error={exportError} />}
      </Stack>

      <Modal opened={report !== null} onClose={close} title={t('import.title')} size="xl">
        {report !== null && (
          <Stack gap="sm" data-testid="ui-strings-import-report">
            <Group gap="xs">
              <Badge variant="light">{countsText(report)}</Badge>
              {report.errors.length > 0 && (
                <Badge color="statusError">
                  {t('import.rejected', { count: report.errors.length })}
                </Badge>
              )}
            </Group>

            {report.errors.length > 0 ? (
              <>
                {/* ⛔ Причина названа ДО таблиці: людина має спершу дізнатися,
                    що не записано нічого, і лише потім читати перелік.
                    Зворотний порядок читається як «частину прийнято, ось
                    відхилені». */}
                <Alert color="statusWarning" title={t('import.blockedTitle')}>
                  {Literal.blockedHint}
                </Alert>

                <Table striped withTableBorder className="ecr-sticky-head">
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>{t('import.row')}</Table.Th>
                      <Table.Th>{t('uiStrings.key')}</Table.Th>
                      <Table.Th>{t('import.reason')}</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {report.errors.map((failure) => (
                      <Table.Tr key={`${String(failure.row)}:${failure.key}`}>
                        <Table.Td>{failure.row}</Table.Td>

                        {/* ⚠ `data-allow-dotted`: ключ каталогу тут — ДАНІ
                            звіту, а не неперекладений напис (`D-138`). */}
                        <Table.Td data-allow-dotted>
                          <Text size="xs">{failure.key}</Text>
                        </Table.Td>
                        <Table.Td>{rowErrorText(failure.messageKey, failure.key)}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </>
            ) : (
              <Text size="sm">{Literal.ready}</Text>
            )}

            <Group justify="flex-end">
              <Button variant="default" onClick={close}>
                {t('common.cancel')}
              </Button>
              <Button
                disabled={!isApplicable(report) || file === null}
                loading={apply.isPending}
                onClick={() => {
                  if (file !== null) apply.mutate({ lang, file });
                }}
              >
                {t('import.apply')}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </>
  );
}

/**
 * Віддає blob браузеру як завантаження.
 *
 * ⚠ URL звільняється одразу після кліку: інакше кожен експорт тримав би весь
 * файл у пам'яті вкладки до її закриття.
 */
function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);

  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.style.display = 'none';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}
