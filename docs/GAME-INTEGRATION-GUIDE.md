# Building a game on the Game Engine API

This guide is for developers who want to build a new game on this API (`Worldiator.Server`, the "FeedingFranzee API"). It covers:

- how a player gets from the casino lobby into your game;
- which engine to choose and how to configure it;
- how money moves (debit, credit, rollback);
- how the Game Back Office manages your game after launch;
- the step-by-step checklist for adding a game, using **Arctic Appetite** as the worked example.

Code references are relative to the repository root.

---

## 1. The big picture

```
 Casino lobby / operator
          │  player clicks a game
          ▼
 ┌────────────────────┐   POST api/auth/create-session  (X-API-Key)   ┌──────────────────────────┐
 │ Games-API (BetNova)│ ─────────────────────────────────────────────▶ │ Game Engine API (this)   │
 │ BetAvenue "Game"   │ ◀──────────── { token } ────────────────────── │ issues a player JWT      │
 │ table, wallets     │                                                │ scoped to ONE game       │
 └────────────────────┘                                                └──────────────────────────┘
          │ redirects player to  <GameUrl>?token=<jwt>                          ▲   │
          ▼                                                                     │   │ wallet calls
 ┌────────────────────┐   REST / SignalR with the JWT                           │   │ api/SdGaming/*
 │ Your game frontend │ ────────────────────────────────────────────────────────┘   ▼
 │ (React/Vite, etc.) │                                                Games-API → operator wallet
 └────────────────────┘
```

The rules everything else follows:

1. **The server decides every result.** The frontend sends only the player's choices: stake, pick, target or cash-out. The outcome comes from this API's RNG, and so does every payout. The browser never asks for a credit.
2. **The player's identity comes only from the JWT.** Player id, username, currency and game are claims in the token. They are never read from query strings or request bodies.
3. **A session token belongs to one game.** The token's `game` claim must match the game being played or settled. A Comet Rider token can't place a bet booked under Mystery Box.
4. **Money moves through the wallet layer.** That layer has idempotency, settlement rules and a ledger. Engines never call the operator wallet directly.

---

## 2. Two places a game must exist

A game is registered in **two catalogues**, linked by a number.

| Where | Table | Key fields | Who uses it |
|---|---|---|---|
| **Games-API / BetAvenue** (`BetNova-GameAPI` repo) | `Game`, `GameImages`, `OperatorGame` | `ProviderID = 10013` (SD GAMING), `ProviderGameID`, `Enum`, `GameUrl` | Lobby listing, launching, wallet bookings |
| **This API** (`FeedingFranzee` database) | `dbo.Games` (+ `dbo.GameEvolutionTiers` for evolving crash games) | `GameId` (string), `ProviderGameId` (number), `Engine`, limits | Running the game, the back office |

**The link:** `dbo.Games.ProviderGameId` equals BetAvenue's `Game.ProviderGameID`.

- At launch, Games-API sends that number to `api/auth/create-session`. This API maps it to your `GameId` and puts the `GameId` in the token.
- On every wallet call this API sends the number back, so Games-API books the bet under the right title.

**ProviderGameIds in use:**

