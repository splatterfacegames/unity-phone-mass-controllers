using System;
using System.Collections.Generic;

namespace Splatter.Pmc.Demo
{
    /// <summary>
    /// Buzzer Party rules, as pure logic (no networking, no MonoBehaviours). Time is passed in as
    /// msec so tests can drive it deterministically. Port of demo/buzzer_game.gd.
    ///
    /// Each round every player privately gets a secret symbol. The big screen flashes symbols one
    /// after another. Buzz while YOUR symbol is showing: the first correct buzz wins the round.
    /// A wrong buzz locks that phone out for a moment. First to <see cref="TargetScore"/> wins the match.
    /// </summary>
    public sealed class BuzzerGame
    {
        /// <summary>Result of a <see cref="Buzz"/> call.</summary>
        public enum BuzzResult
        {
            Win,
            Wrong,
            Locked,
            Idle,
        }

        /// <summary>"lobby" | "round" | "reveal" | "over" — the wire names match the lowercase names.</summary>
        public enum Phase
        {
            Lobby,
            Round,
            Reveal,
            Over,
        }

        /// <summary>One event from <see cref="Tick"/>: a new flash, or the round timing out.</summary>
        public struct TickEvent
        {
            public string Type;   // "flash" | "timeout"
            public int Symbol;    // symbol index for "flash"
            public int Seq;       // flash sequence number for "flash"

            public static TickEvent Flash(int symbol, int seq)
            {
                return new TickEvent { Type = "flash", Symbol = symbol, Seq = seq };
            }

            public static readonly TickEvent Timeout = new TickEvent { Type = "timeout", Symbol = -1, Seq = 0 };
        }

        /// <summary>Outcome of a <see cref="Buzz"/> call.</summary>
        public struct BuzzOutcome
        {
            public BuzzResult Result;
            public long LockedMs;
        }

        /// <summary>A playable symbol: what the big screen draws and what phones get as their secret.</summary>
        public sealed class SymbolDef
        {
            public readonly int Id;
            public readonly string Label;
            public readonly string Color;   // "#rrggbb"
            public readonly string Shape;   // "circle" | "square" | "triangle" | "star" | "diamond" | "hexagon" | "heart" | "ring"

            public SymbolDef(int id, string label, string color, string shape)
            {
                Id = id;
                Label = label;
                Color = color;
                Shape = shape;
            }
        }

        public static readonly SymbolDef[] Symbols =
        {
            new SymbolDef(0, "Red Circle", "#ff5a5f", "circle"),
            new SymbolDef(1, "Blue Square", "#3d8bfd", "square"),
            new SymbolDef(2, "Green Triangle", "#2ec27e", "triangle"),
            new SymbolDef(3, "Yellow Star", "#ffc53d", "star"),
            new SymbolDef(4, "Purple Diamond", "#a371f7", "diamond"),
            new SymbolDef(5, "Orange Hexagon", "#ff8c42", "hexagon"),
            new SymbolDef(6, "Pink Heart", "#ff6fb5", "heart"),
            new SymbolDef(7, "Teal Ring", "#26c6da", "ring"),
        };

        public Phase GamePhase = Phase.Lobby;
        public int RoundNo = 0;
        public int TargetScore = 5;
        public int FlashMs = 1400;
        public long LeadMs = 1500;           // "get ready" pause before the first flash
        public int MaxFlashes = 30;          // round ends with no winner after this many
        public long LockoutMs = 1500;
        public long LateMs = 250;            // a buzz this soon after a flash changes still counts for the previous one

        public readonly Dictionary<int, int> Scores = new Dictionary<int, int>();       // player id -> int. Kept for the whole match, so rejoin keeps the score.
        public readonly Dictionary<int, int> Secrets = new Dictionary<int, int>();      // player id -> symbol index (current round)
        public readonly Dictionary<int, long> LockedUntil = new Dictionary<int, long>(); // player id -> msec
        public int WinnerId = -1;
        public int MatchWinnerId = -1;
        public int FlashSymbol = -1;         // symbol index on screen, -1 = none
        public int FlashSeq = 0;

        /// <summary>A late-arriving earlier tap can still take the win within this window (ms).</summary>
        public const long StealMs = 400;

        private struct FlashMark
        {
            public long Ms;
            public int Symbol;
        }

        private readonly Random _rng;
        private readonly List<FlashMark> _flashLog = new List<FlashMark>(); // [msec, symbol] per change this round; lets us judge a buzz at its own timestamp
        private long _nextFlashMsec = 0;
        private long _winAtMsec = 0;
        private long _winSeenMsec = -1;      // -1 = nobody has won this round yet

        public BuzzerGame(int seedValue = 0)
        {
            _rng = seedValue != 0 ? new Random(seedValue) : new Random();
        }

