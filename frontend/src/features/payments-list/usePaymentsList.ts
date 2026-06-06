import { useQuery } from '@tanstack/react-query';
import { paymentsApi } from '../../lib/api/client';
import { queryKeys } from '../../lib/api/queryKeys';
import type { ListPaymentsQuery, PaymentListResponse } from '../../lib/api/types';

export function usePaymentsList(query: ListPaymentsQuery) {
  return useQuery<PaymentListResponse>({
    queryKey: queryKeys.payments.list(query),
    queryFn: ({ signal }) => paymentsApi.list(query, signal),
    placeholderData: (prev) => prev,
  });
}
