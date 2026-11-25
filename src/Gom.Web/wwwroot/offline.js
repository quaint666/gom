// The "no connection" page is shown at the address that could not load (a conversation opened from a notification, say), so
// "Thử lại", or the phone coming back online, loads that same address again. Without this script the button goes home.
document.querySelector('[data-retry]')?.addEventListener('click', (e) => { e.preventDefault(); location.reload(); });
addEventListener('online', () => location.reload());
