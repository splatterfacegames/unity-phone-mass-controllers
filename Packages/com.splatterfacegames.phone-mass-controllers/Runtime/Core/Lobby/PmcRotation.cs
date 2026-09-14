using System;
using System.Collections.Generic;

namespace Splatter.Pmc
{
    /// <summary>
    /// Picks the next two players for a two-sided ("blue" vs "red") match. Pure logic, no networking.
    ///
    /// The <see cref="PmcQueue"/> holds waiting players only. Players on court aren't in it.
    /// - <see cref="Policy.WinnerStays"/>: the winner keeps their side and the loser goes to the back
    ///   of the queue. <c>streaks[winner]</c> counts consecutive wins. When it reaches
    ///   <c>maxStreak</c>, the winner goes back too (after the loser). With the default of 2, a
    ///   player plays at most two matches in a row by winning.
    /// - <see cref="Policy.LoserStays"/>: the same, with the roles swapped. The streak counts
    ///   consecutive losses.
    /// - <see cref="Policy.Strict"/>: both go back (blue first) and the next two come in.
    ///
    /// A draw (<paramref name="winner"/> isn't one of the two players) rotates both players, as
    /// <see cref="Policy.Strict"/> does. A stayer who isn't eligible anymore goes to the back of the
    /// queue. Leavers are queued before new players are drawn, so with only two players the same
    /// pair plays again. <c>maxStreak &lt;= 0</c> means no limit.
    /// </summary>
    public sealed class PmcRotation
    {
        /// <summary>Rotation policy.</summary>
        public enum Policy
        {
            /// <summary>Winner keeps their side.</summary>
            WinnerStays = 0,
            /// <summary>Loser keeps their side.</summary>
            LoserStays = 1,
            /// <summary>Both rotate out.</summary>
            Strict = 2,
        }

        /// <summary>
        /// Returns [blue, red] for the next match, or an empty array when fewer than two eligible
        /// players are available. In that case everyone from the last match is queued (a would-be
        /// stayer goes to the front) and all of their streaks are cleared.
        /// <paramref name="streaks"/> (id -&gt; streak) is updated in place: the stayer's count goes
        /// up and everyone who leaves the court is erased. Pass -1 for
        /// <paramref name="lastBlue"/>/<paramref name="lastRed"/> before the first match.
        /// </summary>
        public static int[] Next(Policy policy, int lastBlue, int lastRed, int winner,
            Dictionary<int, int> streaks, PmcQueue queue, Func<int, bool> isEligible = null, int maxStreak = 2)
        {
            Func<int, bool> eligible = id => id >= 0 && (isEligible == null || isEligible(id));

            int stayer = -1;
            var leavers = new List<int>();
            bool decided = winner >= 0 && (winner == lastBlue || winner == lastRed) && lastBlue >= 0 && lastRed >= 0;
            if (policy != Policy.Strict && decided)
            {
                int loser = winner == lastBlue ? lastRed : lastBlue;
                int candidate = policy == Policy.WinnerStays ? winner : loser;
                int other = policy == Policy.WinnerStays ? loser : winner;
                int streak = (streaks.TryGetValue(candidate, out int s) ? s : 0) + 1;
                leavers.Add(other);
                if ((maxStreak <= 0 || streak < maxStreak) && eligible(candidate))
                {
                    stayer = candidate;
                    streaks[candidate] = streak;
                }
                else
                {
                    leavers.Add(candidate);
                }
            }
            else
            {
                foreach (int id in new[] { lastBlue, lastRed })
                    if (id >= 0)
                        leavers.Add(id);
            }

            foreach (int id in leavers)
            {
                streaks.Remove(id);
                queue.Push(id);
            }

            int need = stayer >= 0 ? 1 : 2;
            int available = 0;
            foreach (int id in queue.Ids())
                if (eligible(id))
                    available++;
            if (available < need)
            {
                if (stayer >= 0)
                {
                    streaks.Remove(stayer);
                    queue.PushFront(stayer);
                }
                return new int[0];
            }

            var drawn = queue.PopNext(need, eligible);
            if (stayer < 0)
                return new[] { drawn[0], drawn[1] };
            if (stayer == lastBlue)
                return new[] { stayer, drawn[0] };
            return new[] { drawn[0], stayer };
        }
    }
}