| ProviderGameId | GameId | Name | Engine |
|---|---|---|---|
| *(none)* | `feeding-frenzy` | Feeding Frenzy | Custom (Games-API's default) |
| 2 | `mystery-box` | Mystery Box | Instant (scratch) |
| 3 | `tradex` | Margin Call | MarginCall |
| 4 | `comet-rider` | Comet Rider | Custom |
| 5 | `message-in-a-bottle` | Message in a Bottle | Instant (scratch) |
| 6 | `roulette` | Roulette | Roulette |
| 7 | `arctic-appetite` | Arctic Appetite | Custom |
| 8 | `defuse` | Defuse | Defuse |

Always check `SELECT GameId, ProviderGameId FROM dbo.Games` before you pick the next number. Two games can't share a ProviderGameId, and the API refuses to start if they do.

---

## 3. Choosing an engine

The `Engine` field decides how your game plays. Pick the one closest to your design. If none fits, a new engine means new code here: a service, a controller or hub, and validation in `Domain/GameDefinition.cs`.

| Engine | What it is | Frontend talks to | Config block | Examples |
|---|---|---|---|---|
| `Instant` | One request = one play. Debit, RNG draw and credit happen in a single call. | `GET api/play/info`, `POST api/play` | `Rng` (Multiplier / Pick / Target) | Mystery Box, Message in a Bottle, example-slot |
| `LiveCrash` | Shared rounds: a betting window, then the multiplier climbs, and players cash out before the crash. | SignalR `/livecrashhub` | `Rng` (Target) + `Live` | example-live-crash |
| `Custom` | The "evolving creature" crash engine (`Application/GameEngine.cs`). All Custom games share its rounds, and the `game` claim decides which title the wallet books under. | SignalR `/gamehub` + `GET api/game/limits` + `GET api/games/{id}/evolution` | none (evolution ladder in `dbo.GameEvolutionTiers`) | Feeding Frenzy, Comet Rider, Arctic Appetite |
| `MarginCall` | Trading crash game | SignalR `/margincallhub` | `Trading` (+ optional `Rng` Target) | TradeX / Margin Call |
| `Roulette` | European roulette: many bets per spin, one draw | `GET api/roulette/info`, `POST api/roulette/spin` | `Rng` (Wheel, 37 pockets) + `Roulette` | Roulette |
| `Blackjack` | One player against the dealer, with a provably-fair shoe per hand | `api/blackjack/info`, `state`, `deal`, `action` | `Blackjack` (no `Rng`) | Blackjack |
| `Defuse` | Cut wires and cash out before the detonator | `api/defuse/info`, `state`, `bet`, `cut`, `tip`, `cashout`, `seed` | none (no `Rng`) | Defuse |

**Quickest route to a new game:**
- **A re-skin of an existing mechanic.** Configuration and a new frontend are enough, with no API code. Examples: a new scratch card on `Instant` + `Rng.Multiplier`, or a new evolving crash game on `Custom`.
- **A new `Instant` card layout** needs a small change in `Domain/Cards/` (see `MysteryBoxCard.cs` and `BottlesCard.cs`), registered in `CardLayouts`.

### RNG models (for `Instant`, `LiveCrash`, `Roulette`)

Defined in `Domain/GameDefinition.cs` → `RngSettings`:

- **Multiplier**: draws a payout multiplier from a weighted `Outcomes` table, where `Multiplier: 0` is a loss. Optional `Card` (a layout that decorates the drawn prize) and `Stakes` (fixed card prices). Used for slots and scratch cards.
- **Pick**: the player picks one of `Options`, the RNG draws one, and a match pays that option's `Payout`. Used for predictions.
- **Target**: the player picks a target and the RNG draws a crash point. If the crash point is at or above the target, the play pays the target. Settings are `HouseEdgePercent` and `MaxMultiplier`.
- **Wheel**: 37 equally likely pockets. Roulette only.

Validation runs on startup and on every back-office save. It rejects:
- an RTP above 100%;
- duplicate multipliers;
- a `MaxWin` below the top prize;
- engine/model mismatches.

Seeds rotate every `RotateSeedEvery` draws, so past results can be verified. `api/play/info` returns the current seed hash and the previous seed.

---

## 4. How launch and authentication work

### 4.1 Launch (production)

1. The player opens the game in the lobby. Games-API looks up the BetAvenue `Game` row and calls this API:

   ```http
   POST /api/auth/create-session
   X-API-Key: <GamesApi:ApiKey>
   Content-Type: application/json

   { "PlayerID": 123456, "OperatorID": 20050, "CurrencyCode": "ZAR", "Username": "jdoe", "GameId": "7" }
   ```

   `GameId` may be the numeric ProviderGameId (what Games-API sends) or the string GameId. If it's empty, the game is `feeding-frenzy`.
2. This API returns `{ "token": "<jwt>" }`. The token is valid for 4 hours and carries:
   - `sub` = player id
   - `name` = username
   - `currency`
   - `game` = GameId
3. Games-API redirects the player to `<Game.GameUrl>?token=<jwt>`.
4. Your frontend reads `?token=` and uses it:
   - **REST:** `Authorization: Bearer <jwt>`
   - **SignalR:** `accessTokenFactory: () => token`. It travels as `?access_token=`, which the API accepts on `/gamehub`, `/livecrashhub`, `/margincallhub` and `/promohub`.

`POST /api/games/launch` does the same thing and also returns the full `launchUrl`. New integrations can use it; Games-API uses `create-session` today.

### 4.2 Local development and staging

There's no lobby when you develop locally, so ask the API for a demo token:

```http
GET /api/dev-token?gameId=arctic-appetite
→ { "token": "<jwt for a demo_xxx player>" }
```

- **Only in Development and Staging.** It doesn't exist in Production.
- **Demo wallet:** players whose id starts with `demo_` use an in-memory wallet with a demo balance (`RGS:Stub:DefaultBalance`). They never touch real money.
- **Frontend pattern:** use `?token=` if present, otherwise call `dev-token` with your game id. See `src/api/auth.ts` in the Arctic Appetite / Feeding Frenzy frontends.

---

## 5. How money moves

### 5.1 Games played inside this API (most games)

For the `Instant`, `Custom`, `LiveCrash`, `Roulette`, `Blackjack`, `Defuse` and `MarginCall` engines, **you don't call the wallet yourself.** The engine does it:

- **Bet placed:** debit.
- **Win or cash-out:** credit, capped at the game's `MaxWin` per round.
- **Bet cancelled or round failed:** rollback.

Every movement goes through `Application/WalletService.cs`:

1. **Ledger first** (`IWalletLedger`, table `dbo.WalletTransactions`):
   - Each `(game, transactionId)` is idempotent.
   - A credit is only allowed against a completed debit in the same round.
   - A rollback only reverses an existing debit of the same player.
   - Reusing a transaction id for a different request is rejected (`TX_ID_REUSED`).
2. **Then the operator wallet** through `IRGSAdapter`. In production this is `WsbRGSAdapter`, which calls Games-API:

   | Wallet action | Games-API endpoint |
   |---|---|
   | Balance | `api/SdGaming/GetBalance` |
   | Debit | `api/SdGaming/SubmitBet` |
   | Credit | `api/SdGaming/ClaimBet` |
   | Rollback | `api/SdGaming/Cancel` |

   Each call sends `GameId = ProviderGameId`.
3. **If the wallet times out**, the transaction is marked `Failed`. A retry with the **same** transactionId reuses the same reference, so the operator dedupes it.

`RGS:Provider` selects the adapter:
- `Wsb`: Games-API (staging and production).
- `Stub`: in-memory wallet. Development and Staging only; the API refuses to start with it in Production.
- `Operator1`/`2`/`3`: other HTTP wallets.

### 5.2 Games with their own backend (optional)

If your game has its **own server** that decides results, it can still use this API's wallet:

- Give the game a `ServerKey` (a long random secret) in the back office.
- Your backend calls these endpoints with header `X-Game-Key: <ServerKey>` and the player's session token in the body:

| Endpoint | Body | Notes |
|---|---|---|
| `POST api/wallet/balance` | `{ sessionToken }` | |
| `POST api/wallet/debit` | `{ sessionToken, roundId, transactionId, amount }` | Needs a live (unexpired) session. The amount must be within MinBet–MaxBet. |
| `POST api/wallet/credit` | `{ sessionToken, roundId, transactionId, amount }` | Works after the token expires, so a round can still settle. Only allowed after a completed debit in that round. |
| `POST api/wallet/rollback` | `{ sessionToken, transactionId, debitTransactionId }` | Refunds exactly the debit's amount. |

**Request rules:**
- Ids are 1–64 characters of `A-Z a-z 0-9 _ - : .`.
- Amounts have at most 2 decimals.

**Responses:**
- **Success:** `{ transactionId, balance, currency, replayed }`. `replayed: true` means you sent a duplicate and nothing moved twice.
- **Failure:** `{ error, code }` with one of these statuses:
  - `400` bad input
  - `401` (`INVALID_GAME_KEY` or `INVALID_SESSION`)
  - `403` (`WRONG_GAME`)
  - `409` (`IN_PROGRESS` or `TX_ID_REUSED`)
  - `422` (wallet rejected, e.g. insufficient funds)
  - `502` (wallet unreachable: retry with the same transactionId)

The browser itself may only call `GET api/wallet/balance` with its Bearer token. It's read-only.

---

## 6. Game configuration

### 6.1 Where config lives

```
appsettings.json "Games": [...]  ──(startup: insert games the DB doesn't have yet)──▶  dbo.Games
                                                                                        │
                        Game Back Office edits ─────────────────────────────────────────┤
                                                                                        ▼
                                                        in-memory snapshot on every API instance
                                                        (reloaded every BackOffice:ConfigRefreshSeconds, default 15s)
```

- **`dbo.Games` is the source of truth.** appsettings only *seeds* new games, and it never overwrites a row that already exists.
- **Once a game is in the database, change it in the back office**, not in appsettings.
- **If SQL is unreachable,** the API starts on appsettings values and keeps retrying.
- **Every change is audited** in `dbo.GameConfigAudit` (who, when, before and after; the ServerKey is masked).

### 6.2 Game fields (`Domain/GameDefinition.cs`)

| Field | Meaning |
|---|---|
| `GameId` | Stable string id, 2–64 lower-case letters, digits and hyphens. It goes into the token. It can't be changed. |
| `ProviderGameId` | BetAvenue `Game.ProviderGameID`. Must be unique. |
| `Name` | Display name. |
| `Type` | `Crash`, `Slot`, `Prediction`, `Scratch` or `Table`. Lobby grouping only. |
| `Engine` | See section 3. It can't be changed after creation; create a new game instead. |
| `LaunchUrl` | Your frontend URL. Its origin is **automatically allowed by CORS**. |
| `ServerKey` | Only for games with their own backend (section 5.2). |
| `MinBet` / `MaxBet` / `MaxWin` | Stake limits and the cap on the total credited per round. At most 2 decimals. |
| `Enabled` | Disabled games can't be launched and are hidden from `GET api/games`. |
| `Rng`, `Live`, `Roulette`, `Trading`, `Blackjack` | Engine-specific blocks (section 3). |

Example: a scratch card on the Instant engine.

```json
{
  "GameId": "message-in-a-bottle",
  "ProviderGameId": 5,
  "Name": "Message in a Bottle",
  "Type": "Scratch",
  "Engine": "Instant",
  "LaunchUrl": "https://messageinabottle.easygamesdemo.co.za/",
  "MinBet": 2.00, "MaxBet": 20.00, "MaxWin": 10000.00,
  "Enabled": true,
  "Rng": {
    "Model": "Multiplier",
    "Card": "Bottles",
    "Stakes": [ 2, 5, 10, 20 ],
    "RotateSeedEvery": 1000,
    "Outcomes": [
      { "Multiplier": 0,   "Weight": 44556 },
      { "Multiplier": 1,   "Weight": 15000 },
      { "Multiplier": 500, "Weight": 4 }
    ]
  }
}
```

### 6.3 Evolution ladders (`Custom` / `/gamehub` games)

Evolving crash games turn the round multiplier into a creature or ship that grows. The ladder is in `dbo.GameEvolutionTiers`; the built-in fallback is `EvolutionTiers.Defaults` in `Domain/EvolutionTier.cs`.

| Level | Rule |
|---|---|
| 0 | The ambient "food" tier, below 1.00x |
| 1 | The starting tier, exactly 1.00x |
| 2+ | Each level needs a higher multiplier than the one before |

- **`Code` is the art key** the frontend draws, e.g. `penguin`. The back office can edit `Name` and `Multiplier` but **not** codes. Adding or renaming a tier needs new art in the frontend and a code change here.
- **Frontends load the ladder at startup** from the public endpoint `GET api/games/{gameId}/evolution`, and the limits from `GET api/game/limits?gameId=...`.

---

## 7. The Game Back Office

The back office is the `GameBackOffice` repo, a React frontend for the `api/backoffice/*` endpoints.

### 7.1 Sign-in and roles

- **Separate token type.** Login is `POST api/backoffice/auth/login`. It returns a back-office JWT with its own scheme and audience (`game-backoffice`), so player tokens can't open the back office and back-office tokens can't play.
- **Roles:** **Viewer**, below **Editor**, below **Admin**.
  - Viewers can read.
  - Editors can change games, evolution ladders and promotions.
  - Admins can also manage users (`api/backoffice/users`).
- **Checked on every request**, so disabling an account or changing a role takes effect immediately.
- **First Admin:** created from `BackOffice:BootstrapAdmin:Username` and `:Password` when there are no accounts yet. Change the password afterwards and remove that setting.

### 7.2 What it manages

| Screen | Endpoints | Notes |
|---|---|---|
| Games list | `GET api/backoffice/games` | Shows engine, limits, and an "evolution" badge when the game has a ladder |
| Game page | `GET` / `PUT api/backoffice/games/{id}` | General settings, limits and engine blocks (RNG table, live timing, roulette limits, trading, blackjack rules) |
| New game | `POST api/backoffice/games` | Engine is required and fixed after creation |
| Evolution tab | `GET` / `PUT api/backoffice/games/{id}/evolution` | Only shown when the game has a ladder |
| History tab | `GET api/backoffice/games/{id}/history` | Audit trail |
| Promotions | `api/backoffice/promotions/*` | Create, edit, enable, grant free bets, trigger rain, cancel awards, view leaderboards |
| Users | `api/backoffice/users/*` | Admin only |

- **Saved changes are live on every API instance within `BackOffice:ConfigRefreshSeconds`** (default 15 seconds), with no restart.
- **Every save is validated against the whole catalogue**, which catches duplicate ProviderGameIds, an RTP above 100%, and so on. An invalid save is rejected with the error list.

### 7.3 Promotions

Players see promotions through SignalR `/promohub`:

| Method or event | Purpose |
|---|---|
| `GetState()` | Returns the player's current promotions |
| `PromoChanged` | Event: the player's promotions changed; fetch them again |
| `PromoAward` | Event: the player won something |
| `PromoEvent` | Event: game-wide announcements, e.g. rain or jackpot |

The same state is available over REST as `GET api/promotions/state`.

**Free bets:** `PlaceFreeBet` on the hubs, or `{ "freeBet": true }` on `POST api/play`.

**Promotion types:**

| Group | Types |
|---|---|
| Free bets, cash drops and cashback | FreeBet, Rain, RoundDrop, Cashback |
| Leaderboards and races | MultiplierLeaderboard, MultiplierRace |
| Jackpots and mystery prizes | CommunityJackpot, MysteryPrize |
| Missions and offers | BuyXGetFree, CollectionMission, StreakMission |
| Payout boosts | LuckyNumber, BoostedPayout |
| Table games | HandBonus, HandJackpot |

Each type applies only to compatible engines; see `src/api/promoTypes.ts` in the back office. To show promotions in your game, connect to `/promohub` with the player token and render the state. The Feeding Frenzy frontend's `src/promo/` folder is a working example.

---

## 8. Checklist: adding a new game

Worked example: **Arctic Appetite**, a re-skin of the `Custom` evolving crash engine with ProviderGameId 7.

### Step 1: Pick ids

- [ ] **`GameId`:** lower-case with hyphens, e.g. `arctic-appetite`.
- [ ] **`ProviderGameId`:** the next free number in `dbo.Games` (here `7`).
- [ ] **BetAvenue `Game.Enum`:** the next free value in BetAvenue's `Game.Enum` (here `723`). Check `Game.DTO/Enums/Games.cs` in BetNova-GameAPI too.
- [ ] **Frontend address:** e.g. `https://arcticappetite.easygamesdemo.co.za/`.

### Step 2: Register the game in this API

**Option A: back office (recommended).** Go to Games → New game. Fill in the engine, ids, limits and LaunchUrl.

**Option B: appsettings.** Add an entry to the `"Games"` array in `appsettings.json`. On the next start it is copied into `dbo.Games`.

```json
{
  "GameId": "arctic-appetite",
  "ProviderGameId": 7,
  "Name": "Arctic Appetite",
  "Type": "Crash",
  "Engine": "Custom",
  "LaunchUrl": "https://arcticappetite.easygamesdemo.co.za/",
  "MinBet": 1.00, "MaxBet": 10000.00, "MaxWin": 500000.00,
  "Enabled": true
}
```

For **Custom** (evolving) games, also add the ladder:

- [ ] Add a default ladder to `EvolutionTiers.Defaults` in `Domain/EvolutionTier.cs`.
- [ ] Add seed rows to `Infrastructure/Persistence/GameConfiguration.sql`, then insert them into `dbo.GameEvolutionTiers`:

```sql
INSERT INTO dbo.GameEvolutionTiers ([GameId],[Level],[Code],[Name],[Multiplier])
VALUES (N'arctic-appetite',0,N'krill',N'Krill',0.50),
       (N'arctic-appetite',1,N'penguin',N'Penguin',1.00),
       (N'arctic-appetite',2,N'seal',N'Seal',2.00),
       (N'arctic-appetite',3,N'polarbear',N'Polar Bear',5.00),
       (N'arctic-appetite',4,N'orca',N'Orca',10.00),
       (N'arctic-appetite',5,N'leviathan',N'Ice Leviathan',25.00);
```

When you run SQL against `dbo.Games` with `sqlcmd`, add `-I` (QUOTED_IDENTIFIER on). The table has a filtered index and updates fail without it.

### Step 3: Register the game in Games-API (BetAvenue)

Add an onboarding script to `BetNova-GameAPI/Games - API/Scripts/`. Copy `2026-11-05-arcticappetite-onboard.sql`. It:

- inserts a `Game` row with:
  - `ProviderID 10013`;
  - your `ProviderGameID` as a string, e.g. `N'7'`;
  - `GameTypeID` (1 = Crash, 11 = Scratch Cards);
  - `Enum`;
  - `GameUrl`;
  - `ProviderGameCode` = your GameId;
- adds a `GameImages` row pointing to `<LaunchUrl>/thumbnail.png`;
- enables the game for an operator in `OperatorGame` (SpinSaga = `20050`).

Scripts must be safe to re-run. Check whether the row exists first, and stop with an error if the `Enum` is taken. Run the script against **BetAvenue-Dev** for Development and Staging.

### Step 4: Build the frontend

Start from the closest existing frontend:

| Engine | Copy this frontend |
|---|---|
| Custom | Feeding Frenzy, Comet Rider or Arctic Appetite |
| Instant scratch | Mystery Box or Message in a Bottle |
| Roulette | RouletteGame |
| Blackjack | Blackjack - FE |

The minimum wiring:

- [ ] **Game id:** one constant, e.g. `src/api/base.ts`: `export const GAME_ID = 'arctic-appetite'`. Use it for `dev-token`, limits, and the evolution ladder.
- [ ] **Token:** read `?token=` from the URL. Fall back to `GET /api/dev-token?gameId=<GAME_ID>` in dev.
- [ ] **API address:**
  - Production builds: put `VITE_API_BASE=https://<api host>` in `.env.production`.
  - Dev: proxy `/api`, `/gamehub`, `/livecrashhub` and `/promohub` to `http://localhost:5003` in `vite.config.ts`. Forgetting `/promohub` gives a 404 on the promo hub.
- [ ] **Show what the server says:**
  - balance comes from `ConnectedAs` / `BetPlaced` / `CashOutResult` or `api/wallet/balance`;
  - multiplier and crash come from `RoundStarted` / `MultiplierUpdated` / `RoundCrashed`;
  - play results come from the `POST api/play` response.
  - Never compute a win in the browser.
- [ ] **Static files in `public/`:** `thumbnail.png` (lobby image, 600×400), favicon and logo.
- [ ] **Build:** `npm run build`. Deploy `dist/` to an IIS site at the LaunchUrl's host.

### Step 5: Deploy and test

1. **Deploy this API.** Run `dotnet publish -c Release -o publish` and deploy `publish/`.
   - CORS allows each game's `LaunchUrl` origin automatically.
   - Add the origin to `AllowedOrigins` in `appsettings.json` / `appsettings.Staging.json` too, so it also works if the catalogue can't be read.
2. **Check the public endpoints:**
   - `GET api/games` lists your game.
   - `GET api/games/<id>/evolution` returns the ladder (Custom games).
   - `GET api/game/limits?gameId=<id>` returns the limits.
3. **Check the back office:** the game appears, and Custom games have an Evolution tab.
4. **Launch from the lobby on staging:**
   - place a bet, win one, lose one;
   - confirm the bets show in Games-API under the right title.
5. **Check promotions:** create a test promotion for the game, check it appears in-game, then **switch it off**. The staging and dev databases are shared.

---

## 9. Reference

### 9.1 Endpoints

| Area | Endpoint | Auth |
|---|---|---|
| Launch | `POST api/auth/create-session`, `POST api/games/launch` | `X-API-Key` (Games-API) |
| Dev token | `GET api/dev-token?gameId=` | none, Dev/Staging only |
| Catalogue | `GET api/games`, `GET api/games/{id}/evolution` | public |
| Custom engine | SignalR `/gamehub`: `PlaceBet(amount, slot, autoCashOut)`, `PlaceFreeBet`, `CashOut(slot)`, `EatFish`, `BombHit`, `GambleWinnings`, `GetHistory`, `GetMyBets` | player JWT |
| Custom engine (REST) | `GET api/game/state`, `history`, `limits`, `multiplier-history`, `my-bets` | public / JWT |
| Instant | `GET api/play/info`, `POST api/play` `{ stake, pick?, target?, freeBet? }` | player JWT |
| Live crash | SignalR `/livecrashhub`: `GetSnapshot`, `PlaceBet`, `PlaceFreeBet`, `CancelBet`, `CashOut`, `GetBalance` | player JWT |
| Margin Call | SignalR `/margincallhub` | player JWT |
| Roulette | `GET api/roulette/info`, `POST api/roulette/spin` | player JWT |
| Blackjack | `GET api/blackjack/info`, `state`; `POST deal`, `action` | player JWT |
| Defuse | `GET api/defuse/info`, `state`; `POST bet`, `cut`, `tip`, `cashout`, `seed` | player JWT |
| Promotions | SignalR `/promohub`, `GET api/promotions/state` | player JWT |
| Wallet (browser) | `GET api/wallet/balance` | player JWT |
| Wallet (game backend) | `POST api/wallet/balance`, `debit`, `credit`, `rollback` | `X-Game-Key` + session token |
| Back office | `api/backoffice/*` | back-office JWT + role |
| Health | `GET /health` | public |

**Custom-engine `/gamehub` server events:**

| Event | When it fires |
|---|---|
| `ConnectedAs` | On connect, with the player's identity and balance |
| `RoundState` | On connect, if a round is running |
| `RoundStarted` | A new round begins |
| `MultiplierUpdated` | The multiplier changed |
| `RoundCrashed` | The round crashed |
| `BetAdded` | Any player placed a bet |
| `BetPlaced` / `BetError` | Your own bet succeeded or failed |
| `BetCashedOut` | Any player cashed out |
| `CashOutResult` / `CashOutError` | Your own cash-out succeeded or failed |
| `FishEaten` | Your creature ate something |
| `PlayerDied` | Your creature hit a hazard |
| `GambleResult` / `GambleError` | Your gamble succeeded or failed |
| `History` | Reply to `GetHistory` |
| `MyBets` | Reply to `GetMyBets` |

A token for a non-Custom game is disconnected from `/gamehub`.

### 9.2 Rate limits

| Bucket | Limit |
|---|---|
| Per game backend (`X-Game-Key`) | 6000/min |
| Per signed-in player | 300/min |
| Per IP otherwise | 60/min |
| `/gamehub` bets | 5 per 10 seconds |

Over the limit returns HTTP `429`.

### 9.3 Settings (`appsettings.json`)

Secrets (`Jwt:Key`, `GamesApi:ApiKey`, `RGS:*:ApiKey`, the connection string, `BackOffice:BootstrapAdmin`) are environment-specific. Get them from the team. **Don't commit new secrets.**

| Setting | Purpose |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server (`FeedingFranzee` DB). If empty, the API runs fully in memory. |
| `Jwt:Key` / `Issuer` / `Audience` | Signing for player session tokens. The key must be 32+ characters. |
| `GamesApi:ApiKey` | Shared secret Games-API sends as `X-API-Key` |
| `RGS:Provider` + `RGS:<Provider>:*` | Wallet adapter and its BaseUrl, ApiKey, timeout and retries |
| `RGS:Stub:DefaultBalance` | Demo balance for `demo_` players and the Stub wallet |
| `AllowedOrigins` | Extra CORS origins (back office, lobby). Game LaunchUrls are added automatically. |
| `BackOffice:ConfigRefreshSeconds` | How often each instance reloads config (minimum 5, default 15) |
| `BackOffice:BootstrapAdmin` | Username and password for the first Admin |
| `Redis:ConnectionString` | Optional. Enables multi-instance SignalR and leader election. |
| `Games` | Seed catalogue (section 6) |
| `MarginCall` | Default trading settings for the Margin Call game |

### 9.4 Database scripts (`Infrastructure/Persistence/`)

Run once per database, in this order. All are safe to re-run.

1. `InitialCreate.sql`: rounds and bets for the Custom engine.
2. `StoredProcedures.sql`
3. `WalletTransactions.sql`: the wallet ledger.
4. `GameConfiguration.sql`: `dbo.GameEvolutionTiers`, `dbo.GameConfigAudit`, ladder seeds.
5. `GameCatalogue.sql`: `dbo.Games`.
6. `BackOfficeUsers.sql`: back-office accounts.
7. `Promotions.sql`: promotions and awards.

`BetAvenueCatalogue.sql`, `RouletteCatalogue.sql` and `DefuseCatalogue.sql` are per-game helper scripts.

### 9.5 Key source files

| File | What's in it |
|---|---|
| `Program.cs` | Wiring: auth, wallet adapter selection, CORS, hubs, dev-token |
| `Domain/GameDefinition.cs` | Game fields, engines, RNG models, validation |
| `Domain/EvolutionTier.cs` | Evolution ladder rules and defaults |
| `Application/GameEngine.cs` | The Custom (`/gamehub`) crash engine |
| `Application/PlayService.cs` | Instant engine (one play per request) |
| `Application/WalletService.cs`, `WalletLedgerRules.cs` | Money movement and idempotency |
| `Application/SessionTokenService.cs` | Player JWTs |
| `Infrastructure/RGS/WsbRGSAdapter.cs` | Games-API wallet calls |
| `Infrastructure/Persistence/SqlGameConfigStore.cs` | Catalogue storage, seeding and refresh |
| `Presentation/Controllers/BackOffice*.cs` | Back-office API |
| `Application/Promotions/PromoEngine*.cs` | Promotions |
