using System.Collections.Generic;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Splatter.Pmc.Tests
{
    /// <summary>Table-driven unit tests for PmcQueue, PmcVote and PmcRotation.
    /// Port of the Godot tests/suites/test_lobby.gd.</summary>
    [TestFixture]
    public class LobbyQueueTests
    {
        private static PmcQueue Q(int[] ids)
        {
            var q = new PmcQueue();
            foreach (int id in ids)
                q.Push(id);
            return q;
        }

        [Test]
        public void Basics()
        {
            var q = Q(new[] { 1, 2, 3 });
            q.Push(2);
            Assert.AreEqual(new[] { 1, 2, 3 }, q.Ids().ToArray(), "push ignores duplicates");
            Assert.AreEqual(2, q.Position(3), "position");
            Assert.AreEqual(-1, q.Position(9), "position of absent");
            q.Remove(2);
            Assert.AreEqual(new[] { 1, 3 }, q.Ids().ToArray(), "remove");
            q.Remove(42);
            Assert.AreEqual(2, q.Size, "remove absent is a no-op");
            q.PushFront(3);
            Assert.AreEqual(new[] { 3, 1 }, q.Ids().ToArray(), "push_front moves existing");
            q.Clear();
            Assert.AreEqual(0, q.Size, "clear");
            Assert.IsFalse(q.Has(1), "has after clear");
        }

        [Test]
        public void PopNextTable()
        {
            // [queue, n, ineligible ids, expected popped, expected remaining]
            var cases = new[]
            {
                new { Q = new[] { 1, 2, 3, 4 }, N = 2, Bad = new int[0], Want = new[] { 1, 2 }, Left = new[] { 3, 4 } },
                new { Q = new[] { 1, 2, 3, 4 }, N = 2, Bad = new[] { 1 }, Want = new[] { 2, 3 }, Left = new[] { 1, 4 } },
                new { Q = new[] { 1, 2, 3, 4 }, N = 2, Bad = new[] { 1, 2, 3 }, Want = new[] { 4 }, Left = new[] { 1, 2, 3 } },
                new { Q = new[] { 1, 2, 3 }, N = 5, Bad = new int[0], Want = new[] { 1, 2, 3 }, Left = new int[0] },
                new { Q = new[] { 1, 2, 3 }, N = 0, Bad = new int[0], Want = new int[0], Left = new[] { 1, 2, 3 } },
                new { Q = new int[0], N = 2, Bad = new int[0], Want = new int[0], Left = new int[0] },
                new { Q = new[] { 5, 6, 7 }, N = 1, Bad = new[] { 5, 6, 7 }, Want = new int[0], Left = new[] { 5, 6, 7 } },
            };
            foreach (var c in cases)
            {
                var qq = Q(c.Q);
                var bad = new HashSet<int>(c.Bad);
                var got = qq.PopNext(c.N, id => !bad.Contains(id));
                Assert.AreEqual(c.Want, got.ToArray(), $"pop_next [{string.Join(",", c.Q)}] n={c.N}");
                Assert.AreEqual(c.Left, qq.Ids().ToArray(), "remaining after pop_next");
            }
            var q2 = Q(new[] { 1, 2 });
            Assert.AreEqual(new[] { 1 }, q2.PopNext(1).ToArray(), "null predicate = all eligible");
        }
    }

    [TestFixture]
    public class LobbyVoteTests
    {
        [Test]
        public void VoteTable()
        {
            // [eligible, vetoers, actions, expected decided, expected yes, expected no]
            // actions: ("y", id) / ("n", id) / ("v", id) / ("x", nowMs)
            var cases = new[]
            {
                new { El = new[] { 1, 2, 3 }, Veto = V(), A = A("y", 1, "y", 2), D = "approved", Y = 2, N = 0 },
                new { El = new[] { 1, 2, 3 }, Veto = V(), A = A("y", 1), D = "", Y = 1, N = 0 },
                new { El = new[] { 1, 2, 3, 4 }, Veto = V(), A = A("y", 1, "y", 2), D = "", Y = 2, N = 0 },       // 2 of 4 is not > half
                new { El = new[] { 1, 2, 3, 4 }, Veto = V(), A = A("y", 1, "y", 2, "y", 3), D = "approved", Y = 3, N = 0 },
                new { El = new[] { 1, 2, 3 }, Veto = V(), A = A("n", 1, "n", 2), D = "rejected", Y = 0, N = 2 },
                new { El = new[] { 1, 2, 3 }, Veto = V(), A = A("y", 1, "n", 1, "n", 2), D = "rejected", Y = 0, N = 2 },  // vote change
                new { El = new[] { 1, 2, 3 }, Veto = V(), A = A("y", 9), D = "", Y = 0, N = 0 },                     // not eligible
                new { El = new[] { 1, 2, 3 }, Veto = V(3, 1), A = A("y", 1, "v", 3), D = "vetoed", Y = 1, N = 0 },
                new { El = new[] { 1, 2, 3 }, Veto = V(), A = A("v", 3), D = "", Y = 0, N = 0 },                     // no vetoes
                new { El = new[] { 1, 2, 3 }, Veto = V(), A = A("y", 1, "x", 999), D = "", Y = 1, N = 0 },         // not due yet
                new { El = new[] { 1, 2, 3 }, Veto = V(), A = A("y", 1, "x", 1000), D = "approved", Y = 1, N = 0 },
                new { El = new[] { 1, 2, 3, 4 }, Veto = V(), A = A("y", 1, "n", 2, "x", 2000), D = "approved", Y = 1, N = 1 },  // tie approves
                new { El = new[] { 1, 2, 3, 4 }, Veto = V(), A = A("n", 2, "x", 2000), D = "rejected", Y = 0, N = 1 },
                new { El = new[] { 1, 2, 3 }, Veto = V(), A = A("x", 1000), D = "approved", Y = 0, N = 0 },          // nobody voted: 0 >= 0
                new { El = new int[0], Veto = V(), A = A("x", 1000), D = "approved", Y = 0, N = 0 },
                new { El = new[] { 1, 2, 3 }, Veto = V(), A = A("y", 1, "y", 2, "n", 3), D = "approved", Y = 2, N = 0 },  // locked: late vote refused
            };
            for (int i = 0; i < cases.Length; i++)
            {
                var c = cases[i];
                var v = new PmcVote();
                v.Open(100 + i, new List<int>(c.El), 1000, c.Veto);
                foreach (var a in c.A)
                {
                    switch (a.Item1)
                    {
                        case "y": v.Cast(a.Item2, true); break;
                        case "n": v.Cast(a.Item2, false); break;
                        case "v": v.Veto(a.Item2); break;
                        case "x": v.Expire(a.Item2); break;
                    }
                }
                var tl = v.Tally();
                Assert.AreEqual(c.D, tl["decided"], $"case {i} decided");
                Assert.AreEqual(c.Y, tl["yes"], $"case {i} yes");
                Assert.AreEqual(c.N, tl["no"], $"case {i} no");
                Assert.AreEqual(c.El.Length, tl["eligible"], $"case {i} eligible");
            }
        }

        private static (string, int)[] A(params object[] acts)
        {
            var list = new List<(string, int)>();
            for (int i = 0; i < acts.Length; i += 2)
                list.Add(((string)acts[i], (int)acts[i + 1]));
            return list.ToArray();
        }

        private static Dictionary<int, int> V(params int[] idAndCount)
        {
            var d = new Dictionary<int, int>();
            for (int i = 0; i < idAndCount.Length; i += 2)
                d[idAndCount[i]] = idAndCount[i + 1];
            return d;
        }

        [Test]
        public void VetoReproposes()
        {
            var v = new PmcVote();
            v.Open(1, new List<int> { 1, 2, 3 }, 1000, new Dictionary<int, int> { { 3, 1 } });
            Assert.IsTrue(v.Cast(1, true), "cast accepted");
            Assert.IsTrue(v.Veto(3), "veto accepted");
            Assert.IsFalse(v.Cast(2, true), "cast refused after veto");
            Assert.AreEqual("vetoed", v.Expire(5000), "expire keeps vetoed");
            v.Repropose(2, 3000);
            var tl = v.Tally();
            Assert.AreEqual(0, tl["yes"]);
            Assert.AreEqual(0, tl["no"]);
            Assert.AreEqual(3, tl["eligible"]);
            Assert.AreEqual("", tl["decided"]);
            Assert.AreEqual(2, tl["proposal_id"]);
            Assert.AreEqual(2, v.ProposalId);
            Assert.AreEqual(3000, v.EndsMs);
            Assert.AreEqual(0, v.VetoesLeft(3), "veto consumed");
            Assert.IsFalse(v.Veto(3), "no vetoes left");
            Assert.IsTrue(v.Cast(1, true) && v.Cast(2, true), "votes on new proposal");
            Assert.AreEqual("approved", v.Tally()["decided"], "new proposal approved");
            Assert.IsFalse(v.IsOpen(), "closed after decision");
        }

        [Test]
        public void RemoveVoter()
        {
            var v = new PmcVote();
            v.Open(1, new List<int> { 1, 2, 3, 4 }, 1000);
            v.Cast(1, true);
            v.Cast(2, true);
            Assert.AreEqual("", v.Tally()["decided"], "2/4 undecided");
            v.RemoveVoter(4);
            Assert.AreEqual("approved", v.Tally()["decided"], "2/3 approves after a voter leaves");
        }
    }

    [TestFixture]
    public class LobbyRotationTests
    {
        private const PmcRotation.Policy Ws = PmcRotation.Policy.WinnerStays;
        private const PmcRotation.Policy Ls = PmcRotation.Policy.LoserStays;
        private const PmcRotation.Policy St = PmcRotation.Policy.Strict;

        private static PmcQueue Q(int[] ids)
        {
            var q = new PmcQueue();
            foreach (int id in ids)
                q.Push(id);
            return q;
        }

        [Test]
        public void RotationTable()
        {
            // [policy, lastBlue, lastRed, winner, streaksIn, queueIn, ineligible, expected pair, streaksOut, queueOut]
            var cases = new[]
            {
                C(Ws, -1, -1, -1, D(), new[] { 1, 2, 3 }, new int[0], new[] { 1, 2 }, D(), new[] { 3 }),                          // first match
                C(Ws, 1, 2, 1, D(), new[] { 3, 4 }, new int[0], new[] { 1, 3 }, D(1, 1), new[] { 4, 2 }),                         // winner stays blue
                C(Ws, 1, 2, 2, D(), new[] { 3, 4 }, new int[0], new[] { 3, 2 }, D(2, 1), new[] { 4, 1 }),                         // winner stays red
                C(Ws, 1, 3, 1, D(1, 1), new[] { 4, 2 }, new int[0], new[] { 4, 2 }, D(), new[] { 3, 1 }),                         // streak 2 reached: both go
                C(Ws, 1, 2, 1, D(), new[] { 3, 4 }, new[] { 1 }, new[] { 3, 4 }, D(), new[] { 2, 1 }),                            // winner ineligible
                C(Ws, 1, 2, 1, D(), new int[0], new int[0], new[] { 1, 2 }, D(1, 1), new int[0]),                                 // two players only
                C(Ws, 1, 2, 1, D(1, 1), new int[0], new int[0], new[] { 2, 1 }, D(), new int[0]),                                 // two players, streak maxed
                C(Ws, 1, 2, -1, D(1, 1), new[] { 3, 4 }, new int[0], new[] { 3, 4 }, D(), new[] { 1, 2 }),                        // draw rotates both
                C(Ws, 1, 2, 7, D(), new[] { 3, 4 }, new int[0], new[] { 3, 4 }, D(), new[] { 1, 2 }),                             // winner not on court = draw
                C(Ws, 1, 2, 1, D(), new[] { 3, 4 }, new[] { 3 }, new[] { 1, 4 }, D(1, 1), new[] { 3, 2 }),                        // skip ineligible, keeps spot
                C(Ws, 1, 2, 1, D(), new[] { 3 }, new[] { 3 }, new[] { 1, 2 }, D(1, 1), new[] { 3 }),                              // loser is the only eligible opponent
                C(Ws, 1, 2, 1, D(), new[] { 3 }, new[] { 1, 3 }, new int[0], D(), new[] { 3, 2, 1 }),                             // not enough eligible, order kept
                C(Ws, 1, 2, 1, D(), new int[0], new[] { 2 }, new int[0], D(), new[] { 1, 2 }),                                    // stayer has no opponent: queued at front
                C(Ls, 1, 2, 1, D(), new[] { 3, 4 }, new int[0], new[] { 3, 2 }, D(2, 1), new[] { 4, 1 }),                         // loser stays
                C(Ls, 1, 3, 1, D(3, 1), new[] { 4, 2 }, new int[0], new[] { 4, 2 }, D(), new[] { 1, 3 }),                         // loser streak maxed
                C(St, 1, 2, 1, D(1, 1), new[] { 3, 4 }, new int[0], new[] { 3, 4 }, D(), new[] { 1, 2 }),                         // strict
                C(St, 1, 2, 1, D(), new int[0], new int[0], new[] { 1, 2 }, D(), new int[0]),                                     // strict, two players
                C(Ws, 1, 2, 1, D(1, 5), new[] { 3 }, new int[0], new[] { 1, 3 }, D(1, 6), new[] { 2 }),                           // max_streak 0 = unlimited (last case)
            };
            for (int i = 0; i < cases.Length; i++)
            {
                var c = cases[i];
                var streaks = new Dictionary<int, int>(c.StreaksIn);
                var q = Q(c.QueueIn);
                var bad = new HashSet<int>(c.Ineligible);
                int maxStreak = i == cases.Length - 1 ? 0 : 2;
                var pair = PmcRotation.Next(c.Pol, c.LastBlue, c.LastRed, c.Winner, streaks, q,
                    id => !bad.Contains(id), maxStreak);
                Assert.AreEqual(c.Want, pair, $"case {i} pair");
                Assert.AreEqual(c.StreaksOut.Count, streaks.Count, $"case {i} streak count");
                foreach (var kv in c.StreaksOut)
                    Assert.AreEqual(kv.Value, streaks[kv.Key], $"case {i} streak[{kv.Key}]");
                Assert.AreEqual(c.QueueOut, q.Ids().ToArray(), $"case {i} queue");
            }
        }

        private sealed class Case
        {
            public PmcRotation.Policy Pol;
            public int LastBlue;
            public int LastRed;
            public int Winner;
            public Dictionary<int, int> StreaksIn;
            public int[] QueueIn;
            public int[] Ineligible;
            public int[] Want;
            public Dictionary<int, int> StreaksOut;
            public int[] QueueOut;
        }

        private static Case C(PmcRotation.Policy pol, int lb, int lr, int w, Dictionary<int, int> sin,
            int[] qin, int[] bad, int[] want, Dictionary<int, int> sout, int[] qout)
        {
            return new Case { Pol = pol, LastBlue = lb, LastRed = lr, Winner = w, StreaksIn = sin, QueueIn = qin, Ineligible = bad, Want = want, StreaksOut = sout, QueueOut = qout };
        }

        private static Dictionary<int, int> D(params int[] idAndStreak)
        {
            var d = new Dictionary<int, int>();
            for (int i = 0; i < idAndStreak.Length; i += 2)
                d[idAndStreak[i]] = idAndStreak[i + 1];
            return d;
        }

        [Test]
        public void WinnerStaysSequence()
        {
            var q = Q(new[] { 1, 2, 3, 4 });
            var streaks = new Dictionary<int, int>();
            var pair = PmcRotation.Next(Ws, -1, -1, -1, streaks, q);
            var log = new List<int[]> { pair };
            foreach (int w in new[] { 0, 0, 1, 1 })  // index into pair of who wins
            {
                pair = PmcRotation.Next(Ws, pair[0], pair[1], pair[w], streaks, q);
                log.Add(pair);
            }
            // 1v2 (1 wins) -> 1v3 (1 wins, streak 2: both out) -> 4v2 (2 wins) -> 3v2 (2 wins, streak 2: both out) -> 1v4
            var want = new[] { new[] { 1, 2 }, new[] { 1, 3 }, new[] { 4, 2 }, new[] { 3, 2 }, new[] { 1, 4 } };
            Assert.AreEqual(want.Length, log.Count);
            for (int i = 0; i < want.Length; i++)
                Assert.AreEqual(want[i], log[i], $"match {i}");
        }
    }
}
