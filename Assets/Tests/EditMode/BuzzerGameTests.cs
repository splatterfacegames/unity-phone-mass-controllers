using System.Collections.Generic;
using NUnit.Framework;
using Splatter.Pmc.Demo;
using Buzz = Splatter.Pmc.Demo.BuzzerGame.BuzzResult;
using Phase = Splatter.Pmc.Demo.BuzzerGame.Phase;

namespace Splatter.Pmc.Tests
{
    /// <summary>
    /// Port of the Godot suite tests/suites/test_demo_buzzer.gd — same assertions against the
    /// C# port of demo/buzzer_game.gd.
    /// </summary>
    public class BuzzerGameTests
    {
        [Test]
        public void SecretsAreDistinctPerRound()
        {
            var g = new BuzzerGame(42);
            Assert.IsFalse(g.StartRound(new int[0], 0), "no players, no round");
            Assert.IsTrue(g.StartRound(new[] { 1, 2, 3, 4, 5, 6 }, 0), "round starts");
            Assert.AreEqual(Phase.Round, g.GamePhase);
            Assert.AreEqual(1, g.RoundNo);
            var seen = new HashSet<int>();
            foreach (int id in new[] { 1, 2, 3, 4, 5, 6 })
                seen.Add(g.Secrets[id]);
            Assert.AreEqual(6, seen.Count, "six distinct secrets");
        }

        [Test]
        public void MorePlayersThanSymbolsStillGetSecrets()
        {
            var big = new BuzzerGame(7);
            var ids = new List<int>();
            for (int i = 1; i <= 20; i++)
                ids.Add(i);
            big.StartRound(ids, 0);
            Assert.AreEqual(20, big.Secrets.Count);
        }

        [Test]
        public void NoFlashBeforeLeadTime()
        {
            var g = new BuzzerGame(42);
            g.StartRound(new[] { 1, 2, 3, 4, 5, 6 }, 0);
            Assert.AreEqual(0, g.Tick(100).Count, "still in lead");
            Assert.AreEqual(Buzz.Idle, g.Buzz(1, 100).Result, "buzz before any flash is ignored");
            var ev = g.Tick(1500);
            Assert.AreEqual(1, ev.Count);
            Assert.AreEqual("flash", ev[0].Type);
            Assert.AreEqual(1, g.FlashSeq);
        }

        [Test]
        public void WrongBuzzLocksOutThenCorrectBuzzWins()
        {
            var g = new BuzzerGame(42);
            g.StartRound(new[] { 1, 2, 3, 4, 5, 6 }, 0);
            g.Tick(1500);
            int shown = g.FlashSymbol;
            int wrongId = -1, rightId = -1;
            foreach (var kv in g.Secrets)
            {
                if (kv.Value == shown) rightId = kv.Key;
                else if (wrongId == -1) wrongId = kv.Key;
            }
            Assert.AreEqual(Buzz.Wrong, g.Buzz(wrongId, 1600).Result);
            var locked = g.Buzz(wrongId, 1700);
            Assert.AreEqual(Buzz.Locked, locked.Result);
            Assert.AreEqual(1400, locked.LockedMs);
            Assert.AreEqual(0, g.Scores[wrongId]);

            if (rightId == -1)
            {
                rightId = 1;
                g.FlashSymbol = g.Secrets[1];
            }
            Assert.AreEqual(Buzz.Win, g.Buzz(rightId, 1800).Result);
            Assert.AreEqual(1, g.Scores[rightId]);
            Assert.AreEqual(rightId, g.WinnerId);
            Assert.AreEqual(Phase.Reveal, g.GamePhase);
            Assert.AreEqual(Buzz.Idle, g.Buzz(wrongId, 1900).Result, "no buzzing after the round is won");
        }

        [Test]
        public void LateBuzzCountsForPreviousFlash()
        {
            var g = new BuzzerGame(42);
            g.StartRound(new[] { 1, 2 }, 10000);
            g.Tick(11500);
            int first = g.FlashSymbol;
            int holder = g.Secrets[1] == first ? 1 : g.Secrets[2] == first ? 2 : -1;
            if (holder == -1)
            {
                g.Secrets[1] = first;
                holder = 1;
            }
            g.Tick(12900);
            Assert.AreNotEqual(first, g.FlashSymbol, "flash changed");
            Assert.AreEqual(Buzz.Win, g.Buzz(holder, 13000).Result, "100 ms after the change still counts");
        }

        [Test]
        public void TimeoutWithNoWinner()
        {
            var q = new BuzzerGame(3);
            q.MaxFlashes = 3;
            q.StartRound(new[] { 1 }, 0);
            var events = new List<BuzzerGame.TickEvent>();
            long now = 0;
            while (q.GamePhase == Phase.Round && now < 100000)
            {
                now += 100;
                events.AddRange(q.Tick(now));
            }
            Assert.AreEqual(Phase.Reveal, q.GamePhase, "timeout -> reveal");
            Assert.AreEqual("timeout", events[events.Count - 1].Type);
            Assert.AreEqual(-1, q.WinnerId);
        }

