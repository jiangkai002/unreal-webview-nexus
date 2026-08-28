namespace DigitalTwin.Host.Services;

internal static class HitRegionScript
{
    private const string ModePlaceholder = "__DT_TRANSPARENT_BACKGROUND_MODE__";

    internal static string CreateSource(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("auto" or "force" or "off"))
        {
            normalized = "auto";
        }

        return Source.Replace(ModePlaceholder, normalized);
    }

    internal const string Source = """
        (() => {
          if (window.__digitalTwinHitRegionBridgeInstalled) return;
          window.__digitalTwinHitRegionBridgeInstalled = true;

          const transparentBackgroundMode = '__DT_TRANSPARENT_BACKGROUND_MODE__';
          const MAX_REGIONS = 512;
          const UPDATE_INTERVAL_MS = 100;
          let updateTimer = 0;
          let pointerPressed = false;
          let dragging = false;
          let pointerCaptureActive = false;
          let dragCandidate = null;
          let emulatedDrag = null;
          let suppressNextClick = false;
          const syntheticDragEvents = new WeakSet();
          const DRAG_THRESHOLD_PX = 5;

          function draggableFrom(target) {
            if (!(target instanceof Element)) return null;
            const element = target.closest('[draggable="true"]');
            return element instanceof HTMLElement || element instanceof SVGElement
              ? element
              : null;
          }

          function createDragEvent(type, state, pointerEvent, target) {
            const event = new DragEvent(type, {
              bubbles: true,
              cancelable: type !== 'dragleave' && type !== 'dragend',
              composed: true,
              dataTransfer: state.dataTransfer,
              clientX: pointerEvent.clientX,
              clientY: pointerEvent.clientY,
              screenX: pointerEvent.screenX,
              screenY: pointerEvent.screenY,
              ctrlKey: pointerEvent.ctrlKey,
              shiftKey: pointerEvent.shiftKey,
              altKey: pointerEvent.altKey,
              metaKey: pointerEvent.metaKey,
              button: pointerEvent.button,
              buttons: pointerEvent.buttons
            });
            syntheticDragEvents.add(event);
            target.dispatchEvent(event);
            return event;
          }

          function restoreNativeDraggable(candidate) {
            if (!candidate) return;
            if (candidate.hadDraggableAttribute) {
              candidate.source.setAttribute('draggable', candidate.draggableValue);
            } else {
              candidate.source.removeAttribute('draggable');
            }
          }

          function finishEmulatedDrag(pointerEvent, cancelled) {
            const state = emulatedDrag;
            const candidate = dragCandidate;
            if (!state) {
              restoreNativeDraggable(candidate);
              dragCandidate = null;
              return;
            }

            if (state.target) {
              if (!cancelled && state.dropAllowed) {
                createDragEvent('drop', state, pointerEvent, state.target);
              } else {
                createDragEvent('dragleave', state, pointerEvent, state.target);
              }
            }
            createDragEvent('dragend', state, pointerEvent, state.source);
            restoreNativeDraggable(candidate);
            emulatedDrag = null;
            dragCandidate = null;
            suppressNextClick = true;
            releasePointerCapture();
          }

          function updateEmulatedDrag(pointerEvent) {
            const state = emulatedDrag;
            if (!state) return;

            createDragEvent('drag', state, pointerEvent, state.source);
            const target = document.elementFromPoint(pointerEvent.clientX, pointerEvent.clientY);
            if (target !== state.target) {
              if (state.target) createDragEvent('dragleave', state, pointerEvent, state.target);
              state.target = target;
              state.dropAllowed = false;
              if (target) createDragEvent('dragenter', state, pointerEvent, target);
            }
            if (state.target) {
              const dragOver = createDragEvent('dragover', state, pointerEvent, state.target);
              state.dropAllowed = dragOver.defaultPrevented;
            }
          }

          function startEmulatedDrag(pointerEvent) {
            const candidate = dragCandidate;
            if (!candidate) return false;
            const state = {
              source: candidate.source,
              dataTransfer: new DataTransfer(),
              target: null,
              dropAllowed: false
            };
            const dragStart = createDragEvent('dragstart', state, pointerEvent, state.source);
            if (dragStart.defaultPrevented) {
              restoreNativeDraggable(candidate);
              dragCandidate = null;
              return false;
            }
            emulatedDrag = state;
            dragging = true;
            syncPointerCapture();
            updateEmulatedDrag(pointerEvent);
            return true;
          }

          function postPointerCapture(captured) {
            pointerCaptureActive = captured;
            window.chrome.webview.postMessage({
              version: '1.0',
              kind: 'event',
              type: 'host.pointerCaptureChanged',
              payload: { captured }
            });
          }

          function syncPointerCapture() {
            const captured = pointerPressed || dragging;
            if (captured === pointerCaptureActive) return;
            postPointerCapture(captured);
          }

          function releasePointerCapture() {
            pointerPressed = false;
            dragging = false;
            syncPointerCapture();
          }

          function isVisible(element, style, rect) {
            return rect.width > 0 && rect.height > 0 &&
              style.display !== 'none' && style.visibility !== 'hidden' &&
              style.pointerEvents !== 'none' &&
              Number.parseFloat(style.opacity || '1') > 0.01;
          }

          function backgroundColorAlpha(style) {
            if (style.backgroundImage !== 'none') return 1;
            const match = style.backgroundColor.match(/rgba?\(([^)]+)\)/);
            if (!match) return 0;
            const parts = match[1].split(',').map(part => part.trim());
            if (parts.length < 4) return 1;
            const alpha = Number.parseFloat(parts[3]);
            return Number.isFinite(alpha) ? alpha : 1;
          }

          function hasVisiblePaint(element, style) {
            const interactive = element.matches(
              'a,button,input,select,textarea,summary,[role="button"],[role="link"],[role="menuitem"],[tabindex]');
            const directText = Array.from(element.childNodes).some(node =>
              node.nodeType === Node.TEXT_NODE && node.textContent.trim().length > 0);
            const hasBackground = style.backgroundImage !== 'none' ||
              (!style.backgroundColor.endsWith(', 0)') && style.backgroundColor !== 'transparent');
            const hasBorder = ['Top', 'Right', 'Bottom', 'Left'].some(side =>
              Number.parseFloat(style[`border${side}Width`]) > 0 &&
              style[`border${side}Style`] !== 'none');
            return interactive || directText || hasBackground || hasBorder;
          }

          // Forcing the page canvas transparent breaks normal pages: components
          // relying on the html/body background turn see-through, and
          // backdrop-filter stops blurring. Only do it for pages that opted
          // into the overlay protocol via [data-web-hit], or when the host
          // forces it through configuration.
          function syncTransparentCanvas(explicitCount) {
            const transparent = transparentBackgroundMode === 'force' ||
              (transparentBackgroundMode === 'auto' && explicitCount > 0);
            document.documentElement.classList.toggle('dt-web-transparent', transparent);
          }

          function collectRegions() {
            const explicit = Array.from(document.querySelectorAll('[data-web-hit]'));
            const root = document.documentElement;
            const body = document.body;
            const candidates = explicit.length > 0
              ? explicit
              : [
                  ...(root ? [root] : []),
                  ...(body ? [body] : []),
                  ...Array.from(body?.querySelectorAll('*') || [])
                ];
            const regions = [];

            for (const element of candidates) {
              if (!(element instanceof HTMLElement) && !(element instanceof SVGElement)) continue;
              const style = getComputedStyle(element);
              for (const rect of element.getClientRects()) {
                if (!isVisible(element, style, rect)) continue;

                if (explicit.length === 0) {
                  if (!hasVisiblePaint(element, style)) continue;
                  const coversViewport = rect.width >= window.innerWidth * 0.98 &&
                    rect.height >= window.innerHeight * 0.98;
                  // Full-viewport containers stay click-through unless they
                  // paint an opaque page canvas.
                  if (coversViewport && backgroundColorAlpha(style) < 0.9) continue;
                }

                const x = Math.max(0, rect.x);
                const y = Math.max(0, rect.y);
                regions.push({
                  x,
                  y,
                  width: Math.max(0, Math.min(rect.width, window.innerWidth - x)),
                  height: Math.max(0, Math.min(rect.height, window.innerHeight - y))
                });
                if (regions.length >= MAX_REGIONS) break;
              }
              if (regions.length >= MAX_REGIONS) break;
            }

            // Nested UI elements frequently produce rectangles already covered by
            // an opaque parent panel. Removing contained rectangles substantially
            // reduces both the WebView message and the native HRGN complexity.
            const reducedRegions = regions
              .sort((left, right) => (right.width * right.height) - (left.width * left.height))
              .filter((region, index, sorted) => !sorted.slice(0, index).some(container =>
                region.x >= container.x &&
                region.y >= container.y &&
                region.x + region.width <= container.x + container.width &&
                region.y + region.height <= container.y + container.height));

            syncTransparentCanvas(explicit.length);
            window.chrome.webview.postMessage({
              version: '1.0',
              kind: 'event',
              type: 'host.hitRegionsChanged',
              payload: {
                regions: reducedRegions,
                devicePixelRatio: window.devicePixelRatio,
                viewportWidth: window.innerWidth,
                viewportHeight: window.innerHeight,
                source: explicit.length > 0 ? 'explicit' : 'automatic'
              }
            });
          }

          function schedule() {
            if (updateTimer) return;
            updateTimer = window.setTimeout(() => {
              updateTimer = 0;
              requestAnimationFrame(collectRegions);
            }, UPDATE_INTERVAL_MS);
          }

          function install() {
            const style = document.createElement('style');
            style.textContent =
              'html.dt-web-transparent,html.dt-web-transparent body{background-color:transparent!important;}';
            document.documentElement.appendChild(style);

            new ResizeObserver(schedule).observe(document.documentElement);
            new MutationObserver(schedule).observe(document.documentElement, {
              subtree: true,
              childList: true,
              characterData: true,
              attributes: true,
              attributeFilter: ['class', 'style', 'hidden', 'open']
            });
            window.addEventListener('resize', schedule);
            window.addEventListener('scroll', schedule, true);
            window.addEventListener('pointerdown', event => {
              if (event.button !== 0) return;
              pointerPressed = true;
              syncPointerCapture();

              const source = draggableFrom(event.target);
              if (!source || event.pointerType === 'touch') return;
              dragCandidate = {
                source,
                pointerId: event.pointerId,
                startX: event.clientX,
                startY: event.clientY,
                hadDraggableAttribute: source.hasAttribute('draggable'),
                draggableValue: source.getAttribute('draggable') || 'true'
              };
              // Disabling the native HTML drag before Chromium crosses its drag
              // threshold avoids the unsupported OLE loop in composition mode.
              source.setAttribute('draggable', 'false');
              try { source.setPointerCapture(event.pointerId); } catch { }
            }, true);
            window.addEventListener('pointermove', event => {
              if (!dragCandidate || event.pointerId !== dragCandidate.pointerId) return;
              if (!emulatedDrag) {
                const distance = Math.hypot(
                  event.clientX - dragCandidate.startX,
                  event.clientY - dragCandidate.startY);
                if (distance < DRAG_THRESHOLD_PX || !startEmulatedDrag(event)) return;
              } else {
                updateEmulatedDrag(event);
              }
              event.preventDefault();
            }, true);
            window.addEventListener('pointerup', event => {
              if (event.button !== 0) return;
              if (dragCandidate && event.pointerId === dragCandidate.pointerId) {
                finishEmulatedDrag(event, false);
              }
              pointerPressed = false;
              if (event.buttons === 0 && !dragging) {
                // The release can land in a different frame than the press
                // (events do not cross frame boundaries). Broadcast the
                // release unconditionally so host-side capture never sticks.
                postPointerCapture(false);
              } else {
                syncPointerCapture();
              }
            }, true);
            window.addEventListener('pointercancel', () => {
              if (emulatedDrag) {
                finishEmulatedDrag(new PointerEvent('pointercancel'), true);
              } else if (dragCandidate) {
                restoreNativeDraggable(dragCandidate);
                dragCandidate = null;
              }
              pointerPressed = false;
              syncPointerCapture();
            }, true);
            window.addEventListener('dragstart', event => {
              if (!syntheticDragEvents.has(event) && draggableFrom(event.target)) {
                event.preventDefault();
                return;
              }
              dragging = true;
              syncPointerCapture();
            }, true);
            window.addEventListener('drop', event => {
              releasePointerCapture(event);
            }, true);
            window.addEventListener('dragend', event => {
              releasePointerCapture(event);
            }, true);
            window.addEventListener('blur', () => {
              if (emulatedDrag) {
                finishEmulatedDrag(new PointerEvent('pointercancel'), true);
              } else if (dragCandidate) {
                restoreNativeDraggable(dragCandidate);
                dragCandidate = null;
              }
              releasePointerCapture();
            });
            window.addEventListener('click', event => {
              if (!suppressNextClick) return;
              suppressNextClick = false;
              event.preventDefault();
              event.stopImmediatePropagation();
            }, true);
            requestAnimationFrame(collectRegions);
          }

          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', install, { once: true });
          } else {
            install();
          }
        })();
        """;
}
