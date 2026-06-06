// Crockford base32 alphabet — omits I, L, O, U to avoid visual confusion.
const CROCKFORD = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
const TIME_LENGTH = 10;
const RANDOM_LENGTH = 16;
const ULID_LENGTH = TIME_LENGTH + RANDOM_LENGTH;
const ULID_PATTERN = /^[0-9A-HJKMNP-TV-Z]{26}$/;

function encodeTime(ms: number): string {
  let remaining = ms;
  let encoded = '';
  for (let i = 0; i < TIME_LENGTH; i++) {
    encoded = CROCKFORD[remaining % 32] + encoded;
    remaining = Math.floor(remaining / 32);
  }
  return encoded;
}

function randomChar(): string {
  // ULID randomness — Math.random is sufficient for an idempotency key;
  // collision risk over a single user session is negligible.
  const index = Math.floor(Math.random() * 32);
  // Index can only reach 31 — assertion narrows the type for noUncheckedIndexedAccess.
  return CROCKFORD[index]!;
}

/**
 * Generates a ULID-shaped Crockford base32 string. Used as the Idempotency-Key
 * header on every mutating request so retries collapse to the same operation.
 */
export function generateIdempotencyKey(now: number = Date.now()): string {
  let random = '';
  for (let i = 0; i < RANDOM_LENGTH; i++) {
    random += randomChar();
  }
  return encodeTime(now) + random;
}

export function isUlidShape(value: string): boolean {
  return value.length === ULID_LENGTH && ULID_PATTERN.test(value);
}

/**
 * Truncates a long ID (like a payment_01H8X...W3F) for table display while
 * keeping the prefix recognizable.
 */
export function truncateMiddle(value: string, head: number = 10, tail: number = 4): string {
  if (value.length <= head + tail + 1) {
    return value;
  }
  return `${value.slice(0, head)}…${value.slice(-tail)}`;
}
