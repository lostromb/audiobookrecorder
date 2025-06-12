using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudiobookRecorder
{
    public struct SilenceTime
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string SpeechAfterwards { get; set; }

        public SilenceTime(TimeSpan start, TimeSpan end, string speechAfterwards)
        {
            StartTime = start;
            EndTime = end;
            SpeechAfterwards = speechAfterwards;
        }

        public TimeSpan Length
        {
            get
            {
                return EndTime - StartTime;
            }
        }
    }
}
