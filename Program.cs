using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.Text;
using System.Threading.RateLimiting;
using Worldiator.Server.Application;
using Worldiator.Server.Application.Blackjack;
using Worldiator.Server.Application.Defuse;
using Worldiator.Server.Application.Vault;
using Worldiator.Server.Application.Dive;
using Worldiator.Server.Application.Fruit;
using Worldiator.Server.Application.Hive;
using Worldiator.Server.Application.LiveCrash;
using Worldiator.Server.Application.MarginCall;
using Worldiator.Server.Application.Promotions;
using Worldiator.Server.Application.Retail;
using Worldiator.Server.Domain.Retail;
using Worldiator.Server.Background;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.MarginCall;
using Worldiator.Server.Infrastructure;
using Worldiator.Server.Infrastructure.Distributed;
using Worldiator.Server.Infrastructure.Persistence;
using Worldiator.Server.Infrastructure.RGS;
using Worldiator.Server.Presentation.Controllers;
using Worldiator.Server.Presentation.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// ─── JWT Authentication ───────────────────────────────────────────────────────
// WSB platform launches the game with ?token=<jwt> in the URL.
// The client passes it to SignalR via accessTokenFactory → query string.
// Production key/issuer must be set in appsettings.Production.json or env vars.
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
    throw new InvalidOperationException("Jwt:Key must be set via environment variable or secrets manager. Never hardcode it.");

var jwtIssuer   = builder.Configuration["Jwt:Issuer"]   ?? "worldiator-dev";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "worldiator-game";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey        = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer          = true,
            ValidIssuer             = jwtIssuer,
            ValidateAudience        = true,
            ValidAudience           = jwtAudience,
            ValidateLifetime        = true,
            ClockSkew               = TimeSpan.FromSeconds(30),
        };
        // SignalR transports token via query string — HTTP headers aren't available on WebSocket upgrade.
        opt.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    (ctx.Request.Path.StartsWithSegments("/gamehub") || ctx.Request.Path.StartsWithSegments("/margincallhub")
                     || ctx.Request.Path.StartsWithSegments("/livecrashhub") || ctx.Request.Path.StartsWithSegments("/promohub")))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });

// ─── Game Back Office sign-in ─────────────────────────────────────────────────
// Its own bearer scheme and audience (see BackOfficeAuthService): player tokens can't open the back office and
// back-office tokens can't play. Every request re-checks the account, so disable / role change / password reset
// apply immediately. Viewer < Editor < Admin.
builder.Services.AddAuthentication()
    .AddJwtBearer(BackOfficeAuthService.Scheme, opt =>
    {
        opt.MapInboundClaims = false;
        opt.TokenValidationParameters = BackOfficeAuthService.ValidationParameters(builder.Configuration);
        opt.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var principal = await ctx.HttpContext.RequestServices.GetRequiredService<BackOfficeAuthService>()
                    .RevalidateAsync(ctx.Principal!);
                if (principal == null) ctx.Fail("Back office account disabled, changed or removed.");
                else ctx.Principal = principal;
            },
        };
    });

builder.Services.AddAuthorization(opt =>
{
    void BackOfficePolicy(string name, params BackOfficeRole[] roles) =>
        opt.AddPolicy(name, p => p.AddAuthenticationSchemes(BackOfficeAuthService.Scheme)
                                  .RequireRole(roles.Select(r => r.ToString())));
    BackOfficePolicy(BackOfficeAuthService.ViewerPolicy, BackOfficeRole.Viewer, BackOfficeRole.Editor, BackOfficeRole.Admin);
    BackOfficePolicy(BackOfficeAuthService.EditorPolicy, BackOfficeRole.Editor, BackOfficeRole.Admin);
    BackOfficePolicy(BackOfficeAuthService.AdminPolicy, BackOfficeRole.Admin);
});

// ─── Redis ────────────────────────────────────────────────────────────────────
var redisConnStr = builder.Configuration["Redis:ConnectionString"];
var redisEnabled = !string.IsNullOrEmpty(redisConnStr);

if (redisEnabled)
{
    var redis = await ConnectionMultiplexer.ConnectAsync(redisConnStr!);
    builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
    builder.Services.AddSingleton<ILeaderElection, RedisLeaderElection>();
    builder.Services.AddSignalR().AddStackExchangeRedis(redisConnStr!);
}
else
{
    builder.Services.AddSignalR();
}

// ─── SQL Server persistence ───────────────────────────────────────────────────
var sqlConnStr = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrEmpty(sqlConnStr))
{
    builder.Services.AddScoped<IAsyncRoundRepository, SqlRoundRepository>();
}

// ─── RNG Integration Point ────────────────────────────────────────────────────
builder.Services.AddSingleton<IRNGService, EasyRNGService>();

