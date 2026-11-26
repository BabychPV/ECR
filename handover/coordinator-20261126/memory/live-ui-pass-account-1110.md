---
name: live-ui-pass-account-1110
description: "10.11: the coordinator's live UI pass uses the local account the human typed in chat on 08.11 16:34Z, not the Windows sign-in"
metadata:
  node_type: memory
  type: feedback
  originSessionId: e3c77d43-fb3b-47d3-b246-67858348a93f
  modified: 2026-11-10T12:01:15.046Z
---

On 10.11 the human said «Я тобі давав логін та пароль … В чаті».
- The account is the one in the human's chat message of 08.11 16:34Z, the same as the HSE401 cred file.
- It has Security and Document rights on live. WORK_MACHINE\Babyc (Windows sign-in) has no Document.View.
- Never repeat the password or name the account in replies.

**Why:** on 10.11 I wrongly told the human I had no account. The credentials were already in the chat, and it cost a round trip.

⚠ 10.11 pm: the transcript line is JSON-escaped. The password contains an escaped quote character followed by more characters, so a naive grep that stops at `\"` truncates it and gives ECR-AUTH-0401. Grep with `.{0,60}` after the phrase and decode the JSON escapes.

**How to apply:** for live UI passes on localhost:6094, sign in through the login form with these credentials. Search the transcript, or read them inside a script, if the summary lost them. Related: [[human-projects-page-and-live-ui-test-1108]], [[human-hse401-cred-file-standing-1105]].
