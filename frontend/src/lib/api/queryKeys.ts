import type { ListPaymentsQuery } from './types';

export const queryKeys = {
  payments: {
    list: (query: ListPaymentsQuery) => ['payments', 'list', query] as const,
    detail: (id: string) => ['payments', 'detail', id] as const,
  },
} as const;