// ─── RGS Integration Point ────────────────────────────────────────────────────
// Select the wallet adapter via RGS:Provider in appsettings.json or environment:
//   RGS__Provider=Operator1   → Operator1RGSAdapter  (config: RGS:Operator1:*)
//   RGS__Provider=Operator2   → Operator2RGSAdapter  (config: RGS:Operator2:*)
//   RGS__Provider=Operator3   → Operator3RGSAdapter  (config: RGS:Operator3:*)
//   RGS__Provider=Stub        → StubRGSAdapter        (Development or Staging only — never Production)
var rgsProvider = builder.Configuration["RGS:Provider"] ?? "Stub";

switch (rgsProvider)
{
    case "Wsb":
        AddOperatorWallet<WsbRGSAdapter>();
        break;
    case "Operator1":
        AddOperatorWallet<Operator1RGSAdapter>();
        break;
    case "Operator2":
        AddOperatorWallet<Operator2RGSAdapter>();
        break;
    case "Operator3":
        AddOperatorWallet<Operator3RGSAdapter>();
        break;
    // Land-based cabinets: the game launches as ?egmid=EGM1 and the EGM holds the money — balance, bets and wins
    // go to the EGM's wallet endpoint (config: RGS:Retail:*), and the EGM reports notes, AFT and cash-outs to
    // api/retail/terminal. See RetailRGSAdapter, RetailController and the "Retail" config.
    case "Retail":
        AddOperatorWallet<RetailRGSAdapter>();
        break;
    // Staging is allowed here (unlike the upstream Worldiator service) so this
    // standalone Feeding Frenzy deployment can run with a fake in-memory wallet
    // and a demo balance for testing — see RGS:Stub:DefaultBalance. Switch
    // RGS:Provider back to a real operator before wiring up real money.
    case "Stub" when builder.Environment.IsDevelopment() || builder.Environment.IsStaging():
        builder.Services.AddSingleton<IRGSAdapter, StubRGSAdapter>();
        break;
    case "Stub":
        throw new InvalidOperationException("StubRGSAdapter cannot be used in Production. Set RGS:Provider to Retail, Wsb, Operator1, Operator2, or Operator3.");
    default:
        throw new InvalidOperationException($"Unknown RGS provider \"{rgsProvider}\". Valid values: Retail, Wsb, Operator1, Operator2, Operator3 (Stub in Development/Staging only).");
}

// Real operator wallet. In Development/Staging, demo players from /api/dev-token
// (ids prefixed "demo_") are routed to an in-memory wallet instead, so the game
// opened without a platform launch still gets a demo balance it can bet with.
void AddOperatorWallet<TAdapter>() where TAdapter : class, IRGSAdapter
{
    builder.Services.AddHttpClient<TAdapter>();
    if (builder.Environment.IsDevelopment() || builder.Environment.IsStaging())
    {
        builder.Services.AddSingleton<StubRGSAdapter>();
        builder.Services.AddTransient<IRGSAdapter>(sp => new DemoWalletRGSAdapter(
            sp.GetRequiredService<TAdapter>(), sp.GetRequiredService<StubRGSAdapter>()));
    }
    else
    {
        builder.Services.AddTransient<IRGSAdapter>(sp => sp.GetRequiredService<TAdapter>());
    }
}

// ─── Retail cabinets ─────────────────────────────────────────────────────────
// Registered for every provider; the api/retail endpoints only answer when RGS:Provider = Retail.
builder.Services.Configure<RetailOptions>(builder.Configuration.GetSection("Retail"));
builder.Services.AddSingleton<RetailWalletService>();

// ─── Game Back Office config ─────────────────────────────────────────────────
// Every game's settings (the catalogue: limits, RNG tables, timings…) and the evolution ladders, edited through
// api/backoffice. Stored in SQL (Infrastructure/Persistence/GameConfiguration.sql + GameCatalogue.sql), seeded from
// the "Games" array in appsettings, and refreshed on every instance in the background.
if (!string.IsNullOrEmpty(sqlConnStr))
    builder.Services.AddSingleton<IGameConfigStore, SqlGameConfigStore>();
else
    builder.Services.AddSingleton<IGameConfigStore, InMemoryGameConfigStore>();
builder.Services.AddHostedService<GameConfigRefreshService>();
// Back-office accounts (Infrastructure/Persistence/BackOfficeUsers.sql).
if (!string.IsNullOrEmpty(sqlConnStr))
    builder.Services.AddSingleton<IBackOfficeUserStore, SqlBackOfficeUserStore>();
else
    builder.Services.AddSingleton<IBackOfficeUserStore, InMemoryBackOfficeUserStore>();
