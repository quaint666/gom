# Voice calls: the plan

Written 2026-11-25 from a discussion with H, before any code. It is what the next sessions build from. Claims are
marked **[verified]** (seen in the code, a primary source or real output this session), **[stated]** (a source or a
person said so; which is given), or **[inferred]** (reasoned, not tried). Facts about other products are as of
November 2026 and should be checked again if this is picked up much later.

## 1. Why, and the shape chosen

Voice calls are what keeps Gom from replacing Zalo as a daily app [stated: H]. About 95% of the people using Gom are on
Android, all on Android 12 or newer, mostly Samsung phones [stated: H].

A web page cannot make calls work like a phone's:

- **iPhone (Home Screen web app):** iOS mutes the microphone while the app is not on screen; one test on iOS 18.6,
  November 2026 [stated: roumail/webrtc-reprex]. An incoming call can only be an ordinary notification; ringing over
  the lock screen needs CallKit, which is native only.
- **Android (Chrome, installed or not):** the microphone stops about 5 seconds after the page goes to the background
  (Chromium issue 374374232, open) [stated]. The usual fix is a native foreground service.
- **"Incoming call" notifications for web apps** (a ringtone, answer and decline buttons) exist only as an origin
  trial in Edge 140+ on Windows; MDN's compatibility data has no entry for it [verified: browser-compat-data].
- **No web-only layer gets around this:** muting the microphone of a background browser is the operating system's
  privacy rule, and only native code the system trusts (Telecom on Android, CallKit on iOS) is exempt.

**Chosen (H, 2026-11-25): a small native Android app, "option B".**

- The app is a Kotlin shell around the Gom web app: chat, groups, stickers and settings stay the web code, shown in a
  web view. Nothing of the interface is written twice.
- **Calls are native:** Jetpack `core-telecom` registers each call with Android's own call system (microphone,
  earpiece, speaker, Bluetooth, the system call controls, holding for a regular phone call), a native WebRTC library
  carries the audio, and FCM wakes the phone for an incoming call with a full-screen ringing screen.