        /// <summary>Wire name of <see cref="GamePhase"/> — "lobby" | "round" | "reveal" | "over".</summary>
        public string PhaseName
        {
            get
            {
                switch (GamePhase)
                {
                    case Phase.Round: return "round";
                    case Phase.Reveal: return "reveal";
                    case Phase.Over: return "over";
                    default: return "lobby";
                }
            }
        }

        /// <summary>Makes sure a player has a score entry.</summary>
        public void AddPlayer(int id)
        {
            if (!Scores.ContainsKey(id))
                Scores[id] = 0;
        }

        /// <summary>
        /// Starts a round for <paramref name="ids"/>. Secrets are distinct while there are enough
        /// symbols. Returns false if nobody can play.
        /// </summary>
        public bool StartRound(IList<int> ids, long nowMsec)
        {
            if (ids == null || ids.Count == 0)
                return false;
            if (GamePhase == Phase.Over)
                ResetMatch();
            RoundNo += 1;
            GamePhase = Phase.Round;
            WinnerId = -1;
            Secrets.Clear();
            LockedUntil.Clear();
            FlashSymbol = -1;
            FlashSeq = 0;
            _winSeenMsec = -1;
            _flashLog.Clear();
            _flashLog.Add(new FlashMark { Ms = nowMsec, Symbol = -1 });
            List<int> bag = ShuffledSymbols();
            for (int i = 0; i < ids.Count; i++)
            {
                AddPlayer(ids[i]);
                if (i > 0 && i % bag.Count == 0)
                    bag = ShuffledSymbols();
                Secrets[ids[i]] = bag[i % bag.Count];
            }
            _nextFlashMsec = nowMsec + LeadMs;
            return true;
        }

        /// <summary>
        /// Gives a player who joined mid-round a secret. Returns the symbol index, or -1 outside a round.
        /// </summary>
        public int AssignLateSecret(int id)
        {
            AddPlayer(id);
            if (GamePhase != Phase.Round)
                return -1;
            if (!Secrets.ContainsKey(id))
            {
                var used = new HashSet<int>(Secrets.Values);
                var free = new List<int>();
                for (int i = 0; i < Symbols.Length; i++)
                {
                    if (!used.Contains(i))
                        free.Add(i);
                }
                Secrets[id] = free.Count > 0 ? free[_rng.Next(free.Count)] : _rng.Next(Symbols.Length);
            }
            return Secrets[id];
        }

        /// <summary>
        /// Advances the flash cycle. Returns events: "flash" (symbol/seq set) and "timeout" when the
        /// round ends with no winner.
        /// </summary>
        public List<TickEvent> Tick(long nowMsec)
        {
            var events = new List<TickEvent>();
            if (GamePhase != Phase.Round || nowMsec < _nextFlashMsec)
                return events;
            if (FlashSeq >= MaxFlashes)
            {
                GamePhase = Phase.Reveal;
                FlashSymbol = -1;
                events.Add(TickEvent.Timeout);
                return events;
            }
            FlashSymbol = PickFlash();
            FlashSeq += 1;
            _flashLog.Add(new FlashMark { Ms = nowMsec, Symbol = FlashSymbol });
            _nextFlashMsec = nowMsec + FlashMs;
            events.Add(TickEvent.Flash(FlashSymbol, FlashSeq));
            return events;
        }

        /// <summary>
        /// Handles a buzz. <paramref name="atMsec"/> is when the phone says it tapped, on the host
        /// clock — clamp it to <paramref name="seenMsec"/> - rtt at the edge so backdating stays
        /// bounded by latency. A correct buzz tapped before the winner's but arriving within
        /// <see cref="StealMs"/> of the win steals the round (first tap wins, not first packet).
        /// A match-ending win is final.
        /// </summary>
        public BuzzOutcome Buzz(int id, long atMsec, long seenMsec = -1)
        {
            if (seenMsec < 0)
                seenMsec = atMsec;
            if (GamePhase == Phase.Reveal && _winSeenMsec >= 0)
            {
                long locked;
                if (Secrets.ContainsKey(id) && atMsec < _winAtMsec && seenMsec - _winSeenMsec <= StealMs
                        && seenMsec >= (LockedUntil.TryGetValue(id, out locked) ? locked : 0)
                        && Hit(id, atMsec, seenMsec))
                {
                    Scores[WinnerId] = Scores[WinnerId] - 1;
                    Scores[id] = (Scores.TryGetValue(id, out int s) ? s : 0) + 1;
                    WinnerId = id;
                    _winAtMsec = atMsec;
                    _winSeenMsec = seenMsec;
                    if (Scores[id] >= TargetScore)
                    {
                        GamePhase = Phase.Over;
                        MatchWinnerId = id;
                    }
                    return new BuzzOutcome { Result = BuzzResult.Win, LockedMs = 0 };
                }
                return new BuzzOutcome { Result = BuzzResult.Idle, LockedMs = 0 };
            }
            if (GamePhase != Phase.Round || !Secrets.ContainsKey(id) || FlashSymbol == -1)
                return new BuzzOutcome { Result = BuzzResult.Idle, LockedMs = 0 };
            long until = LockedUntil.TryGetValue(id, out long u) ? u : 0;
            if (seenMsec < until)
                return new BuzzOutcome { Result = BuzzResult.Locked, LockedMs = until - seenMsec };
            if (!Hit(id, atMsec, seenMsec))
            {
                LockedUntil[id] = seenMsec + LockoutMs;
                return new BuzzOutcome { Result = BuzzResult.Wrong, LockedMs = LockoutMs };
            }
            Scores[id] = (Scores.TryGetValue(id, out int sc) ? sc : 0) + 1;
            WinnerId = id;
            _winAtMsec = atMsec;
            _winSeenMsec = seenMsec;
            FlashSymbol = -1;
            if (Scores[id] >= TargetScore)
            {
                GamePhase = Phase.Over;
                MatchWinnerId = id;
            }
            else
            {
                GamePhase = Phase.Reveal;
            }
            return new BuzzOutcome { Result = BuzzResult.Win, LockedMs = 0 };
        }

