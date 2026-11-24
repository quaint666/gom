# Gom design notes

Written 2026-11-24. This is the visual language the mockups use, and the rules the screens assume. It builds on the Peekbox design notes (same fonts, same sage and cream, same touch sizes). Decisions and their reasons are in `docs/decisions.md`.

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
- No emoji in the interface. Icons are simple stroked line icons.
- Focus ring: 3 px, offset 2 px, `#3F6B57` in light mode and `#9EC3A5` in dark mode, for the keyboard only, never removed.

## Layout
- **Desktop:** a fixed 340 px conversation list on the left, a flexible thread on the right. Nothing open shows a quiet "Chọn một cuộc trò chuyện" pane.
- **Phone:** one pane at a time. A tap on a conversation opens the thread, and the back button returns to the list. Add friend, group info and add members are full screens with a back button; on desktop, add friend is a dialog over the chat.
- **List row:** avatar, name, last message, time, and an unread badge. An unread row is bold with a green time. A muted conversation has a crossed bell and a grey badge. The active row has the chosen tint and a left bar.
- **Thread:** sender name and avatar only in groups (name on the first message of a run, avatar on the last); a day pill; a "3 tin nhắn mới" divider; under your last message "Đã xem" in a 1:1 chat and "Đã xem bởi n" in a group.
- **Composer:** attach, text field with an emoji button, send. A reply bar above it ("Đang trả lời ...") with a close button.

## Rules the screens assume
1. **Friends.** Add by exact Gom ID (no list of matches), by a link, or by a QR code. A request must be accepted. Signing up through an invite makes you friends with the inviter at once. Phone numbers are not used in the first version.
2. **Invites.** Only the owner invites, with a single-use link that expires. An invite can also add the new member to a group.
3. **Groups.** Any member can create a group and add their own friends. Members who are not friends see each other's name and avatar (not the ID) and need a friend request before a one-to-one message. "Thêm thành viên" offers only your own friends. The creator is the leader (trưởng nhóm) and names deputies (phó nhóm). Leader and deputies remove members; a deputy cannot remove the leader or another deputy. Only the leader names deputies, hands leadership over, or disbands. The leader cannot leave without handing over. Removing, handing over and disbanding ask first, naming the person. A removed member is out of the group as if they had left: it leaves their list, and a member can add them again to see what comes after.
4. **Read receipts** are a per-member read position in each conversation, with an opt-out that works both ways. Typing indicators and online status are not in the first version.
5. **Sending.** A message waits with a clock while the connection is down and goes out when it returns; a failed message offers "Gửi lại" (the retry cannot duplicate it) or "Xóa".
6. **Recall.** "Thu hồi" replaces a message with a dashed placeholder for everyone. No time limit is drawn.
7. **Ops rooms** appear in a staff member's list with an "OPS" chip and a rounded-square icon, are muted for push by default, and are read-only: alerts come from the shop as a bot. Alerts carry no customer details (order number, item count, total and payment method only).
8. **Avatars are initials** in the first version. Group links, avatar images and profile customization come later.
9. **Loading** uses skeleton rows, not a spinner; with "reduce motion" the shimmer stops.
10. **Errors** say what is wrong in words, with an icon, never colour alone. The sign-in error does not say whether the ID exists.
11. **Accounts.** Sign-in is Gom ID plus password (at least 10 characters). A Gom ID is lowercase letters, digits and underscore, 3 to 20 characters, and cannot change; the display name is a nickname that defaults to the ID. Only the owner resets a password. An invite lasts 7 days. Each person can see and end their own other sessions.

## Not decided yet
- The desktop version of the message menu (right-click or hover), and the block control (it would sit in a member's menu).
- Hover, focus and pressed looks per control: they follow the Peekbox interaction rules and are not drawn here.

## Placeholders in the mockups
Names, IDs, order numbers, totals and the QR code are samples. The photo in the image screens is a gradient stand-in.
