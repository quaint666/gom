# Gom design notes

Written 2026-11-24; the rules brought up to date on 2026-11-25. This is the visual language the mockups use, and the rules the screens assume. The app has moved on from the mockups in places (stickers, profile pictures, the owner's storage page are not drawn); where they differ, these rules and `docs/decisions.md` win. It builds on the Peekbox design notes (same fonts, same sage and cream, same touch sizes). Decisions and their reasons are in `docs/decisions.md`.

## Look
Calm and quiet: a chat is mostly text and photos, so the colour is one sage green on cream, and everything else is neutral. Phone and desktop matter equally. The interface is Vietnamese only. Light and dark follow the system setting, and there is a manual toggle in the list header that the browser remembers.

## Colour: light mode
| Role | Hex |
|---|---|
| Page background | `#F7F5EF` |
| Surface (header, list, composer, incoming bubble) | `#FFFFFF` |
| Text | `#1F2A24` |
| Muted text | `#4A5650` |
| Border | `#DAD6C9` |
| Primary (buttons, send, badges, links) | `#3F6B57` |
| Chosen row and unread divider | `#E3EFE7`, text `#25513E`, 3 px left bar `#3F6B57` |
| Your bubble | `#DCEBE1`, text `#1F2A24` |
| Muted badge | `#6B756F` |
| Avatars (sage / neutral) | `#E3EFE7` text `#25513E` / `#E8E5DC`, `#DFDDD4`, `#EFEDE6` text `#3A3F3B` |
| Error | background `#FBE4E0`, text `#7A1F14`, border and red buttons `#A32A1E` |
| Warning (reconnecting) | background `#FFF1CC`, text `#7A4F00`, border `#E3C77A` |

## Colour: dark mode
| Role | Hex |
|---|---|
| Page background | `#121513` |
| Surface | `#1B201C` |
| Text | `#EEF0EA` |
| Muted text | `#AEB5AC` |
| Border | `#343B36` |
| Primary on dark (text, links, focus ring) | `#9EC3A5`; filled buttons stay `#3F6B57` with white text |
| Chosen row and unread divider | `#1D2B24`, text `#BFE3CF` |
| Your bubble | `#24382D`, text `#EEF0EA` |
| Muted badge | `#4A524C` |
| Avatars (sage / neutral) | `#24382D` text `#BFE3CF` / `#262B27`, `#2D332E`, `#222723` text `#D9DDD6` |
| Error | background `#3A1F1B`, text and border `#F4B8AF` |
| Warning (reconnecting) | background `#3A3118`, text `#EBD3A6`, border `#6B5A2A` |

No pure black anywhere. The image viewer is `#0E100F` in both themes, on purpose. A dim layer behind dialogs and menus is `rgba(20,26,22,.45)` in light and `rgba(0,0,0,.6)` in dark.

Avatars and sender names are neutral on purpose (only the first avatar tint is sage): the amber, lilac and rose of the first draft were dropped so the colour stays one hue. In a busy group, names then differ by avatar initials, not by colour.

## Contrast (calculated)
Text pairs on their own backgrounds are at least 4.77 to 1 (white on the light muted badge); body text is 10.9 to 14.8 to 1. Muted text on a surface is 7.7 to 1 (light) and 7.9 to 1 (dark); the timestamp on your bubble is 6.2 to 1 and 6.0 to 1. Error text on its background is 8.5 to 1 (light) and 8.9 to 1 (dark); warning text is 6.4 to 1 and 8.8 to 1. Not tested on real screens or with a screen reader.

## Type
Be Vietnam Pro (400, 600, 700), self-hosted from `design/source/fonts/`. Body 15 px; message text 15 px; timestamps and notes 11 to 13 px; screen titles 17 to 22 px; the sign-in and invite headings are Bold, other headings SemiBold.

## Shape and touches
- Buttons and inputs: at least 44 px tall (48 px for the main button).
- Bubbles: 16 px radius; inside a run of messages from one sender the corner on the sender's side is 6 px. No tails.
- Cards and dialogs: 12 to 16 px radius, 1 px border.
- A 3 px primary bar runs along the top of each header, as in Peekbox.
- No emoji in the interface itself: icons are simple stroked line icons. Emoji and stickers are things people send, not part of the interface.
- Focus ring: 3 px, offset 2 px, `#3F6B57` in light mode and `#9EC3A5` in dark mode, for the keyboard only, never removed.

## Layout
- **Desktop:** a fixed 340 px conversation list on the left, a flexible thread on the right. Nothing open shows a quiet "Chọn một cuộc trò chuyện" pane.
- **Phone:** one pane at a time. A tap on a conversation opens the thread, and the back button returns to the list. Add friend, group info and add members are full screens with a back button; on desktop, add friend is a dialog over the chat.
- **List row:** avatar, name, last message, time, and an unread badge. An unread row is bold with a green time. A muted conversation has a crossed bell and a grey badge. A pinned one has a small pin before its name and sits at the top; one marked unread has a dot instead of a number. The active row has the chosen tint and a left bar, and a "⋯" button in the time's place for its menu; another row shows it only while a mouse is over it, so the list is not a column of dots.
- **Thread:** sender name and avatar only in groups (name on the first message of a run, avatar on the last); a day pill; a "3 tin nhắn mới" divider; under your last message "Đã xem" in a 1:1 chat and "Đã xem bởi n" in a group.
- **Composer:** attach, the text field with the emoji and sticker button inside its rounded box at the end, send. The panel opens where the keyboard would be and closes on a press anywhere outside it; stickers come in named packs, one pack shown at a time. A reply bar above it ("Đang trả lời ...") with a close button. A message of one to three emoji only is drawn large, without a bubble; a sticker is drawn without a bubble.

## Rules the screens assume
1. **Friends.** Add by exact Gom ID (no list of matches), by a link, or by a QR code. A request must be accepted. Signing up through an invite makes you friends with the inviter at once. Phone numbers are not used in the first version.
2. **Invites.** Only the owner invites, with a link that works for 1, 7 or 30 days and lets in 1, 10 or any number of people; the owner can revoke it. Joining does not add anyone to a group.
3. **Groups.** Any member can create a group and add their own friends. Members who are not friends see each other's name and avatar (not the ID) and need a friend request before a one-to-one message. "Thêm thành viên" offers only your own friends. The creator is the leader (trưởng nhóm) and names deputies (phó nhóm). Leader and deputies remove members; a deputy cannot remove the leader or another deputy. Only the leader names deputies, hands leadership over, or disbands. The leader cannot leave without handing over. Removing, handing over and disbanding ask first, naming the person. A removed member is out of the group as if they had left: it leaves their list, and a member can add them again to see what comes after.
4. **Read receipts** are a per-member read position in each conversation, with an opt-out that works both ways. Typing indicators and online status are not in the first version.
5. **Sending.** A message waits with a clock while the connection is down and goes out when it returns; a failed message offers "Gửi lại" (the retry cannot duplicate it) or "Xóa".
6. **Recall.** "Thu hồi" replaces a message with a dashed placeholder for everyone. No time limit is drawn.
7. **Alerts from the shop (not built).** The plan changed from the drawn "Ops room": a secret link posts plain-text alerts into an ordinary group as a labelled bot, beside the shop's own email and the server's own alerts, never instead of them. Alerts still carry no customer details.
8. **Avatars.** A person may set a profile picture (cut to a square from its middle); without one, initials. Groups have no picture.
9. **Loading** shows nothing in place of the list until it arrives, and a short "Đang tải…" where a panel waits: no skeleton rows (dropped with the motion rules) and no spinner.
10. **Errors** say what is wrong in words, with an icon, never colour alone. The sign-in error does not say whether the ID exists.
11. **Accounts.** Sign-in is Gom ID plus password (at least 10 characters). A Gom ID is lowercase letters, digits and underscore, 3 to 20 characters, and cannot change; the display name is a nickname that defaults to the ID. Only the owner resets a password (a one-use link for 24 hours). Each person can see and end their own other sessions.
12. **Deleting your account.** Anyone but the owner can, from Settings, by typing their Gom ID. What they said stays, under "Tài khoản đã xóa"; their friendships, group places (a leader's role passes on) and sessions go. A one-to-one chat with them closes. The owner can also lock and unlock an account.
13. **Notifications** are per device, switched on in Settings, never on by default. A notification says who wrote and where, or just "new message" at the quieter level, never the words. A muted conversation is silent; mute lasts a while or until turned back on.
14. **The conversation menu** ("⋯" on a row, a right-click, or a finger held on a row for a second) is the person's own: pin to the top (five at most), mark read or unread (a mark only; the read position others may see never goes back), mute, make a group with this friend (one-to-one chats), hide until something new is said ("Hoàn tác" offered for a few seconds after; a search above the list still finds it under "Đã ẩn", and opening it, from there or anywhere else, brings it back), and delete for me (asked first; everything so far is gone for them alone). No categories, no "Khác" tab, no report: not needed at family size.
15. **The owner's storage page** shows the disk, the database, and sizes by group, by sender, and for one-to-one chats only together (never who talks to whom). Trimming old pictures and files, or old messages, takes a preview and the word "Delete" typed; it is gone for good, backups included. A trimmed picture or file leaves "Ảnh/Tệp không còn khả dụng" in its message; each conversation that lost something gets a line saying so, naming nobody.

## Not drawn
- The message menu opens on a right-click on desktop and a long press on a phone; the conversation menu on a row's "⋯", a right-click, or a one-second press; blocking is on the Friends page. None of these is drawn.
- Hover, focus and pressed looks per control: they follow the Peekbox interaction rules and are not drawn here.

## Placeholders in the mockups
Names, IDs, order numbers, totals and the QR code are samples. The photo in the image screens is a gradient stand-in.
