/** What Chrome on Android gives us when the page can be installed (not in the standard DOM types). */
interface InstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

export type InstallResult = 'accepted' | 'dismissed' | 'unavailable';

export interface Installer {
  /** True while the browser has offered to install the app and it has not been installed or used yet. */
  available(): boolean;
  /** Shows the browser's own install question. One offer can be used once. */
  install(): Promise<InstallResult>;
  /** Called whenever {@link available} may have changed. */
  onChange(callback: () => void): void;
}

/**
 * Keeps the browser's offer to install the app until the person asks for it. The offer comes once, early (before the
 * settings page exists), so this is started on every page. Browsers that never make an offer (iPhone, desktop Firefox,
 * an app already installed) leave {@link Installer.available} false and nothing is shown.
 */
export function createInstaller(target: EventTarget): Installer {
  let offer: InstallPromptEvent | null = null;
  const listeners: Array<() => void> = [];
  const changed = (): void => { for (const l of listeners) l(); };

  target.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();   // no pop-up of the browser's own; ours is the button
    offer = e as InstallPromptEvent;
    changed();
  });
  target.addEventListener('appinstalled', () => { offer = null; changed(); });

  return {
    available: () => offer !== null,
    onChange: (callback) => { listeners.push(callback); },
    async install() {
      const current = offer;
      if (current === null) return 'unavailable';
      offer = null;   // an offer works once, whatever the answer
      try {
        await current.prompt();
        const { outcome } = await current.userChoice;
        changed();
        return outcome;
      } catch { changed(); return 'unavailable'; }
    },
  };
}
