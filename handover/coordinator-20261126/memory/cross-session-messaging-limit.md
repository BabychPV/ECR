---
name: cross-session-messaging-limit
description: "app pauses SendMessage after 10 letters without a separate human message; a mid-turn message does NOT reset it"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 483c17f2-bcf9-4811-94f3-a71c82bb4cb4
  modified: 2026-11-11T21:00:02.853Z
---

On 21.10 the human lifted their own rule (group letters), but the APP still enforces it: after 10 SendMessage calls without a human message in this session, delivery pauses (confirmed 11.11). Quiz answers don't reset it, and neither does a message the human sends mid-turn («Так» on 11.11); only a separate human turn does («Далі»).

**Why:** on 11.11, at ~21:00Z, F13 letters and window prep were held for ~30 min.

**How to apply:** group letters (one per session per event, several items per letter); skip pure acks. Near 8–9 letters, tell the human in the reply that one separate message will be needed soon. Related: [[token-saving-loop]].

✎ 16.11: є дублікати імен сесій (Remote Control з іншого ПК, offline). SendMessage на голе ім'я «Аудит-2» пішов у ОФЛАЙН-дублікат і мовчки став у чергу. Правило: сесіям писати на local_… id (AN local_a44029c0…, PE local_7e7ad761…, AU local_d01ea2c3…, AA local_c3f551b1…), а не на ім'я; «undelivered за 20 с» ще не означає, що лист не дійшов — перевірити list_events.
