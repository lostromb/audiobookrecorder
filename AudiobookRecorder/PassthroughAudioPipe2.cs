using Durandal.Common.IO;
using Durandal.Common.MathExt;
using Durandal.Common.ServiceMgmt;
using Durandal.Common.Tasks;
using Durandal.Common.Time;
using Durandal.Common.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Durandal.Common.Audio.Components
{
    /// <summary>
    /// Audio graph filter which does nothing, just passes through data.
    /// This also can double as a "motivator" where you can figuratively open
    /// a valve and let a specific number of samples to pass from input to output
    /// in graphs which otherwise don't have an active node like a microphone.
    /// </summary>
    public sealed class PassthroughAudioPipe2 : IAudioSampleSource, IAudioSampleTarget
    {
        private readonly IAudioGraph _graph;
        private readonly string _nodeName;
        private readonly string _nodeFullName;
        private bool _playbackFinished = false;
        private HashSet<IDisposable>? _extraDisposables;
        private int _disposed = 0;

        public PassthroughAudioPipe2(IAudioGraph graph, AudioSampleFormat format, string nodeCustomName)
        {
            _graph = graph.AssertNonNull(nameof(graph));
            InputFormat = format.AssertNonNull(nameof(format));
            OutputFormat = format;
            AudioHelpers.BuildAudioNodeNames(nameof(PassthroughAudioPipe2), nodeCustomName, out _nodeName, out _nodeFullName);
            DebugMemoryLeakTracer.TraceDisposableItemCreated(this);
        }

#if TRACK_IDISPOSABLE_LEAKS
        ~PassthroughAudioPipe2()
        {
            Dispose(false);
        }
#endif

        /// <inheritdoc/>
        public IAudioGraph InputGraph => _graph;

        /// <inheritdoc/>
        public IAudioGraph OutputGraph => _graph;

        /// <inheritdoc/>
        public IAudioSampleTarget? Output { get; private set; }

        /// <inheritdoc/>
        public AudioSampleFormat OutputFormat { get; private set; }

        /// <inheritdoc/>
        public IAudioSampleSource? Input { get; private set; }

        /// <inheritdoc/>
        public AudioSampleFormat InputFormat { get; private set; }

        /// <inheritdoc/>
        public bool PlaybackFinished => _playbackFinished;

        /// <inheritdoc/>
        public bool IsActiveNode => false;

        /// <inheritdoc/>
        public string NodeName => _nodeName;

        /// <inheritdoc/>
        public string NodeFullName => _nodeFullName;

        /// <inheritdoc/>
        public void ConnectOutput(IAudioSampleTarget target, bool noRecursiveConnection = false)
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(nameof(AbstractAudioSampleFilter));
            }

            target.AssertNonNull(nameof(target));
            AudioSampleFormat.AssertFormatsAreEqual(target.InputFormat, OutputFormat);

            if (!this.OutputGraph.Equals(target.InputGraph))
            {
                throw new ArgumentException("Cannot connect audio components that are part of different graphs");
            }

            if (noRecursiveConnection)
            {
                if (Output != null)
                {
                    Output.DisconnectInput(true);
                }

                Output = target;
            }
            else
            {
                _graph.LockGraph();
                try
                {
                    if (Output != target)
                    {
                        if (Output != null)
                        {
                            Output.DisconnectInput(true);
                        }

                        target.ConnectInput(this, true);
                        Output = target;
                    }

                }
                finally
                {
                    _graph.UnlockGraph();
                }
            }
        }

        /// <inheritdoc/>
        public void DisconnectOutput(bool noRecursiveConnection = false)
        {
            if (noRecursiveConnection)
            {
                Output = null;
            }
            else
            {
                _graph.LockGraph();
                try
                {
                    if (Output != null)
                    {
                        Output.DisconnectInput(true);
                        Output = null;
                    }
                }
                finally
                {
                    _graph.UnlockGraph();
                }
            }
        }

        /// <inheritdoc/>
        public void ConnectInput(IAudioSampleSource source, bool noRecursiveConnection = false)
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(nameof(AbstractAudioSampleFilter));
            }

            if (PlaybackFinished)
            {
                throw new InvalidOperationException("Can't connect an audio component to something else after its playback has finished");
            }

            source.AssertNonNull(nameof(source));
            AudioSampleFormat.AssertFormatsAreEqual(source.OutputFormat, InputFormat);

            if (!this.InputGraph.Equals(source.OutputGraph))
            {
                throw new ArgumentException("Cannot connect audio components that are part of different graphs");
            }

            if (noRecursiveConnection)
            {
                if (Input != null)
                {
                    Input.DisconnectOutput(true);
                }

                Input = source;
            }
            else
            {
                _graph.LockGraph();
                try
                {
                    if (Input != source)
                    {
                        if (Input != null)
                        {
                            Input.DisconnectOutput(true);
                        }

                        source.ConnectOutput(this, true);
                        Input = source;
                    }
                }
                finally
                {
                    _graph.UnlockGraph();
                }
            }
        }

        /// <inheritdoc/>
        public void DisconnectInput(bool noRecursiveConnection = false)
        {
            if (noRecursiveConnection)
            {
                Input = null;
            }
            else
            {
                _graph.LockGraph();
                try
                {
                    if (Input != null)
                    {
                        Input.DisconnectOutput(true);
                        Input = null;
                    }
                }
                finally
                {
                    _graph.UnlockGraph();
                }
            }
        }

        /// <inheritdoc/>
        public void TakeOwnershipOfDisposable(IDisposable obj)
        {
            if (_extraDisposables == null)
            {
                _extraDisposables = new HashSet<IDisposable>();
            }

            if (!_extraDisposables.Contains(obj))
            {
                _extraDisposables.Add(obj);
            }
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return _nodeFullName;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!AtomicOperations.ExecuteOnce(ref _disposed))
            {
                return;
            }

            DebugMemoryLeakTracer.TraceDisposableItemDisposed(this, disposing);

            if (disposing)
            {
                if (_extraDisposables != null)
                {
                    foreach (IDisposable b in _extraDisposables)
                    {
                        b?.Dispose();
                    }
                }
            }
        }

        /// <inheritdoc/>
        public async ValueTask FlushAsync(CancellationToken cancelToken, IRealTimeProvider realTime)
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(nameof(AbstractAudioSampleFilter));
            }

            if (IsActiveNode)
            {
                throw new InvalidOperationException("Cannot flush an active graph node. Generally there should only be one active node per graph. If more than one is required, you should consider putting a push-pull buffer between them.");
            }

            //await FlushAsyncInternal(cancelToken, realTime).ConfigureAwait(false);
            if (Output != null)
            {
                await Output.FlushAsync(cancelToken, realTime).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public async ValueTask<int> ReadAsync(float[] buffer, int offset, int count, CancellationToken cancelToken, IRealTimeProvider realTime)
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(nameof(AbstractAudioSampleFilter));
            }

            if (PlaybackFinished)
            {
                return -1;
            }

            if (IsActiveNode)
            {
                throw new InvalidOperationException("Cannot read audio samples from an active graph node. Generally there should only be one active node per graph. If more than one is required, you should consider putting a push-pull buffer between them.");
            }

            _graph.BeginComponentInclusiveScope(realTime, _nodeFullName);
            try
            {
                if (Input == null)
                {
                    return 0;
                }
                else
                {
                    int returnVal = await Input.ReadAsync(buffer, offset, count, cancelToken, realTime).ConfigureAwait(false);
                    if (returnVal < 0)
                    {
                        _playbackFinished = true;
                    }

                    return returnVal;
                }
            }
            finally
            {
                _graph.EndComponentInclusiveScope(realTime);
            }
        }

        /// <inheritdoc/>
        public async ValueTask WriteAsync(float[] buffer, int offset, int count, CancellationToken cancelToken, IRealTimeProvider realTime)
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(nameof(AbstractAudioSampleFilter));
            }

            if (IsActiveNode)
            {
                throw new InvalidOperationException("Cannot write audio samples to an active graph node. Generally there should only be one active node per graph. If more than one is required, you should consider putting a push-pull buffer between them.");
            }

            _graph.BeginComponentInclusiveScope(realTime, _nodeFullName);
            try
            {
                if (Output != null)
                {
                    await Output.WriteAsync(buffer, offset, count, cancelToken, realTime).ConfigureAwait(false);

                    if (Input != null)
                    {
                        _playbackFinished = _playbackFinished || Input.PlaybackFinished;
                    }
                }
            }
            finally
            {
                _graph.EndComponentInclusiveScope(realTime);
            }
        }

        /// <summary>
        /// Drives the audio graph which owns this component, reading from
        /// this component's input and writing it to output until the
        /// specified number of audio samples have been written
        /// </summary>
        /// <param name="amountToDrive">The length of audio to drive measured in time units</param>
        /// <param name="cancelToken">A cancel token for the operation.</param>
        /// <param name="realTime">A definition of real time.</param>
        /// <returns>The total number of samples per channel written (may be less than requested if the input ended prematurely)</returns>
        public Task<long> DriveGraph(TimeSpan amountToDrive, CancellationToken cancelToken, IRealTimeProvider realTime)
        {
            return DriveGraph(AudioMath.ConvertTimeSpanToSamplesPerChannel(InputFormat.SampleRateHz, amountToDrive), cancelToken, realTime);
        }

        /// <summary>
        /// Drives the audio graph which owns this component, reading from
        /// this component's input and writing it to output until the
        /// specified number of audio samples have been written
        /// </summary>
        /// <param name="samplesPerChannelToDrive">The number of samples per channel of audio to drive</param>
        /// <param name="cancelToken">A cancel token for the operation.</param>
        /// <param name="realTime">A definition of real time.</param>
        /// <returns>The total number of samples per channel written (may be less than requested if the input ended prematurely)</returns>
        public async Task<long> DriveGraph(long samplesPerChannelToDrive, CancellationToken cancelToken, IRealTimeProvider realTime)
        {
            const int bufferSizeSamplesPerChannel = 65536;
            realTime.AssertNonNull(nameof(realTime));
            long totalSamplesToWrite = samplesPerChannelToDrive.AssertNonNegative(nameof(samplesPerChannelToDrive));
            long totalSamplesWritten = 0;

            if (samplesPerChannelToDrive == 0)
            {
                return 0;
            }

            using (PooledBuffer<float> pooledBuf = BufferPool<float>.Rent(bufferSizeSamplesPerChannel * OutputFormat.NumChannels))
            {
                while (!PlaybackFinished && totalSamplesWritten < totalSamplesToWrite)
                {
                    await _graph.LockGraphAsync(cancelToken, realTime).ConfigureAwait(false);
                    _graph.BeginInstrumentedScope(realTime, NodeFullName);
                    try
                    {
                        // have to check on each loop because someone may have messed with the graph while it was unlocked
                        if (Input == null)
                        {
                            throw new NullReferenceException("Attempted to drive audio input while input was null");
                        }
                        if (Output == null)
                        {
                            throw new NullReferenceException("Attempted to drive audio output while output was null");
                        }

                        int samplesPerChannelRead = await Input.ReadAsync(
                            pooledBuf.Buffer,
                            0,
                            (int)Math.Min(totalSamplesToWrite - totalSamplesWritten, (long)bufferSizeSamplesPerChannel),
                            cancelToken,
                            realTime).ConfigureAwait(false);
                        if (samplesPerChannelRead > 0)
                        {
                            await Output.WriteAsync(pooledBuf.Buffer, 0, samplesPerChannelRead, cancelToken, realTime).ConfigureAwait(false);
                            totalSamplesWritten += samplesPerChannelRead;
                        }
                        else if (samplesPerChannelRead < 0)
                        {
                            _playbackFinished = true;
                        }
                    }
                    finally
                    {
                        _graph.EndInstrumentedScope(realTime, AudioMath.ConvertSamplesPerChannelToTimeSpan(OutputFormat.SampleRateHz, bufferSizeSamplesPerChannel));
                        _graph.UnlockGraph();
                    }
                }

                if (Output != null)
                {
                    await Output.FlushAsync(cancelToken, realTime).ConfigureAwait(false);
                }
            }

            return totalSamplesWritten;
        }

        /// <summary>
        /// Drives the audio graph which owns this component, reading from
        /// this component's input and writing it to output until the
        /// input reports end of stream.
        /// </summary>
        /// <param name="cancelToken">A cancel token for the operation.</param>
        /// <param name="realTime">A definition of real time.</param>
        /// <param name="limitToRealTime">Whether to insert delays to restrict the operation to real time or not.</param>
        /// <returns>An async task</returns>
        public async Task DriveGraphFully(CancellationToken cancelToken, IRealTimeProvider realTime, bool limitToRealTime = false)
        {
            const int bufferSizeSamplesPerChannel = 65536;
            realTime.AssertNonNull(nameof(realTime));
            // 15 milliseconds is a generous estimate assuming low-precision Windows wait providers
            long minimumTicksToTriggerWait = TimeSpan.FromMilliseconds(15).Ticks;
            long operationStartedTicks = realTime.TimestampTicks;
            long totalSamplesWritten = 0;
            using (PooledBuffer<float> pooledBuf = BufferPool<float>.Rent(bufferSizeSamplesPerChannel * OutputFormat.NumChannels))
            {
                while (!PlaybackFinished)
                {
                    await _graph.LockGraphAsync(cancelToken, realTime).ConfigureAwait(false);
                    _graph.BeginInstrumentedScope(realTime, NodeFullName);
                    try
                    {
                        // have to check on each loop because someone may have messed with the graph while it was unlocked
                        if (Input == null)
                        {
                            throw new NullReferenceException("Attempted to drive audio input while input was null");
                        }
                        if (Output == null)
                        {
                            throw new NullReferenceException("Attempted to drive audio output while output was null");
                        }

                        int samplesPerChannelRead = await Input.ReadAsync(pooledBuf.Buffer, 0, bufferSizeSamplesPerChannel, cancelToken, realTime).ConfigureAwait(false);
                        if (samplesPerChannelRead > 0)
                        {
                            await Output.WriteAsync(pooledBuf.Buffer, 0, samplesPerChannelRead, cancelToken, realTime).ConfigureAwait(false);
                            totalSamplesWritten += samplesPerChannelRead;
                        }
                        else if (samplesPerChannelRead < 0)
                        {
                            _playbackFinished = true;
                        }
                    }
                    finally
                    {
                        _graph.EndInstrumentedScope(realTime, AudioMath.ConvertSamplesPerChannelToTimeSpan(OutputFormat.SampleRateHz, bufferSizeSamplesPerChannel));
                        _graph.UnlockGraph();
                    }

                    if (limitToRealTime && !PlaybackFinished)
                    {
                        // This paranoid-looking logic is here to account for the fact that the time spent in waiting
                        // is likely not the actual amount that we requested. To avoid buffer underruns down the line,
                        // make sure we adjust our budget by the actual amount of time that has passed,
                        // and carry any potential deficit forwards.
                        long totalTicksElapsedRealTime = realTime.TimestampTicks - operationStartedTicks;
                        long ticksOfAudioWritten = AudioMath.ConvertSamplesPerChannelToTicks(InputFormat.SampleRateHz, totalSamplesWritten);
                        long ticksWeCanWait = ticksOfAudioWritten - totalTicksElapsedRealTime;
                        if (ticksWeCanWait > minimumTicksToTriggerWait)
                        {
                            await realTime.WaitAsync(TimeSpan.FromTicks(ticksWeCanWait), cancelToken).ConfigureAwait(false);
                        }
                    }
                }

                if (Output != null)
                {
                    await Output.FlushAsync(cancelToken, realTime).ConfigureAwait(false);
                }
            }
        }
    }
}
