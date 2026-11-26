# Releases

Gom is released as a whole: the server, the pages and the Android app share one number. The list of every release, with
each pull request in it, is on GitHub's Releases page; this file says how a release is made and what each one brought.

## The number

Semantic versions, `MAJOR.MINOR.PATCH`, kept in the `VERSION` file at the root of the repository. It is the only place the
number is written: the server reads it through the build and shows it in Settings ("Phiên bản v1.0.0"), and the Android
app takes it as its version name.

- **Minor** (1.1.0): anything new for the people using Gom.
- **Patch** (1.0.1): fixes only.
- **Major** (2.0.0): something people or operations must act on, such as an app and a server that can no longer work
  together.
- Changes to the build, CI or docs alone do not need a release; they go out with the next one.

## Making a release

1. Each pull request carries one type label: `enhancement`, `bug`, `ci` or `documentation`. GitHub sorts a release's
   notes by it (`.github/release.yml`).
2. When what is on `main` should go to the family, a pull request named "Release 1.1.0" changes `VERSION` and adds the
   release's entry under "What's new" below.
3. That commit is deployed to staging, checked, and promoted to production.
4. The commit production runs is tagged `v1.1.0`, and the release is published on GitHub with its generated notes. A tag
   is public and permanent, so it is made only when the owner says so.

Between releases, staging may run newer code than its number says: the number moves only with step 2.

## What's new

### 1.0.0 (26 November 2026)

The first release: everything from the first pull request to #150, as production ran it that day.

**New**
- Accounts: joining by invite, or by asking and being approved by the owner; friends and blocking; profile pictures;
  Vietnamese, or English as each person's choice; deleting your own account.
- Chat: one-to-one and group conversations with live delivery and offline sending; replies, forwarding, pins, reactions,
  editing, taking a message back, delete for me, read receipts.
- Groups: roles, removal, approval to join, bans.
- Media: pictures and files; videos made smaller on the sending device (up to 45 MB); moving pictures (GIFs); emoji and 432
  stickers in eleven packs.
- Search as you type, in one conversation or all of them.
- Each conversation's menu: pin to the top, mark unread, hide, delete for yourself; mute for a while or until turned back on.
- Sixteen colours, fifteen backgrounds and pure black, per person or per conversation.
- Notifications in browsers (Web Push), at three levels: the words, only the name, or nothing.
- Settings as a menu with an address per part; open pages move to a new version by themselves, keeping what was typed.
- For the owner: a storage page that trims old pictures, files and messages; requests to join; contact messages from the
  privacy page.
- The groundwork for voice calls (not usable yet): the server's call steps, call lines in the chat, relay logins and phone
  wake-ups.
- The Android app (not on Google Play yet): Gom in a web view, with notifications through Firebase Cloud Messaging.

**Fixes**
- The image build's start-up crash: the database connection no longer tries Kerberos encryption (#148).
