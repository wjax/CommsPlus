using System;
using System.Collections.Generic;
using System.Text;

namespace CommsLIBPlus.Helper
{
    public static class SpanExtension
    {
        public static ReadOnlySpan<char> SplitNext(this ref ReadOnlySpan<char> span, char seperator)
        {
            int pos = span.IndexOf(seperator);
            if (pos > -1)
            {
                var part = span.Slice(0, pos);
                span = span.Slice(pos + 1);
                return part;
            }
            else
            {
                var part = span;
                span = span.Slice(span.Length);
                return part;
            }
        }
    }
}
