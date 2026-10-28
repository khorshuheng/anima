import { Component, type ErrorInfo, type ReactNode } from 'react';

interface TabErrorBoundaryProps {
  children: ReactNode;
  /** Tab name shown in the fallback, for example "Graph". */
  tab: string;
  onRetry: () => void;
}

interface TabErrorBoundaryState {
  failed: boolean;
  message: string;
}

/**
 * Keeps one failing tab from taking down the shell. The dashboard has no other error
 * boundary, so without this an unexpected payload (for example a knowledge service on
 * an older revision) would unmount the whole app.
 */
export class TabErrorBoundary extends Component<TabErrorBoundaryProps, TabErrorBoundaryState> {
  state: TabErrorBoundaryState = { failed: false, message: '' };

  static getDerivedStateFromError(error: unknown): TabErrorBoundaryState {
    return {
      failed: true,
      message: error instanceof Error ? error.message : String(error),
    };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(`${this.props.tab} tab failed to load`, error, info);
  }

  render() {
    if (this.state.failed) {
      return (
        <div className="panel graph-loading" data-testid="tab-load-error">
          <strong>{this.props.tab} failed to load.</strong>
          <p className="muted small">
            Reload the page to fetch the current bundle. The other tabs are unaffected.
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
