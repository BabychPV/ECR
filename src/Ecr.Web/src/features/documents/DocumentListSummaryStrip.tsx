import type { JSX } from 'react';
import { Group, Stack, Text, UnstyledButton } from '@mantine/core';
import { useDocumentListSummary, type DocumentStateFilter } from '@/features/documents/api';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';
import { statusKey, statusTone, toneFills, type StatusTone } from '@/shared/ui/StatusBadge';
import type { DocumentListFilters } from './documentListFilters';

export interface DocumentListSummaryStripProps {
  /** За який період зводити; `null` — період не обрано. */
  readonly periodKey: number | null;

  /**
   * Фільтр стану — той самий, що в `<DocumentListFilterBar>`.
   *
   * ⛔ Смуга не тримає власного «обраного»: і вона, і перелік фільтра читають
   * `state` з АДРЕСИ (`useDocumentListFilters`), тож розійтися їм нема де.
   */
  readonly filters: Pick<DocumentListFilters, 'state' | 'setState'>;
}

/** Лічильник смуги. `state` — значення фільтра; `null` — лічильник не фільтрує. */
interface Counter {
  readonly id: string;
  readonly label: string;
  readonly count: number;
  readonly tone: StatusTone | null;
  readonly state: DocumentStateFilter | null;
}

/**
 * Смуга лічильників над переліком документів (`BE-09`, `BE-09b`).
 *
 * Лічильник стану — кнопка-перемикач: клік ставить `state` в адресу так само,
 * як вибір у фільтрі, повторний клік на активному — знімає. «З проблемами» —
 * не стан аркуша, фільтра за ним на сервері немає, тож він лишається числом.
 *
 * ⚠ Активний позначено `aria-pressed`, напівжирним і підкресленим підписом —
 * тими самими НЕкольоровими каналами, що в `shared/ui/StatStrip` (`ФВ-14.18`).
 * Сам `StatStrip` не взято: він тримає ≤ 4 показники (`L4`), а тут їх до п'яти.
 *
 * ⚠ Підписи станів — ті самі рядки каталогу, що й у `<StatusBadge>`
 * (`status.sheet.*`): число над таблицею і бейдж у ній мусять називати стан
 * одним словом.
 *
 * ⛔ Без періоду, під час завантаження і при відмові смуги НЕМАЄ зовсім: нулі
 * на її місці читалися б як «документів немає». Без періоду вона ще й не може
 * фільтрувати — стан поза періодом не визначений (`D-93`), — тож перевірка
 * `periodKey` стоїть тут явно, а не лише в `enabled` запиту.
 */
export function DocumentListSummaryStrip({ periodKey, filters }: DocumentListSummaryStripProps): JSX.Element | null {
  const summary = useDocumentListSummary(periodKey);

  if (periodKey === null || summary.data === undefined) {
    return null;
  }

  const active = filters.state;

  /*
   * ⛔ Rejected — лише коли відхилені Є (рішення людини, 2026-09-19). У
   * спокійному стані смуга тримає чотири числа без кольору; «0 відхилено»
   * червоним привчало б не дивитися на червоне. Тон — той самий, яким
   * `<StatusBadge>` малює `sheet/Rejected`, з тієї ж таблиці, не власний.
   *
   * ⚠ Виняток — фільтр `Rejected` уже активний: тоді лічильник лишається (без
   * кольору, якщо нуль), інакше активний фільтр не мав би на смузі ні
   * позначки, ні кнопки, якою його зняти.
   */
  const rejected: readonly Counter[] =
    summary.data.rejected > 0 || active === 'Rejected'
      ? [
          {
            id: 'rejected',
            label: t(statusKey('sheet', 'Rejected')),
            count: summary.data.rejected,
            tone: summary.data.rejected > 0 ? statusTone('sheet', 'Rejected') : null,
            state: 'Rejected',
          },
        ]
      : [];

  const counters: readonly Counter[] = [
    { id: 'draft', label: t(statusKey('sheet', 'Draft')), count: summary.data.draft, tone: null, state: 'Draft' },
    {
      id: 'submitted',
      label: t(statusKey('sheet', 'Submitted')),
      count: summary.data.submitted,
      tone: null,
      state: 'Submitted',
    },
    ...rejected,
    { id: 'approved', label: t(statusKey('sheet', 'Approved')), count: summary.data.approved, tone: null, state: 'Approved' },
    { id: 'withIssues', label: t('documents.summaryWithIssues'), count: summary.data.withIssues, tone: null, state: null },
  ];

  return (
    <Group gap="xl" mb="md" role="group" aria-label={t('documents.summaryLabel')}>
      {counters.map((counter) => {
        const pressed = counter.state !== null && counter.state === active;

        const content = (
          <Stack gap="xs">
            <Text size="xl" fw={600} {...(counter.tone === null ? {} : { c: toneFills[counter.tone].text })}>
              {formatNumber(counter.count)}
            </Text>
            <Text size="xs" c="dimmed" {...(pressed ? ({ fw: 700, td: 'underline' } as const) : {})}>
              {counter.label}
            </Text>
          </Stack>
        );

        const marks = {
          'data-summary-counter': counter.id,
          'data-summary-tone': counter.tone ?? undefined,
          'data-summary-active': String(pressed),
        };

        return counter.state === null ? (
          <div key={counter.id} {...marks}>
            {content}
          </div>
        ) : (
          // Справжня `<button>`: у порядку табуляції, Enter/Space — рідні.
          <UnstyledButton
            key={counter.id}
            type="button"
            aria-pressed={pressed}
            onClick={() => {
              filters.setState(pressed ? null : counter.state);
            }}
            {...marks}
          >
            {content}
          </UnstyledButton>
        );
      })}
    </Group>
  );
}
