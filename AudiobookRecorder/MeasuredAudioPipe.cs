using Durandal.Common.Audio;
using Durandal.Common.Tasks;
using Durandal.Common.Time;
using Durandal.Common.Utils;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AudiobookRecorder
{
    /// <summary>
    /// Audio graph filter which passes only a limited amount of data through
    /// </summary>
    public class MeasuredAudioPipe : AbstractAudioSampleFilter
    {
        private readonly long _samplesPerChannelToAllow;
        private long _samplesPerChannelPassedThrough = 0;

        public MeasuredAudioPipe(IAudioGraph graph, AudioSampleFormat format, string nodeCustomName, TimeSpan amountToAllowThrough)
            : base(graph, nameof(MeasuredAudioPipe), nodeCustomName)
        {
            InputFormat = format.AssertNonNull(nameof(format));
            OutputFormat = format;
            _samplesPerChannelToAllow = AudioMath.ConvertTimeSpanToSamplesPerChannel(format.SampleRateHz, amountToAllowThrough);
        }

        public bool ReachedEnd => _samplesPerChannelPassedThrough == _samplesPerChannelToAllow;

        protected override async ValueTask<int> ReadAsyncInternal(float[] buffer, int offset, int count, CancellationToken cancelToken, IRealTimeProvider realTime)
        {
            if (ReachedEnd)
            {
                return -1;
            }

            int maxAmountCanRead = (int)Math.Min((long)count, _samplesPerChannelToAllow - _samplesPerChannelPassedThrough);
            int readReturnVal = await Input.ReadAsync(buffer, offset, maxAmountCanRead, cancelToken, realTime).ConfigureAwait(false);
            if (readReturnVal > 0)
            {
                _samplesPerChannelPassedThrough += readReturnVal;
            }

            return readReturnVal;
        }

        protected override async ValueTask WriteAsyncInternal(float[] buffer, int offset, int count, CancellationToken cancelToken, IRealTimeProvider realTime)
        {
            int maxAmountCanWrite = (int)Math.Min((long)count, _samplesPerChannelToAllow - _samplesPerChannelPassedThrough);
            if (maxAmountCanWrite > 0)
            {
                _samplesPerChannelPassedThrough += maxAmountCanWrite;
                await Output.WriteAsync(buffer, offset, count, cancelToken, realTime).ConfigureAwait(false);
            }
        }
    }
}
