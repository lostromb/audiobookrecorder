using Durandal.Common.Speech.SR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudiobookRecorder
{
    public class SpeechRecoClosure
    {
        public TimeSpan SilenceStart { get; private set; }
        public TimeSpan SilenceEnd { get; private set; }
        public MeasuredAudioPipe Pipe { get; private set; }
        public ISpeechRecognizer Recognizer { get; private set; }

        public SpeechRecoClosure(TimeSpan silenceStart, TimeSpan silenceEnd, MeasuredAudioPipe pipe, ISpeechRecognizer recognizer)
        {
            SilenceStart = silenceStart;
            SilenceEnd = silenceEnd;
            Pipe = pipe;
            Recognizer = recognizer;
        }
    }
}
