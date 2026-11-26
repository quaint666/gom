// The pages' words: these tests read the Vietnamese ones (jsdom's browser says English).
localStorage.setItem('gom-language', 'vi');

// jsdom has no matchMedia. Tests that care about the system colour scheme set `matches` themselves.
if (typeof window.matchMedia !== 'function') {
  window.matchMedia = (query: string): MediaQueryList => ({
    matches: false, media: query, onchange: null,
    addEventListener: () => undefined, removeEventListener: () => undefined, addListener: () => undefined,
    removeListener: () => undefined, dispatchEvent: () => false,
  });
}

// Every page the server sends carries its limits (Pages.Limits). Written out here, not read from the code, so a changed value
// shows up as a failing test.
document.body.dataset['limits'] = JSON.stringify({
  messageChars: 4000, attachments: 10, imageBytes: 10 * 1024 * 1024, fileBytes: 25 * 1024 * 1024, videoBytes: 45 * 1024 * 1024, posterBytes: 1024 * 1024, pins: 3, nameChars: 60,
  passwordMin: 10, passwordMax: 128, gomIdMin: 3, gomIdMax: 20, searchChars: 100, quoteChars: 100, listPreviewChars: 200, noteChars: 200,
  inviteDays: 30, inviteUses: 100, inviteDefaultDays: 7, inviteDefaultUses: 10, pageVersion: 1,
  contactChars: 2000, contactReplyChars: 200, contactKeepDays: 180,
});
