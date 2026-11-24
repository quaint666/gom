// jsdom has no matchMedia. Tests that care about the system colour scheme set `matches` themselves.
if (typeof window.matchMedia !== 'function') {
  window.matchMedia = (query: string): MediaQueryList => ({
    matches: false, media: query, onchange: null,
    addEventListener: () => undefined, removeEventListener: () => undefined, addListener: () => undefined,
    removeListener: () => undefined, dispatchEvent: () => false,
  });
}
