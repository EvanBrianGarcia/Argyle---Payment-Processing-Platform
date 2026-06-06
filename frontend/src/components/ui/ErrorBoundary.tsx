import { Component, type ErrorInfo, type ReactNode } from 'react';
import { ErrorNotice } from './ErrorNotice';

interface Props {
  children: ReactNode;
}

interface State {
  error: Error | null;
}

export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // eslint-disable-next-line no-console
    console.error('Render error boundary caught:', error, info);
  }

  reset = () => this.setState({ error: null });

  render() {
    if (this.state.error) {
      return (
        <div style={{ padding: 'var(--space-7) var(--space-6)' }}>
          <ErrorNotice
            code="render_error"
            message={this.state.error.message || 'Something went wrong.'}
            onRetry={this.reset}
          />
        </div>
      );
    }
    return this.props.children;
  }
}
