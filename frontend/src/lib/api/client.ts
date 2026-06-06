import { env } from '../env';
import { generateIdempotencyKey } from '../format/id';
import type {
  CapturePaymentRequest,
  CreatePaymentRequest,
  ErrorEnvelopeBody,
  ListPaymentsQuery,
  Payment,
  PaymentListResponse,
  RefundPaymentRequest,
} from './types';

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly requestId: string | null;
  readonly traceId: string | null;
  readonly details: unknown;

  constructor(status: number, body: ErrorEnvelopeBody) {
    super(body.error.message);
    this.name = 'ApiError';
    this.status = status;
    this.code = body.error.code;
    this.requestId = body.error.requestId;
    this.traceId = body.error.traceId;
    this.details = body.error.details;
  }
}

interface RequestOptions {
  method?: 'GET' | 'POST';
  body?: unknown;
  query?: Record<string, string | number | undefined>;
  idempotencyKey?: string;
  signal?: AbortSignal;
}

function buildUrl(path: string, query?: Record<string, string | number | undefined>): string {
  const base = env.apiBaseUrl.replace(/\/+$/, '');
  const url = new URL(`${base}${path}`);
  if (query) {
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== null && value !== '') {
        url.searchParams.set(key, String(value));
      }
    }
  }
  return url.toString();
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, query, idempotencyKey, signal } = options;

  const headers: Record<string, string> = {
    Authorization: `Bearer ${env.devBearerToken}`,
    Accept: 'application/json',
  };

  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  if (method === 'POST') {
    headers['Idempotency-Key'] = idempotencyKey ?? generateIdempotencyKey();
  }

  const response = await fetch(buildUrl(path, query), {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  });

  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  const parsed: unknown = text.length === 0 ? null : safeParseJson(text);

  if (!response.ok) {
    const envelope = parsed as ErrorEnvelopeBody | null;
    if (envelope && typeof envelope === 'object' && 'error' in envelope) {
      throw new ApiError(response.status, envelope);
    }
    throw new ApiError(response.status, {
      error: {
        code: 'unknown_error',
        message: `Request failed with status ${response.status}`,
        details: parsed,
        traceId: null,
        requestId: null,
      },
    });
  }

  return parsed as T;
}

function safeParseJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

export const paymentsApi = {
  list(query: ListPaymentsQuery, signal?: AbortSignal): Promise<PaymentListResponse> {
    return request<PaymentListResponse>('/v1/payments', {
      method: 'GET',
      query: {
        status: query.status,
        cursor: query.cursor,
        limit: query.limit,
      },
      signal,
    });
  },

  get(id: string, signal?: AbortSignal): Promise<Payment> {
    return request<Payment>(`/v1/payments/${encodeURIComponent(id)}`, {
      method: 'GET',
      signal,
    });
  },

  create(payload: CreatePaymentRequest, idempotencyKey?: string): Promise<Payment> {
    return request<Payment>('/v1/payments', {
      method: 'POST',
      body: payload,
      idempotencyKey,
    });
  },

  capture(
    id: string,
    payload: CapturePaymentRequest = {},
    idempotencyKey?: string,
  ): Promise<Payment> {
    return request<Payment>(`/v1/payments/${encodeURIComponent(id)}/capture`, {
      method: 'POST',
      body: payload,
      idempotencyKey,
    });
  },

  refund(id: string, payload: RefundPaymentRequest, idempotencyKey?: string): Promise<Payment> {
    return request<Payment>(`/v1/payments/${encodeURIComponent(id)}/refund`, {
      method: 'POST',
      body: payload,
      idempotencyKey,
    });
  },
};
