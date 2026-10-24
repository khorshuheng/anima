import { Component, type ErrorInfo, type ReactNode } from 'react';

interface GraphErrorBoundaryProps {
  children: ReactNode;
  onRetry: () => void;
}

interface GraphErrorBoundaryState {
  failed: boolean;
  message: string;
}

/** Keeps a failed Graph chunk from taking down the Workflows and Bus tabs. */
export class GraphErrorBoundary extends Component<GraphErrorBoundaryProps, GraphErrorBoundaryState> {
  state: GraphErrorBoundaryState = { failed: false, message: '' };

  static getDerivedStateFromError(error: unknown): GraphErrorBoundaryState {
    return {
      failed: true,
      message: error instanceof Error ? error.message : String(error),
    };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Graph tab failed to load', error, info);
  }

  render() {
    if (this.state.failed) {
      return (
        <div className="panel graph-loading" data-testid="graph-load-error">
          <strong>Graph failed to load.</strong>
          <p className="muted small">
            Reload the page to fetch the current graph bundle. The Workflows and Bus tabs are unaffected.
          </p>
          {this.state.message && <p className="muted small mono">{this.state.message}</p>}
          <button className="button" onClick={this.props.onRetry} type="button">
            Reload page
          </button>
        </div>
      );
    }

    return this.props.children;
  }
}
