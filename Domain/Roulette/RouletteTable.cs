namespace Worldiator.Server.Domain.Roulette;

/// <summary>
/// The European (single-zero) roulette layout: every legal bet and what it pays. A bet is a type plus the
/// numbers it covers; it is only accepted if that exact set is a real position on the table (a split must be
/// two adjacent numbers, a corner four numbers meeting at one point, and so on), so the client can't invent
/// e.g. a "split" on 1 and 36.
/// Table layout: three rows (3,6..36 / 2,5..35 / 1,4..34) of twelve columns, zero on the left.
/// </summary>
public static class RouletteTable
{
    /// <summary>Odds paid to 1 by bet type. A winning bet returns Amount × (Payout + 1).</summary>
    public static readonly IReadOnlyDictionary<string, int> Payouts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["straight"]  = 35,   // 1 number
        ["split"]     = 17,   // 2 adjacent
        ["street"]    = 11,   // row of 3
        ["trio"]      = 11,   // 0-1-2 or 0-2-3
        ["corner"]    = 8,    // 4 meeting at a point
        ["four"]      = 8,    // 0-1-2-3 (first four)
        ["line"]      = 5,    // two streets, 6 numbers
        ["column"]    = 2,    // 12
        ["dozen"]     = 2,    // 12
        ["evenMoney"] = 1,    // red / black, odd / even, 1-18 / 19-36
    };

    private static readonly HashSet<int> Reds = [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];

    public static string ColorOf(int n) => n == 0 ? "green" : Reds.Contains(n) ? "red" : "black";

    public static bool IsBetType(string type) => Payouts.ContainsKey(type);

    /// <summary>The canonical spelling of a bet type (as in Payouts), or null if unknown.</summary>
    public static string? Canonical(string type) => Payouts.Keys.FirstOrDefault(k => string.Equals(k, type, StringComparison.OrdinalIgnoreCase));

    public static string Key(string type, IEnumerable<int> numbers) =>
        type.ToLowerInvariant() + ":" + string.Join('-', numbers.Order());

    public static bool IsValid(string type, IEnumerable<int> numbers) => IsBetType(type) && Positions.Contains(Key(type, numbers));

    private static readonly HashSet<string> Positions = BuildPositions();

    private static HashSet<string> BuildPositions()
    {
        var set = new HashSet<string>();
        void Add(string type, params int[] nums) => set.Add(Key(type, nums));
        static int At(int col, int row) => 3 * col + 3 - row;   // row 0 = top (3, 6 .. 36)

        for (var n = 0; n <= 36; n++) Add("straight", n);
        for (var c = 0; c < 12; c++)
        {
            for (var r = 0; r < 3; r++)
            {
                var n = At(c, r);
                if (c < 11) Add("split", n, n + 3);
                if (r < 2) Add("split", n, n - 1);
                if (c < 11 && r < 2) Add("corner", n, n - 1, n + 3, n + 2);
            }
            Add("street", 3 * c + 1, 3 * c + 2, 3 * c + 3);
            if (c < 11) Add("line", Enumerable.Range(3 * c + 1, 6).ToArray());
        }
        Add("split", 0, 1); Add("split", 0, 2); Add("split", 0, 3);
        Add("trio", 0, 1, 2); Add("trio", 0, 2, 3);
        Add("four", 0, 1, 2, 3);

        var all = Enumerable.Range(1, 36).ToArray();
        for (var k = 0; k < 3; k++)
        {
            Add("column", all.Where(n => n % 3 == (k + 1) % 3).ToArray());
            Add("dozen", Enumerable.Range(12 * k + 1, 12).ToArray());
        }
        Add("evenMoney", all.Where(n => n <= 18).ToArray());
        Add("evenMoney", all.Where(n => n >= 19).ToArray());
        Add("evenMoney", all.Where(n => n % 2 == 0).ToArray());
        Add("evenMoney", all.Where(n => n % 2 == 1).ToArray());
        Add("evenMoney", all.Where(Reds.Contains).ToArray());
        Add("evenMoney", all.Where(n => !Reds.Contains(n)).ToArray());
        return set;
    }
}
