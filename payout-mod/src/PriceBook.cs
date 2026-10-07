using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ScamWYF.RequestedPayout
{
    internal sealed class PriceBook
    {
        private sealed class IdentityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object left, object right) { return ReferenceEquals(left,right); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }
        private sealed class Quote { internal int Sequence; internal int Amount; }
        private readonly Dictionary<object,Dictionary<string,Quote>> quotes = new Dictionary<object,Dictionary<string,Quote>>(new IdentityComparer());
        private readonly object sync = new object();
        internal bool Record(object session, int sequence, int amount)
        {
            return Record(session,"credit-card",sequence,amount);
        }
        internal bool Record(object session,string scamId,int sequence,int amount)
        {
            if (session == null || string.IsNullOrEmpty(scamId) || sequence < 0 || amount <= 0) return false;
            lock (sync)
            {
                Dictionary<string,Quote> perScam;
                if(!quotes.TryGetValue(session,out perScam))
                {
                    perScam=new Dictionary<string,Quote>(StringComparer.Ordinal);
                    quotes[session]=perScam;
                }
                Quote old;
                // The native price objective completes once. Its first confirmed agreement is
                // immutable; recovered/duplicate evaluator results cannot silently re-price it.
                if (perScam.TryGetValue(scamId,out old)) return false;
                perScam[scamId] = new Quote {Sequence=sequence,Amount=amount};
                return true;
            }
        }
        internal bool TryGet(object session, out int amount)
        {
            return TryGet(session,"credit-card",out amount);
        }
        internal bool TryGet(object session,string scamId,out int amount)
        {
            amount=0; if (session==null || string.IsNullOrEmpty(scamId)) return false;
            lock(sync) {
                Dictionary<string,Quote> perScam;Quote quote;
                if (!quotes.TryGetValue(session,out perScam) || !perScam.TryGetValue(scamId,out quote)) return false;
                amount=quote.Amount; return true;
            }
        }
        internal void Remove(object session) { if(session!=null) lock(sync) quotes.Remove(session); }
        internal void Clear() { lock(sync) quotes.Clear(); }
    }
}
