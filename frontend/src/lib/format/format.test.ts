import { describe, expect, it } from 'vitest';
import { formatMoney, formatMoneyParts } from './money';
import { formatIsoUtc, relativeFromNow } from './time';
import { generateIdempotencyKey, isUlidShape, truncateMiddle } from './id';

describe('formatMoney', () => {
  it('renders USD with two decimals', () => {
    expect(formatMoney(1_245_000, 'USD')).toBe('12,450.00 USD');
  });

  it('renders JPY with no decimals', () => {
    expect(formatMoney(1234, 'JPY')).toBe('1,234 JPY');
  });

  it('renders negative amounts with a sign', () => {
    expect(formatMoney(-89_900, 'USD')).toBe('-899.00 USD');
  });

  it('splits the amount into parts for layout', () => {
    expect(formatMoneyParts(89_900, 'usd')).toEqual({
      sign: '',
      whole: '899',
      fractional: '00',
      currency: 'USD',
    });
  });
});

describe('relativeFromNow', () => {
  const now = new Date('2026-06-06T12:00:00Z');

  it('returns "just now" within a minute', () => {
    expect(relativeFromNow('2026-06-06T11:59:30Z', now)).toBe('just now');
  });

  it('returns minutes within an hour', () => {
    expect(relativeFromNow('2026-06-06T11:58:00Z', now)).toBe('2m ago');
  });

  it('returns hours within a day', () => {
    expect(relativeFromNow('2026-06-06T09:00:00Z', now)).toBe('3h ago');
  });

  it('returns days beyond a day', () => {
    expect(relativeFromNow('2026-06-04T12:00:00Z', now)).toBe('2d ago');
  });

  it('returns dash on invalid input', () => {
    expect(relativeFromNow('not-a-date', now)).toBe('—');
  });
});

describe('formatIsoUtc', () => {
  it('renders an ISO string in human-readable UTC', () => {
    expect(formatIsoUtc('2026-06-06T11:48:11.000Z')).toBe('2026-06-06 11:48:11 UTC');
  });
});

describe('generateIdempotencyKey', () => {
  it('returns a 26-character Crockford base32 ULID-shaped string', () => {
    const key = generateIdempotencyKey();
    expect(key).toHaveLength(26);
    expect(isUlidShape(key)).toBe(true);
  });

  it('keeps the timestamp prefix monotonic across calls with the same now', () => {
    const a = generateIdempotencyKey(1_700_000_000_000);
    const b = generateIdempotencyKey(1_700_000_000_001);
    expect(a.slice(0, 10) <= b.slice(0, 10)).toBe(true);
  });

  it('rejects malformed inputs', () => {
    expect(isUlidShape('TOO_SHORT')).toBe(false);
    expect(isUlidShape('01234567890123456789012345')).toBe(true);
  });
});

describe('truncateMiddle', () => {
  it('returns the original when short enough', () => {
    expect(truncateMiddle('pay_short')).toBe('pay_short');
  });

  it('truncates long IDs with an ellipsis', () => {
    expect(truncateMiddle('pay_01H8XW3F000000000000000001')).toBe('pay_01H8XW…0001');
  });
});
