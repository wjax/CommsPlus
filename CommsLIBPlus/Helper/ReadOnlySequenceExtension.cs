using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace CommsLIBPlus.Helper
{
    public static class ReadOnlySequenceExtension
    {
        public static SequencePosition GetPosition(this ref ReadOnlySequence<byte> seq, int position)
        {
            var reader = new SequenceReader<byte>(seq);
            reader.Advance(position);

            return reader.Position;
        }
    }
}
