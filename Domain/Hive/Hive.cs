using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Worldiator.Server.Domain.Hive;

/// <summary>
/// The hive: 19 hexagonal cells in a honeycomb of radius 2 (rows of 3, 4, 5, 4, 3), pointy side up, in axial
/// coordinates (q, r). Cells are numbered row by row, top to bottom and left to right, exactly like the frontend's
/// src/game/hex.ts, and every mask is a bit mask over those numbers.
/// </summary>
public static class HiveBoard
{
    public const int CellCount = 19;
    private const int Radius = 2;
    public const int AllCells = (1 << CellCount) - 1;

    /// <summary>Neighbors[i]: the cells touching cell i (3 to 6 of them), as a bit mask.</summary>
    public static readonly int[] Neighbors = BuildNeighbors();

    private static int[] BuildNeighbors()
    {
        var cells = new List<(int Q, int R)>();
        for (var r = -Radius; r <= Radius; r++)
            for (var q = Math.Max(-Radius, -Radius - r); q <= Math.Min(Radius, Radius - r); q++)
                cells.Add((q, r));
        (int Dq, int Dr)[] directions = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, -1), (-1, 1)];
        return cells.Select(c => directions.Aggregate(0, (mask, d) =>
        {
            var n = cells.IndexOf((c.Q + d.Dq, c.R + d.Dr));
            return n < 0 ? mask : mask | (1 << n);
        })).ToArray();
    }

    public static bool Has(int mask, int cell) => ((mask >> cell) & 1) == 1;

    public static int Count(int mask) => BitOperations.PopCount((uint)mask);

    /// <summary>How many bees touch the cell.</summary>
    public static int Clue(int cell, int bees) => Count(Neighbors[cell] & bees);
}

/// <summary>What the board shows: the open honey cells, their clues, and the order they opened in.</summary>
public sealed class HiveInfo
{
    public int Opened { get; private set; }

    /// <summary>Per cell: its clue once open, -1 while closed.</summary>
    public int[] Clues { get; } = Enumerable.Repeat(-1, HiveBoard.CellCount).ToArray();

    public List<int> Order { get; } = [];

    /// <summary>
    /// Open a honey cell. A clue of 0 means no bee touches it, so its neighbours open too, and so on: they're certain
    /// honey (boost ×1), so the fair value doesn't move. Same breadth-first order as the frontend's openCell.
    /// </summary>
    public void Open(int cell, int bees)
    {
        var queue = new Queue<int>();
        queue.Enqueue(cell);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (HiveBoard.Has(Opened, c)) continue;
            var clue = HiveBoard.Clue(c, bees);
            Opened |= 1 << c;
            Clues[c] = clue;
            Order.Add(c);
            if (clue != 0) continue;
            for (var n = 0; n < HiveBoard.CellCount; n++)
                if (HiveBoard.Has(HiveBoard.Neighbors[c], n) && !HiveBoard.Has(Opened, n)) queue.Enqueue(n);
        }
    }
}

/// <summary>
/// HIVE MIND (Engine = Hive): bees hide in 3, 5, 7 or 10 of the 19 cells (the player's choice), every layout equally
/// likely. An open honey cell shows its clue, how many bees touch it. Before each pick, the odds of every closed cell
/// come from counting the bee layouts that agree with every clue on the board:
///     P(honey) = layouts with honey in that cell / layouts that fit the clues
/// Picking honey multiplies the round's fair value by 1 / P, the cell's boost; a bee ends the round with nothing paid.
/// The RTP is applied once, to the whole round:
///     multiplier = floor(970 × Π layouts / Π layouts-with-honey-there) / 1000      (capped at ×500)
/// The fair value is a martingale whatever the player picks, clues included, so no strategy returns more than 97%.
/// A cell the clues prove safe has boost ×1. The payout is bet × multiplier, floored to the cent.
///
/// Must match the frontend's src/game/math.ts (HiveMind) exactly.
/// </summary>
public static class HiveRules
{
    public const decimal Rtp = 0.97m;
    private const int RtpMilli = 970;

