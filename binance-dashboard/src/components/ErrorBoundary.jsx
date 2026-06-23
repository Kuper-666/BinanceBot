import { Component } from 'react';

export default class ErrorBoundary extends Component {
  constructor(props) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error) {
    return { hasError: true, error };
  }

  render() {
    if (this.state.hasError) {
      return (
        <div style={{ padding: '40px', textAlign: 'center', color: '#ef4444' }}>
          <h2 style={{ fontSize: '18px', marginBottom: '8px' }}>Something went wrong</h2>
          <p style={{ color: '#888', fontSize: '13px' }}>{this.state.error?.message}</p>
          <button
            onClick={() => { this.setState({ hasError: false, error: null }); window.location.reload(); }}
            style={{ marginTop: '16px', padding: '8px 20px', borderRadius: '6px', border: 'none', background: '#22c55e', color: '#000', cursor: 'pointer', fontWeight: 600, fontSize: '13px' }}
          >Reload</button>
        </div>
      );
    }
    return this.props.children;
  }
}
