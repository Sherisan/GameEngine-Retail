using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Application;

public enum WalletErrorKind { BadRequest, Conflict, Rejected, WalletUnavailable }

public record WalletResult(
    bool Ok,
    string? TransactionId = null,
    decimal? Balance = null,
    bool Replayed = false,
    WalletErrorKind? ErrorKind = null,
    string? ErrorCode = null,
    string? Error = null)
{
    public static WalletResult Fail(WalletErrorKind kind, string code, string message) =>
        new(false, ErrorKind: kind, ErrorCode: code, Error: message);
}

/// <summary>
/// Debit / credit / rollback for games other than Feeding Frenzy, on behalf of their backends.
/// Every movement goes through IWalletLedger first (idempotency + settlement rules), then the
/// configured IRGSAdapter. RGS references are derived deterministically from (game, transaction id),
/// so a retried request hits the operator wallet with the same idempotency key.
/// </summary>
public partial class WalletService(IWalletLedger ledger, IRGSAdapter rgs, ILogger<WalletService> logger)
{
    [GeneratedRegex(@"^[A-Za-z0-9_\-:.]{1,64}$")]
    private static partial Regex IdPattern();

    public Task<decimal> GetBalanceAsync(PlayerSession player) =>
        rgs.GetBalanceAsync(player.PlayerId, player.Currency, player.GameId);

    public Task<WalletResult> DebitAsync(GameDefinition game, PlayerSession player, string roundId, string transactionId, decimal amount)
    {
        if (amount < game.MinBet || amount > game.MaxBet)
            return Task.FromResult(WalletResult.Fail(WalletErrorKind.BadRequest, "INVALID_AMOUNT",
                FormattableString.Invariant($"Bet must be between {game.MinBet} and {game.MaxBet}.")));

        var tx = NewTx(game, player, WalletTxType.Debit, roundId, transactionId, amount);
        return ExecuteAsync(game, tx, () => rgs.DebitAsync(player.PlayerId, amount, player.Currency, RgsRoundId(game, tx.RoundId), tx.Reference, game.GameId));
    }

    public Task<WalletResult> CreditAsync(GameDefinition game, PlayerSession player, string roundId, string transactionId, decimal amount)
    {
        if (amount < 0)
            return Task.FromResult(WalletResult.Fail(WalletErrorKind.BadRequest, "INVALID_AMOUNT", "Credit can't be negative."));

        var tx = NewTx(game, player, WalletTxType.Credit, roundId, transactionId, amount);
        return ExecuteAsync(game, tx, () => rgs.CreditAsync(player.PlayerId, amount, player.Currency, RgsRoundId(game, tx.RoundId), tx.Reference, game.GameId));
    }

    public Task<WalletResult> RollbackAsync(GameDefinition game, PlayerSession player, string transactionId, string debitTransactionId)
    {
        if (!IdPattern().IsMatch(debitTransactionId ?? string.Empty))
            return Task.FromResult(WalletResult.Fail(WalletErrorKind.BadRequest, "INVALID_ID", "debitTransactionId is invalid."));

        // RoundId and Amount are filled in from the debit by the ledger.
        var tx = NewTx(game, player, WalletTxType.Rollback, roundId: string.Empty, transactionId, amount: 0);
        tx.RelatedExternalTxId = debitTransactionId;
        return ExecuteAsync(game, tx, async () =>
        {
            // tx.Amount is the debit's amount (filled in by the ledger) — the wallet refunds exactly what is sent.
            await rgs.RollbackAsync(player.PlayerId, Reference(game.GameId, WalletTxType.Debit, debitTransactionId!), RgsRoundId(game, tx.RoundId), game.GameId, tx.Amount, player.Currency);
            await ledger.MarkRolledBackAsync(game.GameId, debitTransactionId!);
            return tx.Reference;
        });
    }

