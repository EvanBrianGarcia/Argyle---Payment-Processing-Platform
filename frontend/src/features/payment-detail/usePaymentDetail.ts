import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { paymentsApi } from '../../lib/api/client';
import { queryKeys } from '../../lib/api/queryKeys';
import type { Payment } from '../../lib/api/types';

const IN_FLIGHT_STATUSES = new Set(['Pending', 'Authorized', 'Captured']);

export function usePaymentDetail(id: string | undefined) {
  return useQuery<Payment>({
    queryKey: id ? queryKeys.payments.detail(id) : ['payments', 'detail', 'none'],
    queryFn: ({ signal }) => paymentsApi.get(id!, signal),
    enabled: !!id,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      // Poll fast while the payment is still moving; stop once it lands
      // in a terminal state (Settled / Failed / Refunded).
      return status && IN_FLIGHT_STATUSES.has(status) ? 3_000 : false;
    },
    refetchIntervalInBackground: false,
  });
}

export function useCapturePayment(id: string) {
  const queryClient = useQueryClient();

  return useMutation<Payment, Error, void, { previous: Payment | undefined }>({
    mutationFn: () => paymentsApi.capture(id),
    onMutate: async () => {
      await queryClient.cancelQueries({ queryKey: queryKeys.payments.detail(id) });
      const previous = queryClient.getQueryData<Payment>(queryKeys.payments.detail(id));
      if (previous) {
        queryClient.setQueryData<Payment>(queryKeys.payments.detail(id), {
          ...previous,
          status: 'Captured',
        });
      }
      return { previous };
    },
    onError: (_err, _vars, context) => {
      if (context?.previous) {
        queryClient.setQueryData(queryKeys.payments.detail(id), context.previous);
      }
    },
    onSuccess: (fresh) => {
      queryClient.setQueryData(queryKeys.payments.detail(id), fresh);
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['payments', 'list'] });
    },
  });
}
