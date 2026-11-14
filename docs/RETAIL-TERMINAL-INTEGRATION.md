# Retail EGM integration

This guide is for the team building the **EGM side**: the terminal software in or beside each cabinet, which handles the credit meter, the bill validator, AFT and the cash-out button.

There are two APIs:

- The **EGM API** is hosted by **you**. It holds the balance. The Game Engine API calls it to read the balance and to debit bets and credit wins.
- The **Game Engine API** is hosted by **us**. It runs the game. You call it when money arrives at or leaves the cabinet.

---

## 1. How it works

```text
          EGM side (you)                                              Game Engine API (us)
 ┌─────────────────────────────────┐                        ┌──────────────────────────────────┐
 │ Browser: <GameUrl>?egmid=EGM1 ──┼── plays the game ─────▶│ runs rounds, decides results     │
 │                                 │                        │                                  │
 │ EGM API  POST /wallet  ◀────────┼── Balance / Debit / ───│ every balance read, bet and win  │
 │  (holds the credit meter)  ─────┼── Credit / Rollback ──▶│                                  │
 │                                 │                        │                                  │
 │ Terminal ──────────────────────▶│ POST /api/retail/terminal  Bill_Inserted / AFT_Deposit /  │
 │  (bills, AFT, cash-out button)  │                            Cashout / Balance              │
 └─────────────────────────────────┘                        └──────────────────────────────────┘
```

- **Your meter is the balance.** The Game Engine API keeps no balance of its own. It asks your EGM API every time.
- **Bets and wins arrive as calls to your EGM API.** You apply them to the meter and return the new balance.
- **Notes, AFT and cash-outs are reported to us.** That way the game on the cabinet's screen updates straight away. For a cash-out, we also confirm that no bet is still in play.

---

## 2. How debit and credit work

There is **one** endpoint on your side. Every call carries the EGM id, a `debit` and a `credit`. You apply both to the meter and return the balance afterwards.

