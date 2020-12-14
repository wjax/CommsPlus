using NLog.Fluent;
using System;
using System.Collections.Generic;
using System.Text;

namespace CommsLIBPlus.FrameWrappers
{
    public interface Identifiable
    {
        uint ID { get; set; }
        uint AnswerToID { get; set; }
    }
}
