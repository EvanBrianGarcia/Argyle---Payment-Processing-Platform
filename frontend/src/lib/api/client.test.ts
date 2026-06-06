import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/server-export';
import { ApiError, paymentsApi } from './client';
import { isUlidShape } from '../format/id';

const BASE = 'http://localhost:8080';

describe('paymentsApi.list', () => {
  it('returns the parsed list response', async () => {
    const response = await paymentsApi.list({ status: 'Failed' });
    expect(response.data.every((p) => p.status === 'Failed')).toBe(true);
    expect(response.nextCursor).toBeNull();
  });

  it('encodes status filter into the query string', async () => {
    let observedSearch: string | null = null;
    server.use(
      http.get(`${BASE}/v1/payments`, ({ request }) => {
        observedSearch = new URL(request.url).search;
        return HttpResponse.json({ data: [], nextCursor: null });
      }),
    );
    await paymentsApi.list({ status: 'Settled', limit: 10 });
    expect(observedSearch).toContain('status=Settled');
    expect(observedSearch).toContain('limit=10');
  });
});

describe('paymentsApi.create', () => {
  it('sends a bearer token and ULID-shaped idempotency key on POST', async () => {
    let authHeader: string | null = null;
    let idempotencyKey: string | null = null;
    server.use(
      http.post(`${BASE}/v1/payments`, ({ request }) => {
        authHeader = request.headers.get('Authorization');
        idempotencyKey = request.headers.get('Idempotency-Key');
        return HttpResponse.json({
          id: 'pay_test',
          amountMinor: 100,
          currency: 'USD',
          status: 'Pending',
          customerReference: null,
          metadata: {},
          createdAt: '2026-06-06T12:00:00Z',
          updatedAt: '2026-06-06T12:00:00Z',
          events: [],
        }, { status: 201 });
      }),
    );
    await paymentsApi.create({ amountMinor: 100, currency: 'USD', cardToken: 'tok_test' });
    expect(authHeader).toMatch(/^Bearer /);
    expect(idempotencyKey).not.toBeNull();
    expect(isUlidShape(idempotencyKey!)).toBe(true);
  });

  it('reuses a caller-provided idempotency key', async () => {
    let captured: string | null = null;
    server.use(
      http.post(`${BASE}/v1/payments`, ({ request }) => {
        captured = request.headers.get('Idempotency-Key');
        return HttpResponse.json({
          id: 'pay_test',
          amountMinor: 100,
          currency: 'USD',
          status: 'Pending',
          customerReference: null,
          metadata: {},
          createdAt: '2026-06-06T12:00:00Z',
          updatedAt: '2026-06-06T12:00:00Z',
          events: [],
        }, { status: 201 });
      }),
    );
    await paymentsApi.create(
      { amountMinor: 100, currency: 'USD', cardToken: 'tok_test' },
      'CALLER_KEY_42',
    );
    expect(captured).toBe('CALLER_KEY_42');
  });
});

describe('ApiError', () => {
  it('parses an error envelope on non-2xx response', async () => {
    server.use(
      http.get(`${BASE}/v1/payments/pay_missing`, () =>
        HttpResponse.json(
          {
            error: {
              code: 'payment_not_found',
              message: "Payment 'pay_missing' was not found.",
              details: null,
              traceId: 'trace_abc',
              requestId: 'req_xyz',
            },
          },
          { status: 404 },
        ),
      ),
    );

    await expect(paymentsApi.get('pay_missing')).rejects.toMatchObject({
      name: 'ApiError',
      status: 404,
      code: 'payment_not_found',
      requestId: 'req_xyz',
      traceId: 'trace_abc',
    });
  });

  it('falls back to unknown_error when the body is not an envelope', async () => {
    server.use(
      http.get(`${BASE}/v1/payments/pay_weird`, () =>
        new HttpResponse('boom', { status: 500 }),
      ),
    );

    try {
      await paymentsApi.get('pay_weird');
      expect.unreachable();
    } catch (err) {
      expect(err).toBeInstanceOf(ApiError);
      expect((err as ApiError).status).toBe(500);
      expect((err as ApiError).code).toBe('unknown_error');
    }
  });
});