- **Rejected:** a call inside the web view ("option A": unproven with the screen off, and Chrome's web view has no
  audio routing, so no earpiece or Bluetooth choice); a Trusted Web Activity (Chrome's limits apply); React Native,
  Flutter or Compose Multiplatform (a rewrite of the interface, a weaker web version, and calls still need native code
  per platform; Compose Multiplatform's web target is Beta [stated: Kotlin docs]); a fully native app (three codebases).
- **The web version stays** for iPhone and computers. Calls come to it now, before the app's, working only while Gom is open
  (section 2b; H, 2026-11-26).

Version 1 is **1:1 voice calls between friends**. Not in version 1: group calls (they need a media server, an SFU),
video, recording, iPhone native.

## 2. Decisions (agreed with H on 2026-11-25)

**Behaviour**

1. Only friends can call each other, 1:1. Group members who are not friends cannot.
2. Ringing lasts **45 seconds**, then the call is missed. A server setting (`Calls:RingSeconds`), changeable without a
   build. For comparison: WhatsApp about 45 to 60 seconds, Telegram rings with no limit (a 30-second limit is an open
   request), Zalo not found [stated: a third-party guide and Telegram's bug tracker].
3. No limit on how long a call lasts.
4. A second incoming call while in a call: the caller hears "busy"; it is a missed call for the person called.
5. A regular phone call arriving during a Gom call: Android's call system holds or ends it, its default.
6. Each call leaves a row in the chat: "Cuộc gọi thoại · 3:05", "Cuộc gọi nhỡ", "Đã từ chối", "Máy bận". A missed call
   counts as unread and is notified.
7. **A muted chat still rings for calls** [H]. Mute is for messages.
8. The notification privacy setting is followed on the lock screen: with "nothing", the ringing screen says "Cuộc gọi
   đến" without the name, and the name shows once unlocked (Android's public version of a notification).
9. Decline with a quick message ("Gọi lại sau"): later, not version 1.
10. The call screens (incoming, in call) are native, so they work over the lock screen.
11. A person on several devices is rung on all of them. Answering on one stops the others ("answered elsewhere");
    declining on one declines everywhere; the caller hanging up stops all of them, including a phone ringing from a
    push.
12. A network change during a call (Wi-Fi to mobile data) is survived with an ICE restart. Must-have in version 1;
    Cloudflare also recommends it because its maintenance can interrupt relayed connections [stated: Cloudflare docs].
13. Stored about a call: who, when, how long, and how it ended (answered, missed, declined, busy, failed). No audio, no
    quality statistics.

**Server**

14. Call steps (call, accept, decline, hang up, connection details) are **REST requests**, with the Origin check, and
    reach the other side as socket frames. This keeps the existing rule: sending is REST, the socket only takes "sync"
    [verified: `SocketEndpoint.cs`].
15. The server keeps each call's state and checks friendship (and that neither has blocked the other) at every step.
16. A block, an unfriend, an account deletion, a session sign-out or revoke ends a call in progress. Account deletion
    and the owner's message trim also remove call records.
17. If the server restarts during a call, the call goes on (the audio is phone to phone); the record is written when
    the call ends.
18. Calling attempts are rate-limited, reusing `SendLimiter`.
19. The call protocol carries a version number from the start; the server accepts the current and the previous
    version, since the app and the server are updated at different times.
20. The whole feature sits behind a configuration switch, off by default, until it works end to end.

**Relay (TURN)**

21. **Cloudflare Realtime TURN.** A relay must take traffic from the internet; a managed one means no extra server to
    run, open or patch, and Gom's server only makes outgoing HTTPS calls to it. A small rented server with coturn is the
    fallback if Cloudflare is ever unwanted.
22. **TURN logins last 3 hours** [H: some family calls are long]. A longer call gets a fresh login mid-call
    (`setConfiguration()` and an ICE restart), without hanging up. Logins are issued only when a call really starts
    (the caller when dialling, the person called when answering) and revoked when it ends.
23. Each login is tagged with an opaque ID (a keyed hash of the user's ID), never a name or Gom ID.
24. If Cloudflare does not answer, the call tries a direct connection only (STUN) and says so if that fails.

**Phone wake-up (FCM)**

25. **Firebase Cloud Messaging** wakes the phone for a call. A device's FCM token is registered next to its Web Push
    subscription, tied to the session like it.
26. The call push carries only a call ID; the app fetches the rest. It is high priority and **expires with the
    ringing** (FCM `ttl` about 45 seconds), so a late push never rings. A "cancel" push stops a phone that is ringing.
27. Every high-priority push must show a notification at once, or FCM lowers the app's priority (judged over the last
    7 days) [stated: Firebase docs]. A call push always shows the call.

**Android app**

28. Plain Kotlin, Jetpack Compose for the native screens, in an `android/` folder of this repository; CI builds it.
    Code in English, screens in Vietnamese, configuration from the build or the server, no hardcoded hosts.
29. Minimum Android 12 (API 31) [H: the family's phones]. `core-telecom`'s `CallsManager` needs API 26 or newer
    [verified: its reference page].
30. A phoneCall foreground service started through self-managed Telecom. Two calling SDKs report that a
    microphone-type service started from the background is refused on Android 14 to 16 and the other side hears
    silence; the phoneCall type through Telecom avoids it [stated: twilio_voice #297, Rocket.Chat #7233].
31. The web view loads only Gom's own origin; other links open in the browser. The bridge between page and Kotlin
    (androidx.webkit `WebMessageListener`) answers Gom's origin only, and carries only: the app is installed, start a
    call, the FCM token.
32. The native call code uses the web view's session (Android's `CookieManager`), so there is no second sign-in.
33. The app opens its own socket only during a call; otherwise FCM wakes it. Up to 5 sockets per person are allowed
    [verified: `MaxSocketsPerUser`], so this fits.
34. WebRTC library: Google no longer publishes a ready-made Android build. Candidates: GetStream's
    `stream-webrtc-android` (latest 1.3.9 or 1.3.10, sources disagree) and `io.github.webrtc-sdk` (last release
    6 September 2026). Choose by checking Maven Central for the newest release and the licence.
35. Samsung puts apps to sleep ("Sleeping apps", "Deep sleeping apps"). The app shows a short guide to "Never sleeping
    apps" when calls might be missed [stated: known Samsung behaviour; to confirm on a test phone].
36. Firebase's `google-services.json` is never committed: CI writes it from a stored secret.
37. No crash-reporting library at first; Play Console's own crash list is used.

**Play and publishing**

38. H has a Google Play developer account (personal, US$25 once) [stated: H, 2026-11-25]. First the **internal
    testing** track for family and friends (testers by email, installs and updates from the Play Store). A public
    release needs a closed test with 12 testers for 14 days, for personal accounts made after 13 November 2023
    [verified: Play Console Help].
39. Play App Signing: Google holds the signing key; H keeps the upload key, never in the repository.
40. A public privacy policy page at **`/privacy`**, in Vietnamese and English (the page's language, as every page) [H, 2026-11-26; first planned in Vietnamese only], linked from Settings and from sign-in. Play requires one, naming who is behind the app and a way to ask about privacy: the name comes from configuration (`Site__Operator`), the way to ask is a contact form.
41. Play declarations to prepare: Data safety; the phoneCall foreground service with a short video; the full-screen
    notification permission (a calling app keeps it; Android 14+ grants it by default and the Play Store removes it
    from apps that are not calling or alarm apps [verified: AOSP docs]); the target Android version Play requires each
    year (this year's deadline was 31 August 2026 [stated: Play policy pages]). In-app account deletion already exists.

**Later, for calls in the web version**

42. `Permissions-Policy` today says `microphone=()`, which blocks the microphone on every Gom page [verified:
    `SecurityHeaders.cs`]. Web calls need `microphone=(self)`, changed only then.

**Messages in the app (added 2026-11-25)**

43. A web view has no Web Push, so in the app new messages also come by FCM. The push carries only the conversation and
    the message's IDs; the app fetches the words over HTTPS with its session and builds the notification itself, so
    Google never sees them, and the device's notification level still applies.
44. A device that registers for FCM stops its Web Push, so nothing arrives twice. The web half (the words in a
    notification) is built first, for browsers; this half comes with the app.

## 2b. Calls on the web (agreed with H on 2026-11-26)

H asked for web calls alongside the app. One call written in the page could also serve the app, with Kotlin keeping only what
Android demands (the wake-up and full-screen ringing, Telecom, the phoneCall foreground service, the sound route). One open-source
project showed a call in an app's web view under Telecom staying up five minutes with the screen off in Doze, on an Android 16
emulator only (no real microphone, earpiece or Bluetooth) [stated: selfhost-unihub pull request 164, 22 November 2026]; whether
a web view's call follows `setCommunicationDevice` is not known. So web calls come first, and a test on a Samsung phone decides
the app's way.

1. A person can be called while one of their pages that takes calls is open: the page says so as its socket connects
   (`/ws?calls=1`, keeping the socket's rule of taking only "sync"). The app's FCM registration stops counting until the app
   takes calls; until then the app's web view says nothing, shows no call button and ignores incoming calls (it has no
   microphone permission). A closed tab does not ring; the missed-call notification still comes.
2. Every page of the person that takes calls rings (decision 11): answering on one stops the others.
3. Browsers on phones too, iPhone included; the call says "Giữ Gom trên màn hình trong lúc gọi", since a browser in the
   background loses the microphone.
4. `Permissions-Policy` becomes `microphone=(self)` (decision 42).
5. Only the chat page takes calls; Settings, Friends, a group's page and the others are pages of their own, so going to one
   ends a call. During a call the page's own update waits, leaving asks first (the browser's question), and a page closed
   anyway hangs up on its way out so the other side is not left waiting.
6. A ring made in the page (Web Audio, no sound file) and the tab's title; if the browser blocks the sound until the person has
   touched the page, the ringing screen is still there.
7. Each offer and answer waits up to 3 s for its addresses, then goes with what it has (no trickle ICE, as agreed in 2a).
8. After a network change only the caller restarts the connection (`restartIce()` and a new offer through `/sdp`), when it
   stays down about 2 s or fails, so the two never offer at once.
9. At 2 h 50 min the page asks for a fresh relay login, applies it and restarts (decision 22).
10. A microphone refused: "Gom cần micro để gọi" with how to allow it; the call does not start, or a ringing one is declined.
11. With no relay set up, a direct connection only (decision 24); if that fails, "Không kết nối được". An answered call that
    has not connected after 30 s (`CONNECT_MS`) ends the same way and is hung up, rather than "connecting" for ever (a network
    whose NAT blocks direct paths, with no relay reached: seen on a full-tunnel VPN, 2026-11-26). The app writes the kinds of
    address each side offered and the pair it chose to the phone's log (`adb logcat -s GomCall`), never the addresses.
12. The screens: one row of three in a call (speaker, end, mic; the web has no speaker choice), a minimise arrow, and the call
    bar across the top of the chat page (name, time, a crossed mic when muted, a red end button; a tap brings the call back).
    Someone calling can minimise while it rings; an incoming call comes full size. Rule 16 of `design/design-notes.md`,
    screens 21 to 35.
13. In the app later, if its calls run in the web view, the same bar inside it; outside the app, Android's own ongoing-call
    notification.

Not known yet: whether Chrome and Samsung Internet on Android play a web call through the earpiece or the speaker.

## 3. Cloudflare TURN, the facts (docs updated June to November 2026 [verified: developers.cloudflare.com])

- **Price:** $0.05 per GB, counting only what Cloudflare sends to the phones; the first 1,000 GB a month free, shared
  with their SFU. STUN (`stun.cloudflare.com`) free and unlimited. Self-serve billing is by card. A voice call relayed
  for an hour is about 30 to 50 MB in all [inferred: typical voice bit rates], so a family stays inside the free tier.
- **Setup:** a TURN key made in the dashboard (Realtime; the address still says `calls`). Its ID and API token are
  Gom's configuration, never the repository.
- **A login:** `POST https://rtc.live.cloudflare.com/v1/turn/keys/{key id}/credentials/generate-ice-servers` with
  `Authorization: Bearer {token}` and `{"ttl": seconds}`; 201 with `iceServers` (STUN, then TURN with `username` and
  `credential`). At most 48 hours. Revoke: `POST …/credentials/{username}/revoke`, 204. An optional
  `customIdentifier` shows in analytics.
- **Addresses:** `turn.cloudflare.com`, UDP 3478 and 443, TCP 3478 and 80, TLS 5349 and 443; the nearest location by
  anycast; IPv4 and IPv6 to the relay, relayed addresses IPv4 only. The docs contradict themselves about port 53 (said
  to time out in browsers); leave it out.
- **Limits per relayed connection:** about 50 to 100 Mbit/s and 5 to 10 thousand packets a second; no limit on length,
  logins renewed at least every 48 hours; issue at most about 500 logins a second and use more than half of those
  issued. Sending to many new addresses quickly looks like port scanning and is dropped.
- **Privacy:** "Cloudflare cannot access the contents of the media being relayed" (WebRTC encrypts it); it processes
  IP addresses, ports and session timing. No retention period is stated.
- **Usage:** GraphQL only (`callsTurnUsageAdaptiveGroups`: bytes out and in, connections, by location, key, tag),
  about 30 seconds behind; the token needs Account Analytics.

## 4. What H does (none of it goes in the repository)

1. Create the Cloudflare TURN key; give the key ID and API token to the server's configuration.
2. Create the Firebase project and the Android app in it; keep `google-services.json` and an FCM service-account key
   for the server's configuration and CI's secrets.
3. Set up the internal testing track in Play Console and add the family's addresses as testers.
4. Keep the upload key safe (outside the repository and its CI logs).
5. Deploy each server change, and test each app build on a Samsung phone with the checklist in section 6.

## 5. Proposed PR order (each discussed and shown before it is built)

1. **Design:** mockups in `design/` for the incoming call, the call screen, the call ended state and the chat rows;
   a rule in `design-notes.md`; the `/privacy` page.
2. **Server, calls** (2a, 2026-11-26): the `calls` table (migration `Calls`), the call states, the REST steps, socket frames,
   the chat line, the endings in section 2, the rate limit, the protocol version, the switch. Tests. **2b, web** (2026-11-26): the call
   lines drawn in the chat and the list (mockups 29 and 30), and the missed-call notification. Settled while designing 2a
   (H, 2026-11-26): the server decides which answer wins (no "select answer" step as in Matrix); each offer and answer carries
   all its addresses (no trickle ICE: one message, about a second slower to start); offers and answers stay in memory only;
   two people calling each other at once get the first call; a phone in a call says "still here" every 30 s and 90 s of
   silence ends it; a missed call notifies even in a muted chat; a person who cannot take calls (no app) is not offered the
   button, checked once phones register (PR 4).
3. **Server, relay:** Cloudflare logins (3 hours, tag, revoke), a fake Cloudflare in tests. Settled while designing it
   (H, 2026-11-26): a device asks `POST /api/calls/relay` for its login before it builds its offer or answer (they carry
   every address), so the login cannot come in the start or accept answer; the call it starts or answers claims it, a login
   no call claims within the ring time and 15 s is taken back, and all of a call's logins are taken back as it ends (a
   revoked login is dropped by Cloudflare "after a short delay" [stated: Cloudflare FAQ]). One key per environment, made by
   a script in the operations repository [stated: the operations session].
   Split in two (H, 2026-11-26): **4a** the app's registration (`PUT /api/push/fcm`, table `fcm_devices`), the FCM client
   with Google's login written by hand (no Google package), and the call pushes; **4b** messages and the owner's notices by
   FCM too. Settled while building 4a: a call is refused (`cannot_take_calls`) unless the person called has the app on a
   live session; a ringing call that ends without being answered on a device is pushed to its apps as `call-end` with how
   it ended, which is also their missed-call notification (Web Push still tells browsers). Settled while building 4b: a
   message wakes the app with `message`, its conversation and its id (high priority), a recall with `recall` (normal
   priority, only to apps at the "words" level), the owner's notices with `owner` and their kind (normal priority).
4. **Server, phones:** registering FCM tokens, sending with FCM's HTTP v1 API, the call and cancel pushes with their
   expiry, a fake FCM in tests.
5. **Android, the shell:** the project, the web view locked to Gom, the bridge, the session, FCM registration, CI
   build, notification channels. No calls yet. Split in two (H, 2026-11-26): **5a** the server and the page (FCM sent to
   the installation id, `GET /api/push/notice` for the app's words, Settings working through the app, the bridge's protocol 1);
   **5b** the `android/` project and its CI. Package ids `io.github.quaint666.gom` and `io.github.quaint666.gom.staging`;
   settings (base address, Firebase values) from Gradle properties outside the repository.
6. **Android, calls:** `core-telecom`, the WebRTC library, the phoneCall foreground service, the incoming full-screen
   screen, the call screen, ICE restart, renewing logins. Split in five (H, 2026-11-26): **6a** the page's call buttons ask the
   app (its hello says `calls: true`; the bridge's `{t: 'call'}`); **6b** the app's call engine (Telecom, the call service, the
   ringing notification, the call steps, a socket only during a call, the `call` and `call-end` pushes); **6c** the call screens
   (Compose, mockups 21 to 28, screen off at the ear); **6d** the sound (WebRTC, relay logins, restart after a network change,
   renewing logins, routes through Telecom); **6e** readiness (Samsung's sleeping apps, full-screen ringing turned off, a screen
   for no connection). Settled then: WebRTC `io.github.webrtc-sdk:android` (BSD; Google publishes no build, GetStream's last
   release was September 2025), `core-telecom` 1.1.0, OkHttp for the call socket; the call service is `phoneCall` while ringing
   and adds `microphone` once the person answers or calls (Android lets the microphone start only after a tap); the call
   screens are in the phone's language, as the app's notifications; the call protocol and timings are copied in Kotlin and a
   test checks them against the page's; with full-screen ringing turned off a call rings as a banner;
   `Calls:AppsTakeCalls` is turned on only once all five are in.
7. **Android, release:** the Samsung sleep guide, the update check, the Play listing and declarations.
8. **Web calls** (section 2b; H, 2026-11-26), before 6: **8a** the mockups (screens 31 to 35, the one-row buttons, the call
   bar); **8b** the server: who can take a call; **8c** the page: calling, ringing, answering, the call screen; **8d** the page:
   the call bar, the network-change restart, renewing relay logins. The test APK once planned here, to try calls in the app's web
   view, was dropped: the app's calls are native, as planned in section 1 (H, 2026-11-26).

## 6. Checklist for each app build (on a Samsung phone)

- Incoming call with the app closed, the screen locked, and the phone in "Deep sleeping apps" and out of it.
- Talking with the screen off and the phone at the ear; switching to speaker and to a Bluetooth headset.
- Moving from Wi-Fi to mobile data during a call.
- A call over 3 hours (or a lifetime set short for the test) keeps going after the login is renewed.
- Both phones on mobile data (the relay is used); the call connects and sounds right.
- Calling someone already in a call; declining; missing; hanging up while it rings; another device of the same person.
- A muted chat still rings; the "nothing" notification setting hides the name on the lock screen.

## 7. Not verified yet

- Calls on iOS 26 and Chromium's background-microphone issue may have changed since the sources above.
- How Android 15 and 16 treat the full-screen permission for an app from the Play Store's internal track (a calling
  app should keep it [inferred]).
- Which WebRTC library is current, and `core-telecom`'s exact minimum Android version in its 1.1.0 build files.
- How Samsung's sleeping lists affect FCM high-priority pushes.
- The relay traffic per call: to measure in Cloudflare's analytics once calls run.

## 8. Main sources

- iPhone: [webrtc-reprex](https://github.com/roumail/webrtc-reprex); WebKit Safari 26.x notes
  ([26.0](https://webkit.org/blog/17333/webkit-features-in-safari-26-0/),
  [26.6](https://webkit.org/blog/18178/webkit-features-for-safari-26-6/)).
- Android browser: [Chromium issue 374374232](https://issues.chromium.org/issues/374374232);
  [incoming-call notifications](https://whatpwacando.today/incoming-call-notifications/).
- Android native: [CallsManager](https://developer.android.com/reference/androidx/core/telecom/CallsManager);
  [Jetpack stable releases](https://developer.android.com/jetpack/androidx/versions/stable-channel);
  [foreground service changes](https://developer.android.com/develop/background-work/services/fgs/changes);
  [full-screen intent limits](https://source.android.com/docs/core/permissions/fsi-limits);
  [twilio_voice #297](https://github.com/cybex-dev/twilio_voice/issues/297);
  [GetStream webrtc-android](https://github.com/GetStream/webrtc-android).
- FCM: [message priority](https://firebase.google.com/docs/cloud-messaging/android-message-priority.md.txt).
- Cloudflare: [TURN](https://developers.cloudflare.com/realtime/turn/),
  [FAQ](https://developers.cloudflare.com/realtime/turn/faq/),
  [credentials](https://developers.cloudflare.com/realtime/turn/generate-credentials/),
  [pricing](https://developers.cloudflare.com/realtime/pricing/),
  [analytics](https://developers.cloudflare.com/realtime/turn/analytics/).
- Play: [testing requirements](https://support.google.com/googleplay/android-developer/answer/14151465?hl=en);
  [developer verification](https://android-developers.googleblog.com/2026/06/android-developer-verification.html).
- Ring times: [WhatsApp (third party)](https://www.itgeared.com/how-long-does-whatsapp-ring-for/);
  [Telegram request](https://bugs.telegram.org/c/762/10).