        [Test]
        public void FlashNeverRepeatsBackToBack()
        {
            var r = new BuzzerGame(9);
            r.MaxFlashes = 200;
            r.StartRound(new[] { 1 }, 0);
            int last = -1, repeats = 0;
            for (int i = 0; i < 200; i++)
            {
                foreach (var e in r.Tick(1500 + i * 1400))
                {
                    if (e.Type != "flash")
                        continue;
                    if (e.Symbol == last)
                        repeats++;
                    last = e.Symbol;
                }
            }
            Assert.AreEqual(0, repeats, "no back-to-back repeats");
        }

        [Test]
        public void MatchWinAndReset()
        {
            var m = new BuzzerGame(5);
            m.TargetScore = 2;
            for (int i = 0; i < 2; i++)
            {
                m.StartRound(new[] { 1, 2 }, 0);
                m.Tick(1500);
                m.FlashSymbol = m.Secrets[1];
                m.Buzz(1, 1600);
            }
            Assert.AreEqual(Phase.Over, m.GamePhase);
            Assert.AreEqual(1, m.MatchWinnerId);
            var rank = m.Ranking();
            Assert.AreEqual(2, rank.Count);
            Assert.AreEqual(1, rank[0]);
            Assert.AreEqual(2, rank[1]);
            m.StartRound(new[] { 1, 2 }, 5000);
            Assert.AreEqual(0, m.Scores[1], "new match resets scores");
            Assert.AreEqual(1, m.RoundNo);
        }

        [Test]
        public void TimestampedBuzzCreditedAtTapTime()
        {
            var g = new BuzzerGame(42);
            g.StartRound(new[] { 1, 2 }, 0);
            g.Tick(1500);
            g.Secrets[1] = g.FlashSymbol;
            g.Secrets[2] = (g.FlashSymbol + 1) % BuzzerGame.Symbols.Length;
            g.Tick(2900); // the flash has moved on
            // p1 tapped at 2800 while their symbol still showed; the packet lands at 3050
            Assert.AreEqual(Buzz.Win, g.Buzz(1, 2800, 3050).Result, "tap-while-showing wins even when it arrives late");
            Assert.AreEqual(Buzz.Idle, g.Buzz(2, 8000, 8000).Result);
        }

        [Test]
        public void EarlierTapStealsInsideWindow()
        {
            var g = new BuzzerGame(9);
            g.StartRound(new[] { 1, 2 }, 0);
            g.Tick(1500);
            g.Secrets[1] = g.FlashSymbol;
            g.Secrets[2] = g.FlashSymbol; // shared symbol, as happens with more players than symbols
            Assert.AreEqual(Buzz.Win, g.Buzz(2, 1800, 1800).Result);
            Assert.AreEqual(2, g.WinnerId);
            Assert.AreEqual(Buzz.Win, g.Buzz(1, 1700, 1900).Result, "earlier tap wins although it arrived later");
            Assert.AreEqual(1, g.WinnerId);
            Assert.AreEqual(1, g.Scores[1]);
            Assert.AreEqual(0, g.Scores[2]);
        }

        [Test]
        public void LateTapsCannotSteal()
        {
            var g = new BuzzerGame(9);
            g.StartRound(new[] { 1, 2 }, 0);
            g.Tick(1500);
            g.Secrets[1] = g.FlashSymbol;
            g.Secrets[2] = g.FlashSymbol;
            Assert.AreEqual(Buzz.Win, g.Buzz(2, 1800, 1800).Result);
            Assert.AreEqual(Buzz.Idle, g.Buzz(1, 1900, 1900).Result, "a later tap doesn't steal");
            Assert.AreEqual(Buzz.Idle, g.Buzz(1, 1700, 1800 + BuzzerGame.StealMs + 1).Result, "outside the steal window");
            Assert.AreEqual(2, g.WinnerId);
        }

        [Test]
        public void StealAcrossDifferentFlashes()
        {
            var g = new BuzzerGame(11);
            g.StartRound(new[] { 1, 2 }, 0);
            g.Tick(1500);
            g.Secrets[1] = g.FlashSymbol; // p1's symbol shows first
            g.Tick(2900);
            g.Secrets[2] = g.FlashSymbol; // then p2's
            Assert.AreEqual(Buzz.Win, g.Buzz(2, 3000, 3000).Result);
            // p1 tapped way back at 1600, while their symbol was up; the packet only lands now.
            Assert.AreEqual(Buzz.Win, g.Buzz(1, 1600, 3200).Result, "a much earlier tap steals across flashes");
            Assert.AreEqual(1, g.WinnerId);
        }

        [Test]
        public void LateJoinerGetsUnusedSecret()
        {
            var l = new BuzzerGame(11);
            l.StartRound(new[] { 1, 2, 3 }, 0);
            int s = l.AssignLateSecret(9);
            Assert.GreaterOrEqual(s, 0);
            Assert.AreNotEqual(l.Secrets[1], s);
            Assert.AreNotEqual(l.Secrets[2], s);
            Assert.AreNotEqual(l.Secrets[3], s);
            Assert.AreEqual(s, l.AssignLateSecret(9), "stable on repeat");
            l.GamePhase = Phase.Reveal;
            Assert.AreEqual(-1, l.AssignLateSecret(10), "no secret outside a round");
        }
    }
}
