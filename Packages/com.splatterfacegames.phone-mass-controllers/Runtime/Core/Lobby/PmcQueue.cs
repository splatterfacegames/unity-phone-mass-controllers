using System;
using System.Collections.Generic;

namespace Splatter.Pmc
{
    /// <summary>
    /// Ordered play queue of player ids. Pure logic, no networking.
    ///
    /// Players wait in insertion order. <see cref="PopNext"/> takes the first eligible players and
    /// leaves ineligible ones (e.g. temporarily disconnected) in place, so they keep their spot.
    /// </summary>
    public sealed class PmcQueue
    {
        private readonly List<int> _ids = new List<int>();

        /// <summary>Number of queued ids.</summary>
        public int Size => _ids.Count;

        /// <summary>Appends <paramref name="id"/> to the back. Does nothing if it's already queued.</summary>
        public void Push(int id)
        {
            if (!_ids.Contains(id))
                _ids.Add(id);
        }

        /// <summary>Inserts <paramref name="id"/> at the front, or moves it there if it's already queued.</summary>
        public void PushFront(int id)
        {
            _ids.Remove(id);
            _ids.Insert(0, id);
        }

        /// <summary>Removes <paramref name="id"/> from the queue. Does nothing if it's absent.</summary>
        public void Remove(int id)
        {
            _ids.Remove(id);
        }

        /// <summary>Zero-based position of <paramref name="id"/>, or -1 when it isn't queued.</summary>
        public int Position(int id)
        {
            return _ids.IndexOf(id);
        }

        /// <summary>Whether <paramref name="id"/> is queued.</summary>
        public bool Has(int id)
        {
            return _ids.Contains(id);
        }

        /// <summary>Removes every id.</summary>
        public void Clear()
        {
            _ids.Clear();
        }

        /// <summary>A copy of the queued ids, front first.</summary>
        public List<int> Ids()
        {
            return new List<int>(_ids);
        }

        /// <summary>
        /// Removes and returns up to <paramref name="n"/> eligible ids from the front.
        /// <paramref name="isEligible"/> maps an id to whether it may be drawn; a null predicate
        /// treats everyone as eligible. Ineligible ids are skipped but stay where they are.
        /// </summary>
        public List<int> PopNext(int n, Func<int, bool> isEligible = null)
        {
            var outIds = new List<int>();
            if (n <= 0)
                return outIds;
            foreach (int id in _ids)
            {
                if (outIds.Count >= n)
                    break;
                if (isEligible == null || isEligible(id))
                    outIds.Add(id);
            }
            foreach (int id in outIds)
                _ids.Remove(id);
            return outIds;
        }
    }
}