builder.Services.AddSingleton<BackOfficeAuthService>();
IGameConfigStore? gameConfigForCors = null; // set once the app is built — see the CORS policy

// ─── Promotions ───────────────────────────────────────────────────────────────
// Promo tools for the crash games (free bets, rain, round drops, leaderboards, races, jackpots), managed from the
// back office (api/backoffice/promotions) and shown to players on /promohub. See PromoEngine.
if (!string.IsNullOrEmpty(sqlConnStr))
    builder.Services.AddSingleton<IPromoStore, SqlPromoStore>();
else
    builder.Services.AddSingleton<IPromoStore, InMemoryPromoStore>();
builder.Services.AddSingleton<PromoEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PromoEngine>());

// ─── Core Application Services ───────────────────────────────────────────────
builder.Services.AddSingleton<IRoundRepository, InMemoryRoundRepository>();
builder.Services.AddSingleton<IChatService, ChatService>();
builder.Services.AddSingleton<GameEngine>();
builder.Services.AddHostedService<GameHostedService>();

// ─── Multi-game platform ─────────────────────────────────────────────────────
// Other crash / slot / prediction games launch through api/games/launch and settle
// through api/wallet/* (see WalletController). The catalogue comes from the Game Back Office config store.
builder.Services.AddSingleton<IGameRegistry, StoreGameRegistry>();
builder.Services.AddSingleton<SessionTokenService>();
if (!string.IsNullOrEmpty(sqlConnStr))
    builder.Services.AddSingleton<IWalletLedger, SqlWalletLedger>();
else
    builder.Services.AddSingleton<IWalletLedger, InMemoryWalletLedger>();
builder.Services.AddScoped<WalletService>();
// Games with an "Rng" block play through api/play using this API's RNG (see RngSettings).
builder.Services.AddSingleton<IGameRngService, EasyGameRngService>();
builder.Services.AddScoped<PlayService>();
// Engine = Roulette games spin through api/roulette (many bets, one draw per spin).
builder.Services.AddScoped<RouletteService>();
// Engine = Blackjack games deal through api/blackjack (one player against the dealer, provably-fair shoe per round).
builder.Services.AddSingleton<BlackjackService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BlackjackService>());
// Engine = Defuse games play through api/defuse (cut wires, cash out before the detonator).
builder.Services.AddSingleton<DefuseService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DefuseService>());
// Engine = Vault games play through api/vault (crack the combination, cash out before the alarm).
builder.Services.AddSingleton<VaultService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VaultService>());
// Engine = Dive games play through api/dive (pick a cave per zone, surface before the shark).
builder.Services.AddSingleton<DiveService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiveService>());
// Engine = Fruit games play through api/fruit (slice fruit, cash out before a bomb).
builder.Services.AddSingleton<FruitService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FruitService>());
// Engine = Hive games play through api/hive (open honeycomb cells, read the clues, cash out before a sting).
builder.Services.AddSingleton<HiveService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HiveService>());

// ─── Margin Call (TradeX) — live trading crash game ─────────────────────────
// Server-authoritative markets, rounds and settlement on /margincallhub. See MarginCallEngine.
builder.Services.Configure<MarginCallOptions>(builder.Configuration.GetSection("MarginCall"));
builder.Services.AddSingleton<MarginCallEngine>();
builder.Services.AddHostedService<MarginCallHostedService>();

// ─── Live crash games ───────────────────────────────────────────────────────
// One engine per "Games" entry with Engine = LiveCrash, each with its own RNG instance,
// rounds and SignalR group, all served by /livecrashhub. See LiveCrashEngine.
builder.Services.AddSingleton<LiveCrashRegistry>();
builder.Services.AddHostedService<LiveCrashHostedService>();