        /// <summary>
        /// Was <paramref name="id"/>'s secret on screen at <paramref name="atMsec"/> (with
        /// <see cref="LateMs"/> grace for a tap that lands right as the flash changes)?
        /// <paramref name="seenMsec"/> caps how far ahead a claim can reach.
        /// </summary>
        private bool Hit(int id, long atMsec, long seenMsec)
        {
            int mine = Secrets.TryGetValue(id, out int s) ? s : -1;
            if (mine < 0)
                return false;
            // The live flash covers taps since it appeared (and a FlashSymbol a host set directly).
            if (mine == FlashSymbol && _flashLog[_flashLog.Count - 1].Ms <= atMsec && atMsec <= seenMsec)
                return true;
            int i = SymbolIdxAt(atMsec);
            if (i >= 0 && _flashLog[i].Symbol == mine)
                return true;
            return i > 0 && _flashLog[i - 1].Symbol == mine && atMsec - _flashLog[i].Ms <= LateMs;
        }

        /// <summary>Index into _flashLog of the symbol showing at <paramref name="atMsec"/>; -1 if before the round.</summary>
        private int SymbolIdxAt(long atMsec)
        {
            for (int i = _flashLog.Count - 1; i >= 0; i--)
            {
                if (_flashLog[i].Ms <= atMsec)
                    return i;
            }
            return -1;
        }

        /// <summary>Back to the lobby with all scores at zero (players stay).</summary>
        public void ResetMatch()
        {
            foreach (int id in new List<int>(Scores.Keys))
                Scores[id] = 0;
            RoundNo = 0;
            GamePhase = Phase.Lobby;
            WinnerId = -1;
            MatchWinnerId = -1;
            Secrets.Clear();
            LockedUntil.Clear();
            FlashSymbol = -1;
        }

        /// <summary>Forget a player completely (e.g. kicked).</summary>
        public void RemovePlayer(int id)
        {
            Scores.Remove(id);
            Secrets.Remove(id);
            LockedUntil.Remove(id);
        }

        /// <summary>Players sorted by score, highest first, ties by id.</summary>
        public List<int> Ranking()
        {
            var ids = new List<int>(Scores.Keys);
            ids.Sort(delegate (int a, int b)
            {
                int sa = Scores[a], sb = Scores[b];
                if (sa != sb)
                    return sb - sa;
                return a - b;
            });
            return ids;
        }

        private List<int> ShuffledSymbols()
        {
            var bag = new List<int>(Symbols.Length);
            for (int i = 0; i < Symbols.Length; i++)
                bag.Add(i);
            for (int i = bag.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                int tmp = bag[i];
                bag[i] = bag[j];
                bag[j] = tmp;
            }
            return bag;
        }

        // Mostly symbols someone actually holds, with a few decoys; never the same symbol twice in a row.
        private int PickFlash()
        {
            var held = new List<int>();
            foreach (int s in Secrets.Values)
            {
                if (!held.Contains(s))
                    held.Add(s);
            }
            for (int tries = 0; tries < 8; tries++)
            {
                int pick;
                if (held.Count > 0 && _rng.NextDouble() < 0.6)
                    pick = held[_rng.Next(held.Count)];
                else
                    pick = _rng.Next(Symbols.Length);
                if (pick != FlashSymbol)
                    return pick;
            }
            return (FlashSymbol + 1) % Symbols.Length;
        }
    }
}
