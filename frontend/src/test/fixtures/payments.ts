import type { Payment, PaymentEvent, PaymentStatus } from '../../lib/api/types';

interface Seed {
  id: string;
  amountMinor: number;
  currency: string;
  status: PaymentStatus;
  customerReference: string;
  createdAt: string;
  updatedAt: string;
}

const seeds: Seed[] = [
  {
    id: 'pay_01H8XW3F000000000000000001',
    amountMinor: 1_245_000,
    currency: 'USD',
    status: 'Settled',
    customerReference: 'order_88241',
    createdAt: '2026-06-06T11:58:00.000Z',
    updatedAt: '2026-06-06T11:59:00.000Z',
  },
  {
    id: 'pay_01H8XK2L000000000000000002',
    amountMinor: 89_900,
    currency: 'USD',
    status: 'Failed',
    customerReference: 'order_88229',
    createdAt: '2026-06-06T11:46:02.000Z',
    updatedAt: '2026-06-06T11:48:11.000Z',
  },
  {
    id: 'pay_01H8XP9Q000000000000000003',
    amountMinor: 400_000,
    currency: 'USD',
    status: 'Authorized',
    customerReference: 'order_88198',
    createdAt: '2026-06-06T11:00:00.000Z',
    updatedAt: '2026-06-06T11:00:00.000Z',
  },
  {
    id: 'pay_01H8XR5T000000000000000004',
    amountMinor: 4_995,
    currency: 'USD',
    status: 'Captured',
    customerReference: 'order_88142',
    createdAt: '2026-06-06T09:00:00.000Z',
    updatedAt: '2026-06-06T10:00:00.000Z',
  },
  {
    id: 'pay_01H8XN1B000000000000000005',
    amountMinor: 1_820_000,
    currency: 'USD',
    status: 'Settled',
    customerReference: 'order_88011',
    createdAt: '2026-06-06T06:00:00.000Z',
    updatedAt: '2026-06-06T07:00:00.000Z',
  },
];

export const fixturePayments: Payment[] = seeds.map((seed) => ({
  ...seed,
  metadata: { source: 'fixture' },
  events: [],
}));

function eventsFor(id: string): PaymentEvent[] {
  const payment = seeds.find((p) => p.id === id);
  if (!payment) {
    return [];
  }
  if (payment.status === 'Failed') {
    return [
      {
        id: 'evt_001',
        fromStatus: null,
        toStatus: 'Pending',
        actor: 'system',
        reason: 'Payment created',
        payload: {},
        at: '2026-06-06T11:46:02.000Z',
      },
      {
        id: 'evt_002',
        fromStatus: 'Pending',
        toStatus: 'Authorized',
        actor: 'system',
        reason: 'Processor authorization succeeded',
        payload: { auth_code: '882901' },
        at: '2026-06-06T11:46:04.000Z',
      },
      {
        id: 'evt_003',
        fromStatus: 'Authorized',
        toStatus: 'Failed',
        actor: 'system',
        reason: 'Processor declined capture: insufficient_funds',
        payload: {
          processor_code: 'card_declined',
          decline_reason: 'insufficient_funds',
          attempts: '1',
        },
        at: '2026-06-06T11:48:11.000Z',
      },
    ];
  }
  return [
    {
      id: 'evt_001',
      fromStatus: null,
      toStatus: 'Pending',
      actor: 'system',
      reason: 'Payment created',
      payload: {},
      at: payment.createdAt,
    },
  ];
}

export function fixturePayment(id: string): Payment {
  const payment = fixturePayments.find((p) => p.id === id);
  if (!payment) {
    throw new Error(`No fixture for id ${id}`);
  }
  return {
    ...payment,
    events: eventsFor(id),
  };
}
