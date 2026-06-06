export const PAYMENT_STATUSES = [
  'Pending',
  'Authorized',
  'Captured',
  'Settled',
  'Failed',
  'Refunded',
] as const;

export type PaymentStatus = (typeof PAYMENT_STATUSES)[number];

export interface PaymentEvent {
  id: string;
  fromStatus: PaymentStatus | null;
  toStatus: PaymentStatus;
  actor: string;
  reason: string;
  payload: Record<string, string>;
  at: string;
}

export interface Payment {
  id: string;
  amountMinor: number;
  currency: string;
  status: PaymentStatus;
  customerReference: string | null;
  metadata: Record<string, string>;
  createdAt: string;
  updatedAt: string;
  events: PaymentEvent[];
}

export interface PaymentListResponse {
  data: Payment[];
  nextCursor: string | null;
}

export interface CreatePaymentRequest {
  amountMinor: number;
  currency: string;
  cardToken: string;
  customerReference?: string;
  metadata?: Record<string, string>;
}

export interface CapturePaymentRequest {
  amountMinor?: number;
}

export interface RefundPaymentRequest {
  reason: string;
}

export interface ErrorBody {
  code: string;
  message: string;
  details: unknown;
  traceId: string | null;
  requestId: string | null;
}

export interface ErrorEnvelopeBody {
  error: ErrorBody;
}

export interface ListPaymentsQuery {
  status?: PaymentStatus;
  cursor?: string;
  limit?: number;
}

export function isPaymentStatus(value: string): value is PaymentStatus {
  return (PAYMENT_STATUSES as readonly string[]).includes(value);
}
