import { describe, expect, it } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server-export';
import { PaymentsListPage } from './PaymentsListPage';
import { fixturePayments } from '../../test/fixtures/payments';

const BASE = 'http://localhost:8080';

function renderListPage(initialUrl: string = '/payments') {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[initialUrl]}>
        <PaymentsListPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('PaymentsListPage', () => {
  it('renders one row per fixture on load', async () => {
    renderListPage();
    for (const payment of fixturePayments) {
      await screen.findByRole('link', { name: new RegExp(payment.id) });
    }
  });

  it('renders the eyebrow and mixed-weight headline', async () => {
    renderListPage();
    expect(await screen.findByText('Overview')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Payments');
  });

  it('filters to Failed when the chip is clicked', async () => {
    const user = userEvent.setup();
    renderListPage();
    await screen.findByRole('link', { name: new RegExp(fixturePayments[0]!.id) });

    await user.click(screen.getByRole('button', { name: 'Failed' }));

    await waitFor(() => {
      const failedRow = screen.getByRole('link', { name: /pay_01H8XK2L/ });
      expect(failedRow).toBeInTheDocument();
    });
    expect(screen.queryByRole('link', { name: /pay_01H8XW3F/ })).not.toBeInTheDocument();
  });

  it('shows an inline empty state when no payments match', async () => {
    server.use(
      http.get(`${BASE}/v1/payments`, () =>
        HttpResponse.json({ data: [], nextCursor: null }),
      ),
    );
    renderListPage();
    const status = await screen.findAllByRole('status');
    const empty = status.find((el) => el.textContent?.includes('No payments match this filter'));
    expect(empty).toBeDefined();
  });

  it('renders the error envelope when the backend returns 5xx', async () => {
    server.use(
      http.get(`${BASE}/v1/payments`, () =>
        HttpResponse.json(
          {
            error: {
              code: 'payment_api_unreachable',
              message: 'Could not load payments.',
              details: null,
              traceId: null,
              requestId: 'req_test_500',
            },
          },
          { status: 503 },
        ),
      ),
    );
    renderListPage();
    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText('payment_api_unreachable')).toBeInTheDocument();
    expect(within(alert).getByText('req_test_500')).toBeInTheDocument();
  });

  it('renders the status rail with the active status highlighted', async () => {
    renderListPage('/payments?status=failed');
    await waitFor(() => {
      const failedTile = screen.getByRole('button', { name: /Failed/, pressed: true });
      expect(failedTile).toBeInTheDocument();
    });
  });
});