// ─── Rate Limiting ────────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(opt =>
{
    opt.AddSlidingWindowLimiter("bet", o =>
    {
        o.PermitLimit = 5;
        o.Window = TimeSpan.FromSeconds(10);
        o.SegmentsPerWindow = 2;
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    opt.AddFixedWindowLimiter("chat", o =>
    {
        o.PermitLimit = 30;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    // Game backends authenticated by X-Game-Key get their own high-volume bucket (they call
    // debit/credit for every player); signed-in players get one each (a slot on autoplay plays
    // every second, and many players can share one carrier IP); everyone else is limited per IP.
    // Runs after UseAuthentication, so ctx.User is already validated.
    opt.AddPolicy("api", ctx =>
    {
        var gameKey = ctx.Request.Headers[WalletController.GameKeyHeader].ToString();
        var game = ctx.RequestServices.GetRequiredService<IGameRegistry>().FindByServerKey(gameKey);
        if (game != null)
            return RateLimitPartition.GetFixedWindowLimiter("game:" + game.GameId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 6000,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        // A retail terminal reports for every cabinet on its floor, usually from one address.
        var terminalKey = ctx.Request.Headers[RetailWalletService.TerminalKeyHeader].ToString();
        if (ctx.RequestServices.GetRequiredService<RetailWalletService>().IsAnyTerminalKey(terminalKey))
            return RateLimitPartition.GetFixedWindowLimiter("terminal:" + ctx.Connection.RemoteIpAddress, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 6000,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        var player = ctx.User.Identity?.IsAuthenticated == true ? SessionTokenService.FromPrincipal(ctx.User) : null;
        if (player != null)
            return RateLimitPartition.GetFixedWindowLimiter("player:" + player.PlayerId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        return RateLimitPartition.GetFixedWindowLimiter("ip:" + ctx.Connection.RemoteIpAddress, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
    opt.RejectionStatusCode = 429;
});

// ─── Health Checks ───────────────────────────────────────────────────────────
var health = builder.Services.AddHealthChecks();
if (!string.IsNullOrEmpty(sqlConnStr))
    health.AddSqlServer(sqlConnStr, name: "sql");
if (redisEnabled)
    health.AddRedis(redisConnStr!, name: "redis");

// ─── CORS ────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            // Allow any localhost port in dev — Vite auto-increments (5173–5176+)
            policy.SetIsOriginAllowed(o => new Uri(o).Host == "localhost")
                  .AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }
        else
        {
            // Support AllowedOrigins (array) or legacy AllowedOrigin (single string)
            var allowedOrigins = builder.Configuration
                .GetSection("AllowedOrigins").Get<string[]>();

            if (allowedOrigins == null || allowedOrigins.Length == 0)
            {
                var single = builder.Configuration["AllowedOrigin"];
                allowedOrigins = !string.IsNullOrEmpty(single)
                    ? [single]
                    : null;
            }

            if (allowedOrigins != null && allowedOrigins.Length > 0)
            {
                // Each game's frontend reads the player balance from api/wallet/balance, so its origin is allowed too.
                // Read from the catalogue on every check, so a LaunchUrl set in the back office works straight away.
                var fixedOrigins = allowedOrigins.Select(o => o.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                policy.SetIsOriginAllowed(origin => fixedOrigins.Contains(origin)
                          || (gameConfigForCors?.Games.Any(g => Uri.TryCreate(g.LaunchUrl, UriKind.Absolute, out var url)
                                 && string.Equals(url.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)) ?? false))
                      .AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }
            else
                policy.SetIsOriginAllowed(_ => true)
                      .AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }
    });
});

var app = builder.Build();

// Load the game catalogue and evolution ladders from SQL (importing any appsettings game it doesn't have yet)
// before the game engines start. If SQL is unreachable the API starts on appsettings + built-in values and
// GameConfigRefreshService keeps retrying.
var gameConfig = app.Services.GetRequiredService<IGameConfigStore>();
gameConfigForCors = gameConfig;
try { await gameConfig.InitializeAsync(); }
catch (Exception ex)
{
    app.Logger.LogError(ex, "Game config could not be loaded from the database — starting with appsettings / built-in values");
}

// First back-office Admin, from BackOffice:BootstrapAdmin, when there are no accounts yet.
try { await app.Services.GetRequiredService<BackOfficeAuthService>().BootstrapAsync(app.Configuration); }
catch (Exception ex)
{
    app.Logger.LogError(ex, "Back office accounts could not be read — run Infrastructure/Persistence/BackOfficeUsers.sql");
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
    await next();
});

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers().RequireRateLimiting("api");
app.MapHub<GameHub>("/gamehub");
app.MapHub<MarginCallHub>("/margincallhub");
app.MapHub<LiveCrashHub>("/livecrashhub");
app.MapHub<PromoHub>("/promohub");
app.MapHealthChecks("/health");

// Session creation (called by Games-API on game launch) lives in AuthController
// (api/auth/create-session) and GamesController (api/games/launch).

// ─── Dev token endpoint ───────────────────────────────────────────────────────
// Issues a short-lived JWT for dev/staging — not available in production.
// In production, WSB platform provides the token via the game launch URL: ?token=<jwt>
// Pass ?gameId=<id> to get a token for another game in the "Games" list.
if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
{
    app.MapGet("/api/dev-token", (SessionTokenService tokens, string? gameId) =>
    {
        var playerId = DemoWalletRGSAdapter.DemoPlayerPrefix + Random.Shared.NextInt64(100_000_000, 999_999_999);
        var username = "dev_" + playerId[^4..];
        var token    = tokens.Issue(playerId, username, "ZAR", gameId ?? SessionTokenService.DefaultGameId);
        return Results.Ok(new { token });
    }).AllowAnonymous();
}

app.Run();
