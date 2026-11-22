import { describe, expect, it } from 'vitest';
import { queueEtaOf } from '@/features/jobs/queueEta';

const now = Date.parse('2026-11-21T10:00:00Z');
const at = (ms: number): string => new Date(now + ms).toISOString();

describe('queueEtaOf', () => {
  it('без оцінки — null', () => {
    expect(queueEtaOf(null, now)).toBeNull();
    expect(queueEtaOf(undefined, now)).toBeNull();
    expect(queueEtaOf('not a date', now)).toBeNull();
  });
  it('у минулому або менше 30 с — «скоро»', () => {
    expect(queueEtaOf(at(-5000), now)).toEqual({ key: 'jobs.queueEtaSoon' });
    expect(queueEtaOf(at(29_000), now)).toEqual({ key: 'jobs.queueEtaSoon' });
  });
  it('хвилини округлюються вгору, мінімум 1', () => {
    expect(queueEtaOf(at(30_000), now)).toEqual({ key: 'jobs.queueEta', params: { minutes: 1 } });
    expect(queueEtaOf(at(61_000), now)).toEqual({ key: 'jobs.queueEta', params: { minutes: 2 } });
    expect(queueEtaOf(at(300_000), now)).toEqual({ key: 'jobs.queueEta', params: { minutes: 5 } });
  });
});
