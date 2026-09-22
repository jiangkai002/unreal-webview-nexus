namespace DigitalTwin.Host.Services;

/// <summary>
/// Restores DOM dblclick events that WebView2CompositionControl can omit.
/// See https://github.com/MicrosoftEdge/WebView2Feedback/issues/5099.
/// </summary>
internal static class DoubleClickCompatibilityScript
{
    internal const string Source = """
        (() => {
          if (window.__digitalTwinDoubleClickCompatibilityInstalled) return;
          window.__digitalTwinDoubleClickCompatibilityInstalled = true;

          const DOUBLE_CLICK_INTERVAL_MS = 500;
          const DOUBLE_CLICK_RADIUS_PX = 4;
          const syntheticEvents = new WeakSet();
          let previousClick = null;
          let pendingDoubleClick = null;

          function cancelPendingDoubleClick() {
            if (!pendingDoubleClick) return;
            clearTimeout(pendingDoubleClick.timer);
            pendingDoubleClick = null;
          }

          // WebView2CompositionControl currently forwards two ordinary button-down
          // messages instead of a Win32 double-click message in some WPF hosts. The
          // page consequently receives two click events but no dblclick event.
          window.addEventListener('click', event => {
            if (!(event instanceof MouseEvent) || event.button !== 0 || !event.isTrusted) return;

            const click = {
              target: event.target,
              time: performance.now(),
              clientX: event.clientX,
              clientY: event.clientY,
              screenX: event.screenX,
              screenY: event.screenY,
              ctrlKey: event.ctrlKey,
              shiftKey: event.shiftKey,
              altKey: event.altKey,
              metaKey: event.metaKey
            };
            const previous = previousClick;
            const isPair = previous &&
              previous.target === click.target &&
              click.time - previous.time <= DOUBLE_CLICK_INTERVAL_MS &&
              Math.abs(click.screenX - previous.screenX) <= DOUBLE_CLICK_RADIUS_PX &&
              Math.abs(click.screenY - previous.screenY) <= DOUBLE_CLICK_RADIUS_PX;

            if (!isPair) {
              previousClick = click;
              return;
            }

            previousClick = null;
            cancelPendingDoubleClick();
            const pending = {
              timer: window.setTimeout(() => {
                if (pendingDoubleClick !== pending) return;
                pendingDoubleClick = null;
                if (!(click.target instanceof EventTarget) ||
                    (click.target instanceof Node && !click.target.isConnected)) return;

                const doubleClick = new MouseEvent('dblclick', {
                  bubbles: true,
                  cancelable: true,
                  composed: true,
                  view: window,
                  detail: 2,
                  screenX: click.screenX,
                  screenY: click.screenY,
                  clientX: click.clientX,
                  clientY: click.clientY,
                  ctrlKey: click.ctrlKey,
                  shiftKey: click.shiftKey,
                  altKey: click.altKey,
                  metaKey: click.metaKey,
                  button: 0,
                  buttons: 0
                });
                syntheticEvents.add(doubleClick);
                click.target.dispatchEvent(doubleClick);
              }, 0)
            };
            pendingDoubleClick = pending;
          }, true);

          // A future WebView2 runtime may fix the native event. Because native
          // dblclick follows the second click in the same task, it wins the race
          // and suppresses the compatibility event queued above.
          window.addEventListener('dblclick', event => {
            if (syntheticEvents.has(event)) return;
            previousClick = null;
            cancelPendingDoubleClick();
          }, true);

          window.addEventListener('blur', () => {
            previousClick = null;
            cancelPendingDoubleClick();
          });
        })();
        """;
}