    /// <summary>Top prize. Reaching it settles the round.</summary>
    public const decimal MaxMultiplier = 500m;
    private static readonly BigInteger MaxMilli = new(MaxMultiplier * 1000m);

    public static readonly int[] Levels = [3, 5, 7, 10];

    /// <summary>Every way to hide that many bees, as cell masks: C(19, bees), from 969 (3 bees) to 92,378 (10).</summary>
    private static readonly Dictionary<int, int[]> Layouts = Levels.ToDictionary(level => level, level =>
        Enumerable.Range(0, HiveBoard.AllCells + 1).Where(m => HiveBoard.Count(m) == level).ToArray());

    public static bool IsLevel(int bees) => Layouts.ContainsKey(bees);

    /// <summary>Count the layouts that fit the board, and how many of them put a bee in each cell.</summary>
    public static (int Total, int[] Bees) Odds(int level, HiveInfo info)
    {
        var near = new List<int>();
        var clue = new List<int>();
        for (var i = 0; i < HiveBoard.CellCount; i++)
        {
            if (!HiveBoard.Has(info.Opened, i)) continue;
            near.Add(HiveBoard.Neighbors[i] & ~info.Opened);
            clue.Add(info.Clues[i]);
        }
        var bees = new int[HiveBoard.CellCount];
        var total = 0;
        foreach (var m in Layouts[level])
        {
            if ((m & info.Opened) != 0) continue;
            var fits = true;
            for (var c = 0; c < near.Count && fits; c++) fits = HiveBoard.Count(m & near[c]) == clue[c];
            if (!fits) continue;
            total++;
            for (var x = (uint)m; x != 0; x &= x - 1) bees[BitOperations.TrailingZeroCount(x)]++;
        }
        return (total, bees);
    }

    /// <summary>Cash-out multiplier for a fair value num / den: 97% of it, floored to 0.001, capped at ×500.</summary>
    public static decimal Multiplier(BigInteger num, BigInteger den) =>
        (decimal)BigInteger.Min(RtpMilli * num / den, MaxMilli) / 1000m; // both positive, so the division floors

    public static bool IsMaxed(decimal multiplier) => multiplier >= MaxMultiplier;

    /// <summary>bet × multiplier, floored to the cent. Exact: the bet has 2 decimals and the multiplier 3.</summary>
    public static decimal Payout(decimal bet, decimal multiplier) => Math.Floor(bet * multiplier * 100m) / 100m;
}

/// <summary>
/// Provably fair bees. Before the bet the player sees SHA-256(serverSeed) (Shoe.Hash); afterwards the seed is revealed.
///   The cells start as the list 0…18. Bee k (0-based) takes the
///   Pick(HMAC-SHA256(key: serverSeed, message: "clientSeed:nonce:k"), cells left)-th cell still in the list.
///   Pick(mac, n) = (top 52 bits of mac as a big-endian integer × n) >> 52
/// </summary>
public static class HiveFair
{
    /// <summary>The cells the bees hide in, in the order they were drawn.</summary>
    public static int[] BeeCells(string serverSeed, string clientSeed, long nonce, int level)
    {
        var free = Enumerable.Range(0, HiveBoard.CellCount).ToList();
        var bees = new int[level];
        for (var k = 0; k < level; k++)
        {
            var j = Pick(Mac(serverSeed, $"{clientSeed}:{nonce}:{k}"), free.Count);
            bees[k] = free[j];
            free.RemoveAt(j);
        }
        return bees;
    }

    private static byte[] Mac(string key, string message) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message));

    private static int Pick(byte[] mac, int n) =>
        (int)(((BinaryPrimitives.ReadUInt64BigEndian(mac) >> 12) * (ulong)n) >> 52);
}