    private async Task<WalletResult> ExecuteAsync(GameDefinition game, WalletTransaction tx, Func<Task<string>> callRgs)
    {
        if (!IdPattern().IsMatch(tx.ExternalTxId))
            return WalletResult.Fail(WalletErrorKind.BadRequest, "INVALID_ID", "transactionId must be 1-64 chars of A-Z a-z 0-9 _ - : .");
        if (tx.Type != WalletTxType.Rollback && !IdPattern().IsMatch(tx.RoundId))
            return WalletResult.Fail(WalletErrorKind.BadRequest, "INVALID_ID", "roundId must be 1-64 chars of A-Z a-z 0-9 _ - : .");
        if (tx.Amount != Math.Round(tx.Amount, 2))
            return WalletResult.Fail(WalletErrorKind.BadRequest, "INVALID_AMOUNT", "Amount can have at most 2 decimal places.");

        var begin = await ledger.BeginAsync(tx, game.MaxWin);
        switch (begin.Outcome)
        {
            case LedgerOutcome.AlreadyCompleted:
                return new WalletResult(true, tx.ExternalTxId, await TryGetBalanceAsync(tx), Replayed: true);
            case LedgerOutcome.InProgress:
                return WalletResult.Fail(WalletErrorKind.Conflict, begin.ErrorCode!, begin.Error!);
            case LedgerOutcome.Rejected:
                var kind = begin.ErrorCode == "TX_ID_REUSED" ? WalletErrorKind.Conflict : WalletErrorKind.Rejected;
                return WalletResult.Fail(kind, begin.ErrorCode!, begin.Error!);
        }

        try
        {
            await callRgs();
        }
        catch (RGSException ex)
        {
            await ledger.FailAsync(tx.GameId, tx.ExternalTxId, ex.Message);
            return WalletResult.Fail(WalletErrorKind.Rejected, ex.Code ?? "WALLET_REJECTED", ex.Message);
        }
        catch (Exception ex)
        {
            // Outcome at the operator is unknown (e.g. timeout). The game backend should retry with the
            // same transactionId — the RGS reference is identical, so the operator dedupes it.
            logger.LogError(ex, "Wallet {Type} failed: game={GameId} player={PlayerId} tx={TxId}",
                tx.Type, tx.GameId, tx.PlayerId, tx.ExternalTxId);
            await ledger.FailAsync(tx.GameId, tx.ExternalTxId, ex.Message);
            return WalletResult.Fail(WalletErrorKind.WalletUnavailable, "WALLET_ERROR",
                "The wallet could not be reached. Retry with the same transactionId.");
        }

        await ledger.CompleteAsync(tx.GameId, tx.ExternalTxId);
        logger.LogInformation("Wallet {Type} ok: game={GameId} player={PlayerId} round={RoundId} tx={TxId} amount={Amount}",
            tx.Type, tx.GameId, tx.PlayerId, tx.RoundId, tx.ExternalTxId, tx.Amount);
        return new WalletResult(true, tx.ExternalTxId, await TryGetBalanceAsync(tx));
    }

    private async Task<decimal?> TryGetBalanceAsync(WalletTransaction tx)
    {
        try { return await rgs.GetBalanceAsync(tx.PlayerId, tx.Currency, tx.GameId); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Balance lookup after wallet {Type} failed", tx.Type);
            return null;
        }
    }

    private static WalletTransaction NewTx(GameDefinition game, PlayerSession player, WalletTxType type, string roundId, string transactionId, decimal amount) => new()
    {
        GameId       = game.GameId,
        ExternalTxId = transactionId ?? string.Empty,
        Type         = type,
        PlayerId     = player.PlayerId,
        Currency     = player.Currency,
        RoundId      = roundId ?? string.Empty,
        Amount       = amount,
        Reference    = Reference(game.GameId, type, transactionId ?? string.Empty),
    };

    // Namespaced so round ids from different games can't collide at the operator.
    private static string RgsRoundId(GameDefinition game, string roundId) => $"{game.GameId}:{roundId}";

    // "<PREFIX>_<32 hex>" — the shape WsbRGSAdapter.GuidFromReference expects.
    private static string Reference(string gameId, WalletTxType type, string transactionId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{gameId}|{type}|{transactionId}"));
        return $"G{type.ToString().ToUpperInvariant()}_{new Guid(hash):N}";
    }
}
