import { http, HttpResponse } from 'msw';
import { fixturePayments, fixturePayment } from '../fixtures/payments';

const BASE = 'http://localhost:8080';

export const handlers = [
  http.get(`${BASE}/v1/payments`, ({ request }) => {
    const url = new URL(request.url);
    const status = url.searchParams.get('status');
    const filtered = status
      ? fixturePayments.filter((p) => p.status.toLowerCase() === status.toLowerCase())
      : fixturePayments;
    return HttpResponse.json({
      data: filtered.map((p) => ({ ...p, events: [] })),
      nextCursor: null,
    });
  }),

  http.get(`${BASE}/v1/payments/:id`, ({ params }) => {
    const id = params.id as string;
    const payment = fixturePayments.find((p) => p.id === id);
    if (!payment) {
      return HttpResponse.json(
        {
          error: {
            code: 'payment_not_found',
            message: `Payment '${id}' was not found.`,
            details: null,
            traceId: null,
            requestId: 'req_test_404',
          },
        },
        { status: 404 },
      );
    }
    return HttpResponse.json(fixturePayment(payment.id));
  }),

  http.post(`${BASE}/v1/payments/:id/capture`, ({ params }) => {
    const id = params.id as string;
    const payment = fixturePayments.find((p) => p.id === id);
    if (!payment) {
      return HttpResponse.json(
        {
          error: {
            code: 'payment_not_found',
            message: `Payment '${id}' was not found.`,
            details: null,
            traceId: null,
            requestId: 'req_test_404',
          },
        },
        { status: 404 },
      );
    }
    return HttpResponse.json({
      ...fixturePayment(id),
      status: 'Captured' as const,
    });
  }),
];
