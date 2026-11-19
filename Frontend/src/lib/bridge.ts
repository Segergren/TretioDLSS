// Frontend <-> backend bridge. Messages go out through the PhotinoX bridge when present,
// otherwise over the WebSocket (browser development with the mock backend).

type Listener = (method: string, content: any) => void;

const params = new URLSearchParams(window.location.search);
const token = params.get('token') ?? 'dev';
const port = params.get('wsPort') ?? '44130';
const SOCKET_URL = `ws://localhost:${port}/?token=${encodeURIComponent(token)}`;

let socket: WebSocket | null = null;
const listeners = new Set<Listener>();
const connectionListeners = new Set<(open: boolean) => void>();
let open = false;
const pending: string[] = [];

function hasNativeBridge(): boolean {
  const ext = (window as any).external;
  return !!ext && typeof ext.sendMessage === 'function';
}

function setOpen(value: boolean) {
  open = value;
  connectionListeners.forEach((l) => l(value));
}

function connect() {
  const ws = new WebSocket(SOCKET_URL);
  socket = ws;
  ws.onopen = () => {
    setOpen(true);
    send('NewConnection');
    pending.splice(0).forEach((m) => ws.send(m));
  };
  ws.onmessage = (event) => {
    if (event.data === 'pong') return;
    try {
      const data = JSON.parse(event.data);
      listeners.forEach((l) => l(data.method, data.content));
    } catch (e) {
      console.error('Bad message', e);
    }
  };
  ws.onclose = () => {
    setOpen(false);
    setTimeout(connect, 1500);
  };
}

export function startBridge() {
  if (!socket) connect();
}

export function send(method: string, parameters?: unknown) {
  const message = JSON.stringify({ Method: method, Parameters: parameters ?? null });
  if (hasNativeBridge()) {
    (window as any).external.sendMessage(message);
    return;
  }
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(message);
  else pending.push(message);
}

export function onMessage(listener: Listener) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function onConnection(listener: (open: boolean) => void) {
  connectionListeners.add(listener);
  listener(open);
  return () => {
    connectionListeners.delete(listener);
  };
}