| What happens in the game | Call to your EGM API | Meter |
|---|---|---|
| The game opens, reconnects, or needs to show the balance | `debit: 0, credit: 0` (`type: "Balance"`) | No change. You return the balance. |
| The player places a bet | `debit: <stake>, credit: 0` (`type: "Debit"`) | − stake. If the meter is too low, refuse with `402 INSUFFICIENT_FUNDS`. |
| The player wins (cashes out in time) | `debit: 0, credit: <win>` (`type: "Credit"`) | + win. **The win includes the stake** (a stake of 10 cashed out at 1.10x gives a credit of 11). |
| The player loses (the round crashes first) | **No call** | The stake is already gone. |
| A bet is refunded (the round closed while the bet was being placed) | `debit: 0, credit: <stake>` (`type: "Rollback"`, `relatedTransactionId` = the bet's id) | + stake. If you never booked that bet, refund nothing. |

The rules for every call:

1. **`transactionId` is an idempotency key.** We send the same id again when a call times out. If you have already booked it, **don't book it again**: return HTTP 200 with the current balance and `duplicate: true`. If the id comes back with a *different* debit or credit, refuse it with `409 REFERENCE_CONFLICT`.
2. **Apply the call atomically.** Check the meter, change it and record the transaction as one operation. Two bets can arrive at the same moment, one for each betting slot.
3. **Answer within 5 seconds.** After a timeout we retry up to 3 times with the same `transactionId`. If a bet still fails, it isn't placed. If a win still fails, the player is told to try again and we log it for reconciliation.
4. **Never pay out from a game call.** Debits and credits only move the meter. Money leaves the cabinet only through a cash-out (section 6).

The ids we send look like `BET_<32 hex>`, `WIN_<32 hex>` and `ROLLBACK_BET_<32 hex>`. A bet and its win share the same hex part, so you can match them. Treat them as opaque strings of up to 100 characters.

---

## 3. All endpoints

| # | Hosted by | Called by | Endpoint | Purpose |
|---|---|---|---|---|
| A | **EGM API (you)** | Game Engine API | `POST {EGM API}/wallet` | Balance, debit (bet), credit (win), rollback (refund) |
| B | Game Engine API | The game, in the cabinet's browser | `POST /api/retail/session` | Turns `?egmid=EGM1` into a game session |
| C | Game Engine API | Terminal | `POST /api/retail/terminal` | `Bill_Inserted`, `AFT_Deposit`, `Cashout`, `Balance` |

All bodies are JSON (`Content-Type: application/json`). Amounts are **currency units with up to 2 decimals** (`250.50` means R250.50), **not cents**. Field names are camelCase.

---

## 4. Endpoint A: EGM API, `POST {EGM API}/wallet` (you build this)

**Called by:** the Game Engine API.
**Authentication:** we send the header `X-Api-Key: <key you issue us>`. Refuse anything else with 401.
**Path:** `wallet` by default. Tell us if yours is different (for example `/api/egm/wallet`).

### Request

| Field | Type | Always sent | Meaning |
|---|---|---|---|
| `egmId` | string | yes | The cabinet, e.g. `"EGM1"`. |
| `debit` | number | yes | The amount to take off the meter. `0` if none. |
| `credit` | number | yes | The amount to add to the meter. `0` if none. |
| `type` | string | yes | `"Balance"`, `"Debit"`, `"Credit"` or `"Rollback"`. This labels the call; `debit` and `credit` carry the money. |
| `transactionId` | string \| null | yes | The idempotency key. `null` for `Balance`. |
| `roundId` | string \| null | yes | The game round. `null` for `Balance`. |
| `gameId` | string \| null | yes | e.g. `"feeding-frenzy"`. |
| `currency` | string | yes | e.g. `"ZAR"`. |
| `relatedTransactionId` | string \| null | yes | For `Rollback`, the `transactionId` of the bet being refunded. Otherwise `null`. |

### Response, HTTP 200

| Field | Type | Meaning |
|---|---|---|
| `egmId` | string | Echo of the request. |
| `balance` | number | **Required.** The meter after this call. |
| `transactionId` | string \| null | Echo of the request. |
| `duplicate` | boolean | `true` when this `transactionId` had already been booked and nothing changed now. |

### Errors

Return a non-200 status with `{ "error": "<CODE>", "message": "<text>" }`:

| HTTP | `error` | When | What we do |
|---|---|---|---|
| 401 | `UNAUTHORIZED` | The `X-Api-Key` is wrong | Fail the call. |
| 402 | `INSUFFICIENT_FUNDS` | The debit is more than the meter | The bet is refused and the player sees "Insufficient credits". |
| 404 | `EGM_NOT_FOUND` | Unknown `egmId` | Fail the call. |
| 409 | `REFERENCE_CONFLICT` | `transactionId` reused with different amounts | Fail the call and log it. |
| 423 | `EGM_LOCKED` | A debit arrived during a cash-out (section 6) | The bet is refused. |
| 5xx, timeout | — | Your side failed | **Retry with the same `transactionId`.** |

### Examples

**Balance:** the game opens, or the balance is shown.

```json
// request
{ "egmId": "EGM1", "debit": 0, "credit": 0, "type": "Balance", "transactionId": null,
  "roundId": null, "gameId": null, "currency": "ZAR", "relatedTransactionId": null }
// 200
{ "egmId": "EGM1", "balance": 250.00, "transactionId": null, "duplicate": false }
```

**Debit:** the player bets R10.

```json
// request
{ "egmId": "EGM1", "debit": 10, "credit": 0, "type": "Debit",
  "transactionId": "BET_e5085f8a4d304082a0330e5e9237eee1", "roundId": "499c1a325f3f49bc8f83e6fc9aa269c7",
  "gameId": "feeding-frenzy", "currency": "ZAR", "relatedTransactionId": null }
// 200
{ "egmId": "EGM1", "balance": 240.00, "transactionId": "BET_e5085f8a4d304082a0330e5e9237eee1", "duplicate": false }
```

**Credit:** the player cashes out the R10 bet at 1.10x, so the win is R11.

```json
// request
{ "egmId": "EGM1", "debit": 0, "credit": 11, "type": "Credit",
  "transactionId": "WIN_e5085f8a4d304082a0330e5e9237eee1", "roundId": "499c1a325f3f49bc8f83e6fc9aa269c7",
  "gameId": "feeding-frenzy", "currency": "ZAR", "relatedTransactionId": null }
// 200
{ "egmId": "EGM1", "balance": 251.00, "transactionId": "WIN_e5085f8a4d304082a0330e5e9237eee1", "duplicate": false }
```

**Rollback:** the bet is refunded.

```json
// request
{ "egmId": "EGM1", "debit": 0, "credit": 10, "type": "Rollback",
  "transactionId": "ROLLBACK_BET_e5085f8a4d304082a0330e5e9237eee1", "roundId": "499c1a325f3f49bc8f83e6fc9aa269c7",
  "gameId": "feeding-frenzy", "currency": "ZAR", "relatedTransactionId": "BET_e5085f8a4d304082a0330e5e9237eee1" }
// 200 (if the bet was never booked, refund nothing and still answer 200 with the balance)
{ "egmId": "EGM1", "balance": 250.00, "transactionId": "ROLLBACK_BET_e5085f8a4d304082a0330e5e9237eee1", "duplicate": false }
```

**Repeat:** the debit above arrives again after a timeout.

```json
// 200, nothing booked
{ "egmId": "EGM1", "balance": 240.00, "transactionId": "BET_e5085f8a4d304082a0330e5e9237eee1", "duplicate": true }
```

**Insufficient funds:** a bet of R10 with R0 on the meter.

```json
// 402
{ "error": "INSUFFICIENT_FUNDS", "message": "Insufficient credits", "balance": 0.00 }
```

---

## 5. Endpoint B: `POST /api/retail/session` (the game calls this)

You don't call this endpoint. The game calls it when the cabinet's browser opens `<GameUrl>?egmid=EGM1`. It is listed here so you can see the whole flow. To launch the game, open that URL in kiosk mode and keep it open.

**Request**

| Field | Type | Required | Meaning |
|---|---|---|---|
| `egmId` | string | yes | Taken from `?egmid=` in the launch URL. |
| `gameId` | string | no | Defaults to `"feeding-frenzy"`. |

**Response, HTTP 200**

| Field | Type | Meaning |
|---|---|---|
| `token` | string | The game session (JWT), used for the game's own connection. |
| `egmId`, `name` | string | The registered EGM. |
| `currency` | string | e.g. `"ZAR"`. |
| `balance` | number \| null | Read from your EGM API. `null` if it didn't answer. |
| `gameId` | string | The game being played. |

```json
// request
{ "egmId": "EGM1" }
// 200
{ "token": "eyJhbGciOiJIUzI1NiIs…", "egmId": "EGM1", "name": "EGM 1", "currency": "ZAR", "balance": 250.00, "gameId": "feeding-frenzy" }
```

**Errors:** `404 EGM_NOT_FOUND` (not registered with us), `403 EGM_ADDRESS_NOT_ALLOWED` (opened from an IP address the EGM isn't registered for), `404 GAME_NOT_FOUND`.

---

## 6. Endpoint C: `POST /api/retail/terminal` (you call this)

**Authentication:** the header `X-Terminal-Key: <key we issue you>`.

### Request

| Field | Type | Required | Meaning |
|---|---|---|---|
| `egmId` | string | yes | The cabinet. |
| `event` | string | yes | `"Bill_Inserted"`, `"AFT_Deposit"`, `"Cashout"` or `"Balance"`. |
| `amount` | number | for `Bill_Inserted` and `AFT_Deposit` | The amount that arrived, more than 0. |
| `transactionId` | string | for everything except `Balance` | Your id for the event, up to 100 characters. Used in our logs for reconciliation. |
| `balance` | number | optional, for `Bill_Inserted` and `AFT_Deposit` | Your meter **after** the deposit. If you send it, we show it without calling you back. If you leave it out, we read it from your EGM API. |

### Response, HTTP 200

| Field | Type | Meaning |
|---|---|---|
| `egmId`, `event`, `transactionId` | string | Echo of the request. |
| `amount` | number | The deposit for `Bill_Inserted` and `AFT_Deposit`. For `Cashout`, **the game's balance: the amount to pay out**. `0` for `Balance`. |
| `balance` | number | The balance as the game sees it. |
| `currency` | string | e.g. `"ZAR"`. |
| `timestamp` | string | The time we handled it (UTC, ISO 8601). |

### Errors

| HTTP | `error` | What you do |
|---|---|---|
| 400 | `INVALID_EVENT`, `INVALID_AMOUNT`, `INVALID_REFERENCE` | A bug in the request. Log it and raise an alert. |
| 401 | `UNAUTHORIZED` | Wrong or missing `X-Terminal-Key`. |
| 404 | `EGM_NOT_FOUND` | The EGM isn't registered with us. |
| 409 | `ROUND_IN_PROGRESS` | Cashout only. A bet is in play. Retry every 1–2 seconds. |
| 502 | `EGM_WALLET_UNAVAILABLE` | We couldn't read your EGM API. Check that it's up, then retry. |
| 5xx, timeout | — | Retry. |

### 6.1 Bill_Inserted

Send this **after** the note is stacked **and** you have added it to your meter.

```json
// request
{ "egmId": "EGM1", "event": "Bill_Inserted", "amount": 200, "transactionId": "EGM1-BILL-000123", "balance": 200.00 }
// 200
{ "egmId": "EGM1", "event": "Bill_Inserted", "transactionId": "EGM1-BILL-000123", "amount": 200.00,
  "balance": 200.00, "currency": "ZAR", "timestamp": "2026-11-10T14:35:05.0894700Z" }
```

The game shows R200.00 straight away with a "Credits added" message. If this call fails, the money is still safe on your meter; the game picks the balance up on its next read. You can retry the call, but you don't have to.

### 6.2 AFT_Deposit

The same as Bill_Inserted, sent **after** the AFT transfer has completed and the funds are on your meter.

```json
// request (no balance sent: we read it from your EGM API)
{ "egmId": "EGM1", "event": "AFT_Deposit", "amount": 50, "transactionId": "EGM1-AFT-7F3A91" }
// 200
{ "egmId": "EGM1", "event": "AFT_Deposit", "transactionId": "EGM1-AFT-7F3A91", "amount": 50,
  "balance": 250.00, "currency": "ZAR", "timestamp": "2026-11-10T14:35:05.1187704Z" }
```

### 6.3 Cashout: the procedure

When the player presses cash out:

1. **Lock the meter against debits.** From now until step 5, answer any `Debit` to your EGM API with `423 EGM_LOCKED`. **Keep accepting `Credit` and `Rollback`**, because a bet that is still riding must be able to pay out.
2. **Call `Cashout`.**
   ```json
   { "egmId": "EGM1", "event": "Cashout", "transactionId": "EGM1-CASHOUT-000045" }
   ```
3. **If you get `409 ROUND_IN_PROGRESS`,** a bet is still in play. Show "Finishing game…" and repeat step 2 every 1–2 seconds. A round lasts seconds.
4. **On 200,** `amount` is the game's balance:
   ```json
   { "egmId": "EGM1", "event": "Cashout", "transactionId": "EGM1-CASHOUT-000045", "amount": 251.00,
     "balance": 251.00, "currency": "ZAR", "timestamp": "2026-11-10T14:35:31.8360201Z" }
   ```
   The game now shows R0.00.
5. **Pay out your meter** (print a ticket, AFT off, or hopper), set it to 0, record the payout, and unlock. Your meter and `amount` should match. If they don't, pay your meter (it is the balance) and log the difference for us.

If the Cashout call itself fails (5xx or timeout), retry it. It changes nothing on our side, so repeating it is always safe.

### 6.4 Balance

This returns the balance as the game sees it. It always matches your meter, because we read it from your EGM API. Use it as a health check of both directions.

```json
// request
{ "egmId": "EGM1", "event": "Balance" }
// 200
{ "egmId": "EGM1", "event": "Balance", "transactionId": null, "amount": 0, "balance": 0.00,
  "currency": "ZAR", "timestamp": "2026-11-10T14:35:33.5478834Z" }
```

---

## 7. One full session

| Step | Who calls | Call | Meter |
|---|---|---|---|
| 1 | Game → us | `POST /api/retail/session { egmId: "EGM1" }` | |
| 2 | Us → you | `/wallet` Balance | 0.00 |
| 3 | You | A R200 note is stacked; you add it to the meter | 200.00 |
| 4 | You → us | `/api/retail/terminal` Bill_Inserted 200, balance 200 | 200.00 (shown in the game) |
| 5 | Us → you | `/wallet` Debit 10 (`BET_…`) | 190.00 |
| 6 | You → us | `/api/retail/terminal` Cashout → **409** ROUND_IN_PROGRESS | 190.00 (debits locked) |
| 7 | Us → you | `/wallet` Credit 11 (`WIN_…`), allowed while locked | 201.00 |
| 8 | You → us | `/api/retail/terminal` Cashout → **200**, amount 201 | 201.00 |
| 9 | You | Pay R201.00, set the meter to 0, unlock | 0.00 |
| 10 | Us → you | `/wallet` Debit 10 on the next bet → **402** INSUFFICIENT_FUNDS | 0.00 |

---

## 8. Test sequence

Against our staging API, with your EGM API reachable from it:

```bash
API=https://<retail-api-host>; KEY='<terminal key>'
call() { curl -s -X POST "$API/api/retail/terminal" -H "Content-Type: application/json" -H "X-Terminal-Key: $KEY" -d "$1"; echo; }

call '{"egmId":"EGM1","event":"Balance"}'                                                     # proves we can read your EGM API
call '{"egmId":"EGM1","event":"Bill_Inserted","amount":100,"transactionId":"T-BILL-1","balance":100}'
call '{"egmId":"EGM1","event":"Cashout","transactionId":"T-CO-1"}'                            # amount = your meter
```

Then, with the game open at `<GameUrl>?egmid=EGM1`:

1. Insert a note. The game's balance updates within a second.
2. Bet R10. Your EGM API receives a `Debit` of 10 and the meter drops by 10.
3. Cash out in the game before the crash. Your EGM API receives a `Credit` that includes the stake.
4. Press cash out on the cabinet while a bet is riding. You get `409 ROUND_IN_PROGRESS`, then `200` once the round ends.
5. Make your EGM API time out once on a `Debit`. You receive the same `transactionId` again, and the meter must only drop once.

---

## 9. Checklist

- [ ] The EGM API's `/wallet` endpoint is live, reachable from our API, and checks `X-Api-Key`.
- [ ] Debit and credit are applied atomically and return the balance after the call.
- [ ] `transactionId` is idempotent: a repeat returns 200 with `duplicate: true` and changes nothing.
- [ ] A debit larger than the meter answers `402 INSUFFICIENT_FUNDS`.
- [ ] A rollback for an unknown bet refunds nothing and answers 200.
- [ ] The endpoint answers within 5 seconds.
- [ ] The cabinet's browser opens `<GameUrl>?egmid=<id>`, and each EGM id is registered with us.
- [ ] Bill_Inserted and AFT_Deposit are sent after the meter is updated, ideally with `balance`.
- [ ] Cash-out locks debits, retries on 409, pays the meter, and then unlocks.
- [ ] Amounts are in currency units (not cents), with at most 2 decimal places.

---

## Appendix: setup on the API side (for our team)

```json
"RGS": {
  "Provider": "Retail",
  "Retail": {
    "BaseUrl": "https://<egm-api-host>/",
    "WalletPath": "wallet",
    "ApiKey": "<key the EGM side issued us, from secrets>",
    "TimeoutSeconds": 5,
    "MaxRetries": 3
  }
},
"Retail": {
  "Currency": "ZAR",
  "GameId": "feeding-frenzy",
  "TerminalApiKey": "<key we issue the EGM side, from secrets>",
  "AllowUnregisteredEgms": false,
  "Egms": [
    { "EgmId": "EGM1", "Name": "EGM 1", "AllowedIps": ["10.0.4.21"] },
    { "EgmId": "EGM2", "Name": "EGM 2", "TerminalKey": "<per-cabinet key, optional>" }
  ]
}
```

- Code: `Infrastructure/RGS/RetailRGSAdapter.cs` (calls to the EGM API), `Presentation/Controllers/RetailController.cs` (session and terminal), `Application/Retail/RetailWalletService.cs` (EGM registry, terminal keys, pushing balances to the game).
- The API refuses to start with `RGS:Provider = Retail` and an empty `RGS:Retail:BaseUrl`.
