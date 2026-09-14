using System.Collections.Generic;

namespace Splatter.Pmc
{
    /// <summary>
    /// A proposal vote with majority lock-in, vetoes and a timeout. Pure logic, no networking.
    ///
    /// Rules:
    /// - It's "approved" as soon as yes votes are more than half of the eligible voters.
    /// - It's "rejected" as soon as no votes are more than half of the eligible voters.
    /// - A veto (from an id with vetoes left) ends the vote as "vetoed". Call
    ///   <see cref="Repropose"/> to open the next proposal with the same voters and the remaining vetoes.
    /// - At the deadline, <see cref="Expire"/> approves when yes &gt;= no and rejects otherwise.
    /// </summary>
    public sealed class PmcVote
    {
        /// <summary>Current proposal id, or -1 before <see cref="Open"/>.</summary>
        public int ProposalId = -1;
        /// <summary>Deadline in milliseconds, on the same clock you pass to <see cref="Expire"/>.</summary>
        public long EndsMs;

        private List<int> _eligible = new List<int>();
        private readonly Dictionary<int, bool> _votes = new Dictionary<int, bool>();
        private Dictionary<int, int> _vetoers = new Dictionary<int, int>();
        private string _decided = "";
        private bool _open;

        /// <summary>
        /// Starts a vote on <paramref name="proposalId"/>. Only ids in <paramref name="eligibleIds"/>
        /// may vote. <paramref name="vetoers"/> maps id to the number of vetoes it has left.
        /// The collections are copied.
        /// </summary>
        public void Open(int proposalId, List<int> eligibleIds, long endsMs, Dictionary<int, int> vetoers = null)
        {
            ProposalId = proposalId;
            EndsMs = endsMs;
            _eligible = new List<int>(eligibleIds);
            _vetoers = vetoers != null ? new Dictionary<int, int>(vetoers) : new Dictionary<int, int>();
            _votes.Clear();
            _decided = "";
            _open = true;
        }

        /// <summary>
        /// Re-opens voting on a new proposal after a veto (or any decision).
        /// Keeps the voters and the remaining vetoes.
        /// </summary>
        public void Repropose(int proposalId, long endsMs)
        {
            Open(proposalId, _eligible, endsMs, _vetoers);
        }

        /// <summary>Whether a vote is open and still undecided.</summary>
        public bool IsOpen()
        {
            return _open && _decided == "";
        }

        /// <summary>
        /// Records (or changes) <paramref name="id"/>'s vote.
        /// Returns false if <paramref name="id"/> isn't eligible or the vote is closed.
        /// </summary>
        public bool Cast(int id, bool approve)
        {
            if (!IsOpen() || !_eligible.Contains(id))
                return false;
            _votes[id] = approve;
            Evaluate();
            return true;
        }

        /// <summary>
        /// Uses one of <paramref name="id"/>'s vetoes.
        /// Returns false if it has none left or the vote is closed.
        /// </summary>
        public bool Veto(int id)
        {
            if (!IsOpen() || VetoesLeft(id) <= 0)
                return false;
            _vetoers[id] = _vetoers[id] - 1;
            _decided = "vetoed";
            return true;
        }

        /// <summary>Vetoes <paramref name="id"/> has left.</summary>
        public int VetoesLeft(int id)
        {
            return _vetoers.TryGetValue(id, out int left) ? left : 0;
        }

        /// <summary>
        /// Removes <paramref name="id"/> from the electorate (e.g. the player left),
        /// drops its vote and re-evaluates.
        /// </summary>
        public void RemoveVoter(int id)
        {
            _eligible.Remove(id);
            _votes.Remove(id);
            if (IsOpen())
                Evaluate();
        }

        /// <summary>
        /// {yes, no, eligible, decided, proposal_id}. decided is "", "approved", "rejected" or "vetoed".
        /// </summary>
        public Dictionary<string, object> Tally()
        {
            int yes = 0;
            int no = 0;
            foreach (var kv in _votes)
            {
                if (kv.Value)
                    yes++;
                else
                    no++;
            }
            return new Dictionary<string, object>
            {
                { "yes", yes },
                { "no", no },
                { "eligible", _eligible.Count },
                { "decided", _decided },
                { "proposal_id", ProposalId },
            };
        }

        /// <summary>
        /// Applies the timeout rule when <paramref name="nowMs"/> &gt;= <see cref="EndsMs"/>.
        /// Returns the decision, or "" while it's still open and not yet due.
        /// </summary>
        public string Expire(long nowMs)
        {
            if (IsOpen() && nowMs >= EndsMs)
            {
                var t = Tally();
                _decided = (int)t["yes"] >= (int)t["no"] ? "approved" : "rejected";
            }
            return _decided;
        }

        private void Evaluate()
        {
            var t = Tally();
            int n = (int)t["eligible"];
            if ((int)t["yes"] * 2 > n)
                _decided = "approved";
            else if ((int)t["no"] * 2 > n)
                _decided = "rejected";
        }
    }
}
