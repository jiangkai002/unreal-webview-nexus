/**
 * Browser-side client for UnrealWebView Nexus.
 * The page talks only to WPF through window.chrome.webview. WPF owns the
 * authenticated localhost WebSocket connection to Unreal.
 */
export class UnrealBridgeClient {
  #handlers = new Map();
  #pending = new Map();

  constructor({ requestTimeoutMs = 10_000 } = {}) {
    if (!window.chrome?.webview) {
      throw new Error("This page is not running inside WebView2.");
    }

    this.requestTimeoutMs = requestTimeoutMs;
    window.chrome.webview.addEventListener("message", (event) => {
      this.#receive(event.data);
    });
  }

  get isAvailable() {
    return Boolean(window.chrome?.webview);
  }

  on(type, handler) {
    const handlers = this.#handlers.get(type) ?? new Set();
    handlers.add(handler);
    this.#handlers.set(type, handlers);
    return () => handlers.delete(handler);
  }

  sendEvent(type, payload = {}) {
    this.#post(this.#createEnvelope("event", type, payload));
  }

  request(type, payload = {}, timeoutMs = this.requestTimeoutMs) {
    const envelope = this.#createEnvelope("request", type, payload);

    return new Promise((resolve, reject) => {
      const timeout = window.setTimeout(() => {
        this.#pending.delete(envelope.id);
        reject(new Error(`Unreal request timed out: ${type}`));
      }, timeoutMs);

      this.#pending.set(envelope.id, { resolve, reject, timeout });
      this.#post(envelope);
    });
  }

  /** Call after the latest camera state has actually been applied to the DOM. */
  acknowledgeCameraFrame() {
    this.#post({ type: "host.cameraConsumed" });
  }

  #createEnvelope(kind, type, payload) {
    return {
      version: "1.0",
      id: globalThis.crypto?.randomUUID?.() ??
        `${Date.now()}-${Math.random().toString(16).slice(2)}`,
      kind,
      type,
      timestamp: Date.now(),
      payload,
    };
  }

  #post(envelope) {
    window.chrome.webview.postMessage(envelope);
  }

  #receive(message) {
    if (!message || message.version !== "1.0" || !message.type) {
      return;
    }

    if (message.kind === "response") {
      const pending = this.#pending.get(message.id);
      if (!pending) {
        return;
      }

      window.clearTimeout(pending.timeout);
      this.#pending.delete(message.id);
      if (message.success === false) {
        pending.reject(new Error(message.error?.message ?? "Unreal request failed."));
      } else {
        pending.resolve(message.payload);
      }
      return;
    }

    for (const handler of this.#handlers.get(message.type) ?? []) {
      handler(message.payload, message);
    }
  }
}
