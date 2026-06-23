class WebSocketService {
  constructor() {
    const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    this.url = `${wsProtocol}//${window.location.host}`;
    this.ws = null;
    this.listeners = new Map();
    this.channels = new Set();
    this.state = 'disconnected';
    this.reconnectAttempts = 0;
    this.maxReconnectDelay = 30000;
    this.reconnectTimer = null;
    this.stateListeners = new Set();
  }

  static getInstance() {
    if (!WebSocketService._instance) {
      WebSocketService._instance = new WebSocketService();
    }
    return WebSocketService._instance;
  }

  setUrl(url) {
    this.url = url;
  }

  connect() {
    if (this.ws && (this.ws.readyState === WebSocket.CONNECTING || this.ws.readyState === WebSocket.OPEN)) {
      return;
    }

    this._setState('connecting');

    try {
      this.ws = new WebSocket(this.url);
    } catch {
      this._setState('error');
      this._scheduleReconnect();
      return;
    }

    this.ws.onopen = () => {
      this.reconnectAttempts = 0;
      this._setState('connected');
      this.channels.forEach(channel => this._sendSubscribe(channel));
    };

    this.ws.onmessage = (event) => {
      try {
        const msg = JSON.parse(event.data);
        if (msg.channel && this.listeners.has(msg.channel)) {
          this.listeners.get(msg.channel).forEach(cb => cb(msg.data));
        }
      } catch {
        // ignore malformed messages
      }
    };

    this.ws.onclose = () => {
      this._setState('disconnected');
      this._scheduleReconnect();
    };

    this.ws.onerror = () => {
      this._setState('error');
    };
  }

  disconnect() {
    clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
    this.reconnectAttempts = 0;
    if (this.ws) {
      this.ws.onclose = null;
      this.ws.close();
      this.ws = null;
    }
    this._setState('disconnected');
  }

  subscribe(channel) {
    this.channels.add(channel);
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this._sendSubscribe(channel);
    }
  }

  unsubscribe(channel) {
    this.channels.delete(channel);
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this._send({ type: 'unsubscribe', channel });
    }
  }

  on(channel, callback) {
    if (!this.listeners.has(channel)) {
      this.listeners.set(channel, new Set());
    }
    this.listeners.get(channel).add(callback);
  }

  off(channel, callback) {
    const cbs = this.listeners.get(channel);
    if (cbs) {
      cbs.delete(callback);
      if (cbs.size === 0) {
        this.listeners.delete(channel);
      }
    }
  }

  onStateChange(callback) {
    this.stateListeners.add(callback);
    return () => this.stateListeners.delete(callback);
  }

  getState() {
    return this.state;
  }

  send(data) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this._send(data);
    }
  }

  _send(data) {
    this.ws.send(JSON.stringify(data));
  }

  _sendSubscribe(channel) {
    this._send({ type: 'subscribe', channel });
  }

  _setState(state) {
    this.state = state;
    this.stateListeners.forEach(cb => cb(state));
  }

  _scheduleReconnect() {
    clearTimeout(this.reconnectTimer);
    const delay = Math.min(1000 * Math.pow(2, this.reconnectAttempts), this.maxReconnectDelay);
    this.reconnectAttempts++;
    this.reconnectTimer = setTimeout(() => this.connect(), delay);
  }
}

WebSocketService._instance = null;

export default WebSocketService;
