using Durandal.Common.Audio.Codecs.Opus;
using Durandal.Common.Audio.Codecs;
using Durandal.Common.Audio.Components;
using Durandal.Common.Audio;
using Durandal.Common.Logger;
using Durandal.Common.MathExt;
using Durandal.Common.Time;
using Durandal.Common.Utils.NativePlatform;
using Durandal.Extensions.BassAudio;
using Durandal.Extensions.NativeAudio.Codecs;
using Durandal.Extensions.NativeAudio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Durandal.Common.Audio.Hardware;

namespace AudiobookRecorder.Scenarios
{
    public static class LiveRecording
    {
        public static async Task RecordAudiobookLiveWithSplitting()
        {
            ILogger logger = new ConsoleLogger("AudiobookRecorder", LogLevel.All);
            IRealTimeProvider realTime = DefaultRealTimeProvider.Singleton;
            DefaultRealTimeProvider.HighPrecisionWaitProvider = new Win32HighPrecisionWaitProvider();
            NativePlatformUtils.SetGlobalResolver(new NativeLibraryResolverImpl());

            int partIdx = 1;
            int encodingKbps = 16;
            bool speakerMonitor = true;
            TimeSpan minSilenceTime = TimeSpan.FromMilliseconds(300);
            TimeSpan minSegmentTime = TimeSpan.FromMinutes(2);
            AudioSampleFormat format = AudioSampleFormat.Mono(48000);
            TimeSpan endRecordingSilenceTime = TimeSpan.FromMilliseconds(10000);
            const float silenceTransitionThresh = 0.001f;

            DirectoryInfo outputDir = new DirectoryInfo("Recordings");
            outputDir.Create();
            MovingPercentile silenceTimeMsPercentiles = new MovingPercentile(100, 0.25, 0.5, 0.75, 0.99);
            for (int c = 0; c < silenceTimeMsPercentiles.NumSamples; c++)
            {
                silenceTimeMsPercentiles.Add(1300);
            }

            IResamplerFactory resamplerFactory = new NativeSpeexResamplerFactory();
            IOpusCodecProvider opusImpl = NativeOpus.CreateOpusAdapterForCurrentPlatform(logger.Clone("OpusCodec"));
            IAudioDriver driver = new BassDeviceDriver(logger.Clone("Bass"));
            using (IAudioGraph micGraph = new AudioGraph(AudioGraphCapabilities.Concurrent))
            using (IAudioGraph speakerGraph = new AudioGraph(AudioGraphCapabilities.Concurrent))
            using (IAudioCaptureDevice microphone = driver.OpenCaptureDevice(null, micGraph, format, "Microphone"))
            using (IAudioRenderDevice speakers = driver.OpenRenderDevice(null, speakerGraph, format, "Speakers"))
            using (AudioConformer micConformer = new AudioConformer(micGraph, microphone.OutputFormat, format, "MicConformer", resamplerFactory, logger.Clone("Conformer")))
            using (PushPullBuffer pushPull = new PushPullBuffer(micGraph, speakerGraph, format, "PushPull", TimeSpan.FromSeconds(60)))
            using (AudioSplitterAutoConforming splitter = new AudioSplitterAutoConforming(speakerGraph, format, "Splitter", resamplerFactory, logger.Clone("Splitter")))
            using (AudioGate gate = new AudioGate(speakerGraph, format, "Gate", lookAhead: TimeSpan.FromMilliseconds(100)))
            using (PassiveVolumeMeter volumeMeter = new PassiveVolumeMeter(speakerGraph, format, "VolumeMeter"))
            {
                microphone.ConnectOutput(micConformer);
                micConformer.ConnectOutput(pushPull);
                pushPull.ConnectOutput(gate);
                gate.ConnectOutput(volumeMeter);
                volumeMeter.ConnectOutput(splitter);

                if (speakerMonitor)
                {
                    splitter.AddOutput(speakers);
                    await speakers.StartPlayback(realTime).ConfigureAwait(false);
                }

                await microphone.StartCapture(realTime).ConfigureAwait(false);

                DateTimeOffset thisSegmentStartTime = realTime.Time;
                FileInfo currentOutputFile = new FileInfo(Path.Combine(outputDir.FullName, Utils.SanitizeFileName($"Part {partIdx}.opus")));
                FileStream opusOutStream = new FileStream(currentOutputFile.FullName, FileMode.Create, FileAccess.Write);
                OggOpusEncoder encoder = new OggOpusEncoder(speakerGraph, format, "OggOpusOut", opusImpl, bitrateKbps: encodingKbps, complexity: 10);
                await encoder.Initialize(opusOutStream, true, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);
                splitter.AddOutput(encoder);

                bool isSilence = false;
                bool anySignalThisSegment = false;
                DateTimeOffset silenceStartTime = realTime.Time;
                int samplesToReadPerLoop = (int)AudioMath.ConvertTimeSpanToSamplesPerChannel(format.SampleRateHz, TimeSpan.FromMilliseconds(20));

                Console.WriteLine("RECORDING STATUS:");
                StringBuilder s = new StringBuilder();
                while (true)
                {
                    if (!speakerMonitor)
                    {
                        // drive input if the speakers aren't doing it
                        await encoder.ReadFromSource(samplesToReadPerLoop, CancellationToken.None, realTime).ConfigureAwait(false);
                    }
                    else
                    {
                        await realTime.WaitAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None).ConfigureAwait(false);
                    }

                    float avgVolume = volumeMeter.GetLoudestRmsVolume();
                    float peakVolume = volumeMeter.GetPeakVolume();
                    TimeSpan thisSegmentRecordingTime = Utils.TruncateToMilliseconds(realTime.Time - thisSegmentStartTime);
                    if (!isSilence && peakVolume < silenceTransitionThresh)
                    {
                        isSilence = true;
                        silenceStartTime = realTime.Time;
                    }
                    else if (isSilence && peakVolume > silenceTransitionThresh)
                    {
                        isSilence = false;
                        anySignalThisSegment = true;
                        TimeSpan silenceTime = realTime.Time - silenceStartTime;
                        if (silenceTime > minSilenceTime)
                        {
                            silenceTimeMsPercentiles.Add(silenceTime.TotalMilliseconds);
                        }
                    }

                    double silenceThresholdMs = silenceTimeMsPercentiles.GetPercentile(0.75) * 2.5;
                    TimeSpan requiredSilenceToRotateSegments = Utils.TruncateToMilliseconds(TimeSpan.FromMilliseconds(silenceThresholdMs));
                    TimeSpan totalSilenceTime = isSilence ? Utils.TruncateToMilliseconds(realTime.Time - silenceStartTime) : TimeSpan.Zero;

                    int vuMeterWidth = 40;
                    int thickMeterBars = Math.Max(0, (int)(avgVolume * vuMeterWidth));
                    int thinMeterBars = Math.Max(thickMeterBars, (int)(peakVolume * vuMeterWidth));
                    s.Clear();
                    s.Append('\r');
                    if (isSilence)
                    {
                        s.Append(" -- Silent ");
                        TimeSpanExtensions.PrintTimeSpan(totalSilenceTime, s);
                        s.Append(" --");
                        if (vuMeterWidth + 1 - s.Length > 0)
                        {
                            s.Append(' ', vuMeterWidth + 1 - s.Length);
                        }
                    }
                    else
                    {
                        s.Append('▓', thickMeterBars);
                        if (thinMeterBars > thickMeterBars)
                        {
                            s.Append('░', thinMeterBars - thickMeterBars);
                        }
                        if (vuMeterWidth - thinMeterBars > 0)
                        {
                            s.Append(' ', vuMeterWidth - thinMeterBars);
                        }
                    }

                    s.Append("| ");
                    s.Append("Part ");
                    s.Append(partIdx);
                    s.Append(".opus ");
                    TimeSpanExtensions.PrintTimeSpan(thisSegmentRecordingTime, s);
                    s.Append(" Part Threshold ");
                    TimeSpanExtensions.PrintTimeSpan(requiredSilenceToRotateSegments, s);
                    Console.Write(s.ToString());

                    // End recording after a long silence
                    if (totalSilenceTime > endRecordingSilenceTime)
                    {
                        encoder.DisconnectInput();
                        await encoder.Finish(CancellationToken.None, realTime).ConfigureAwait(false);
                        encoder.Dispose();
                        await microphone.StopCapture().ConfigureAwait(false);
                        if (speakerMonitor)
                        {
                            await speakers.StopPlayback().ConfigureAwait(false);
                        }

                        Console.WriteLine();
                        Console.WriteLine("Recording finished!");
                        return;
                    }
                    // Rotate file if silence is long enough and the current segment is also long enough
                    else if (anySignalThisSegment &&
                        totalSilenceTime > requiredSilenceToRotateSegments &&
                        thisSegmentRecordingTime > minSegmentTime)
                    {
                        encoder.DisconnectInput();
                        await encoder.Finish(CancellationToken.None, realTime).ConfigureAwait(false);
                        encoder.Dispose();

                        partIdx++;
                        currentOutputFile = new FileInfo(Path.Combine(outputDir.FullName, Utils.SanitizeFileName($"Part {partIdx}.opus")));
                        opusOutStream = new FileStream(currentOutputFile.FullName, FileMode.Create, FileAccess.Write);
                        encoder = new OggOpusEncoder(speakerGraph, format, "OggOpusOut", opusImpl, bitrateKbps: encodingKbps, complexity: 10);
                        await encoder.Initialize(opusOutStream, true, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);

                        // Prime encoding with 1 second of silence
                        using (SilenceAudioSampleSource silence = new SilenceAudioSampleSource(speakerGraph, format, "Silence"))
                        {
                            silence.ConnectOutput(encoder);
                            await encoder.ReadFromSource(format.SampleRateHz, CancellationToken.None, realTime).ConfigureAwait(false);
                            encoder.DisconnectInput();
                        }

                        splitter.AddOutput(encoder);
                        thisSegmentStartTime = realTime.Time;
                        anySignalThisSegment = false;
                        Console.WriteLine();
                    }
                }
            }
        }

        public static async Task RecordAudiobookLiveSingleSegment(TimeSpan endRecordingSilenceTime)
        {
            ILogger logger = new ConsoleLogger("AudiobookRecorder", LogLevel.All);
            IRealTimeProvider realTime = DefaultRealTimeProvider.Singleton;
            DefaultRealTimeProvider.HighPrecisionWaitProvider = new Win32HighPrecisionWaitProvider();
            NativePlatformUtils.SetGlobalResolver(new NativeLibraryResolverImpl());

            int encodingKbps = 128;
            bool speakerMonitor = true;
            AudioSampleFormat format = AudioSampleFormat.Mono(48000);
            const float silenceTransitionThresh = 0.001f;

            DirectoryInfo outputDir = new DirectoryInfo("Recordings");
            outputDir.Create();

            IResamplerFactory resamplerFactory = new NativeSpeexResamplerFactory();
            IOpusCodecProvider opusImpl = NativeOpus.CreateOpusAdapterForCurrentPlatform(logger.Clone("OpusCodec"));
            IAudioDriver driver = new BassDeviceDriver(logger.Clone("Bass"));
            using (IAudioGraph micGraph = new AudioGraph(AudioGraphCapabilities.Concurrent))
            using (IAudioGraph speakerGraph = new AudioGraph(AudioGraphCapabilities.Concurrent))
            using (IAudioCaptureDevice microphone = driver.OpenCaptureDevice(null, micGraph, format, "Microphone"))
            using (IAudioRenderDevice speakers = driver.OpenRenderDevice(null, speakerGraph, format, "Speakers"))
            using (AudioConformer micConformer = new AudioConformer(micGraph, microphone.OutputFormat, format, "MicConformer", resamplerFactory, logger.Clone("Conformer")))
            using (PushPullBuffer pushPull = new PushPullBuffer(micGraph, speakerGraph, format, "PushPull", TimeSpan.FromSeconds(60)))
            using (SilencePaddingFilter silencePad = new SilencePaddingFilter(speakerGraph, format, "SpeakerPadding"))
            using (AudioGate gate = new AudioGate(micGraph, format, "Gate", lookAhead: TimeSpan.FromMilliseconds(100)))
            using (AudioSplitterAutoConforming splitter = new AudioSplitterAutoConforming(micGraph, format, "Splitter", resamplerFactory, logger.Clone("Splitter")))
            using (PassiveVolumeMeter volumeMeter = new PassiveVolumeMeter(micGraph, format, "VolumeMeter"))
            {
                microphone.ConnectOutput(micConformer);
                micConformer.ConnectOutput(gate);
                gate.ConnectOutput(volumeMeter);
                volumeMeter.ConnectOutput(splitter);

                await microphone.StartCapture(realTime).ConfigureAwait(false);

                if (speakerMonitor)
                {
                    splitter.AddOutput(pushPull);
                    pushPull.ConnectOutput(silencePad);
                    silencePad.ConnectOutput(speakers);
                    await speakers.StartPlayback(realTime).ConfigureAwait(false);
                }

                pushPull.ClearBuffer();

                DateTimeOffset thisSegmentStartTime = realTime.Time;
                string startTimeString = realTime.Time.ToString("yyyy-MM-dd HH_mm_ss");
                FileInfo outputFile = new FileInfo(Path.Combine(outputDir.FullName, Utils.SanitizeFileName($"{startTimeString}.opus")));
                FileStream opusOutStream = new FileStream(outputFile.FullName, FileMode.CreateNew, FileAccess.Write);
                OggOpusEncoder encoder = new OggOpusEncoder(micGraph, format, "OggOpusOut", opusImpl, bitrateKbps: encodingKbps, complexity: 10);
                await encoder.Initialize(opusOutStream, true, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);
                splitter.AddOutput(encoder);

                bool isSilence = false;
                DateTimeOffset recordingStartTime = realTime.Time;
                TimeSpan silenceStartTime = TimeSpan.Zero;
                TimeSpan elapsedTime = TimeSpan.Zero;

                Console.WriteLine("RECORDING STATUS:");
                StringBuilder s = new StringBuilder();
                while (true)
                {
                    await realTime.WaitAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None).ConfigureAwait(false);
                    elapsedTime = realTime.Time - recordingStartTime;

                    float avgVolume = volumeMeter.GetLoudestRmsVolume();
                    float peakVolume = volumeMeter.GetPeakVolume();
                    if (!isSilence && peakVolume < silenceTransitionThresh)
                    {
                        isSilence = true;
                        silenceStartTime = elapsedTime;
                    }
                    else if (isSilence && peakVolume > silenceTransitionThresh)
                    {
                        isSilence = false;
                    }

                    TimeSpan totalSilenceTime = isSilence ? Utils.TruncateToMilliseconds(elapsedTime - silenceStartTime) : TimeSpan.Zero;

                    int vuMeterWidth = 40;
                    int thickMeterBars = Math.Max(0, (int)(avgVolume * vuMeterWidth));
                    int thinMeterBars = Math.Max(thickMeterBars, (int)(peakVolume * vuMeterWidth));
                    s.Clear();
                    s.Append('\r');
                    if (isSilence)
                    {
                        s.Append(" -- Silent ");
                        TimeSpanExtensions.PrintTimeSpan(totalSilenceTime, s);
                        s.Append(" --");
                        if (vuMeterWidth + 1 - s.Length > 0)
                        {
                            s.Append(' ', vuMeterWidth + 1 - s.Length);
                        }
                    }
                    else
                    {
                        s.Append('▓', thickMeterBars);
                        if (thinMeterBars > thickMeterBars)
                        {
                            s.Append('░', thinMeterBars - thickMeterBars);
                        }
                        if (vuMeterWidth - thinMeterBars > 0)
                        {
                            s.Append(' ', vuMeterWidth - thinMeterBars);
                        }
                    }

                    s.Append("| ");
                    s.Append(outputFile.Name);
                    s.Append(" ");
                    TimeSpanExtensions.PrintTimeSpan(elapsedTime, s);
                    s.Append("    ");
                    Console.Write(s.ToString());

                    // End recording after a long silence
                    if (totalSilenceTime > endRecordingSilenceTime)
                    {
                        encoder.DisconnectInput();
                        await encoder.Finish(CancellationToken.None, realTime).ConfigureAwait(false);
                        encoder.Dispose();
                        await microphone.StopCapture().ConfigureAwait(false);
                        if (speakerMonitor)
                        {
                            await speakers.StopPlayback().ConfigureAwait(false);
                        }

                        Console.WriteLine();
                        Console.WriteLine("Recording finished!");
                        return;
                    }
                }
            }
        }

        //public static async Task Karaoke()
        //{
        //    ILogger logger = new ConsoleLogger("AudiobookRecorder", LogLevel.All);
        //    IRealTimeProvider realTime = DefaultRealTimeProvider.Singleton;
        //    DefaultRealTimeProvider.HighPrecisionWaitProvider = new Win32HighPrecisionWaitProvider();
        //    NativePlatformUtils.SetGlobalResolver(new NativeLibraryResolverImpl());

        //    AudioSampleFormat format = AudioSampleFormat.Mono(48000);
        //    const float silenceTransitionThresh = 0.001f;

        //    DirectoryInfo outputDir = new DirectoryInfo("Recordings");
        //    outputDir.Create();

        //    IResamplerFactory resamplerFactory = new NativeSpeexResamplerFactory();
        //    IOpusCodecProvider opusImpl = NativeOpus.CreateOpusAdapterForCurrentPlatform(logger.Clone("OpusCodec"));
        //    IAudioDriver driver = new BassDeviceDriver(logger.Clone("Bass"));
        //    using (IAudioGraph micGraph = new AudioGraph(AudioGraphCapabilities.Concurrent))
        //    using (IAudioGraph speakerGraph = new AudioGraph(AudioGraphCapabilities.Concurrent))
        //    using (IAudioCaptureDevice microphone = driver.OpenCaptureDevice(null, micGraph, format, "Microphone"))
        //    using (IAudioRenderDevice speakers = driver.OpenRenderDevice(null, speakerGraph, format, "Speakers"))
        //    using (AudioConformer micConformer = new AudioConformer(micGraph, microphone.OutputFormat, format, "MicConformer", resamplerFactory, logger.Clone("Conformer")))
        //    using (PushPullBuffer pushPull = new PushPullBuffer(micGraph, speakerGraph, format, "PushPull", TimeSpan.FromSeconds(60)))
        //    using (FeedbackCircuitBreaker circuitBreaker = new FeedbackCircuitBreaker(speakerGraph, format, "CircuitBreaker"))
        //    {
        //        microphone.ConnectOutput(micConformer);
        //        micConformer.ConnectOutput(pushPull);
        //        pushPull.ConnectOutput(circuitBreaker.SpeakerFilterInput);
        //        circuitBreaker.SpeakerFilterOutputConnectOutput(speakers);
        //        await speakers.StartPlayback(realTime).ConfigureAwait(false);

        //        await microphone.StartCapture(realTime).ConfigureAwait(false);

        //        pushPull.ClearBuffer();

        //        DateTimeOffset thisSegmentStartTime = realTime.Time;
        //        string startTimeString = realTime.Time.ToString("yyyy-MM-dd HH_mm_ss");
        //        FileInfo outputFile = new FileInfo(Path.Combine(outputDir.FullName, Utils.SanitizeFileName($"{startTimeString}.opus")));
        //        FileStream opusOutStream = new FileStream(outputFile.FullName, FileMode.CreateNew, FileAccess.Write);
        //        OggOpusEncoder encoder = new OggOpusEncoder(speakerGraph, format, "OggOpusOut", opusImpl, bitrateKbps: encodingKbps, complexity: 10);
        //        await encoder.Initialize(opusOutStream, true, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);
        //        splitter.AddOutput(encoder);

        //        bool isSilence = false;
        //        DateTimeOffset recordingStartTime = realTime.Time;
        //        TimeSpan silenceStartTime = TimeSpan.Zero;
        //        TimeSpan elapsedTime = TimeSpan.Zero;

        //        Console.WriteLine("RECORDING STATUS:");
        //        StringBuilder s = new StringBuilder();
        //        while (true)
        //        {
        //            if (!speakerMonitor)
        //            {
        //                // drive input if the speakers aren't doing it
        //                int samplesToRead = Math.Min(
        //                    (int)AudioMath.ConvertTimeSpanToSamplesPerChannel(format.SampleRateHz, TimeSpan.FromMilliseconds(20)),
        //                    pushPull.AvailableSamples);
        //                int samplesActuallyRead = await encoder.ReadFromSource(samplesToRead, CancellationToken.None, realTime).ConfigureAwait(false);
        //                elapsedTime += AudioMath.ConvertSamplesPerChannelToTimeSpan(format.SampleRateHz, samplesActuallyRead);
        //            }
        //            else
        //            {
        //                await realTime.WaitAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None).ConfigureAwait(false);
        //                elapsedTime = realTime.Time - recordingStartTime;
        //            }

        //            float avgVolume = volumeMeter.GetLoudestRmsVolume();
        //            float peakVolume = volumeMeter.GetPeakVolume();
        //            if (!isSilence && peakVolume < silenceTransitionThresh)
        //            {
        //                isSilence = true;
        //                silenceStartTime = elapsedTime;
        //            }
        //            else if (isSilence && peakVolume > silenceTransitionThresh)
        //            {
        //                isSilence = false;
        //            }

        //            TimeSpan totalSilenceTime = isSilence ? Utils.TruncateToMilliseconds(elapsedTime - silenceStartTime) : TimeSpan.Zero;

        //            int vuMeterWidth = 40;
        //            int thickMeterBars = Math.Max(0, (int)(avgVolume * vuMeterWidth));
        //            int thinMeterBars = Math.Max(thickMeterBars, (int)(peakVolume * vuMeterWidth));
        //            s.Clear();
        //            s.Append('\r');
        //            if (isSilence)
        //            {
        //                s.Append(" -- Silent ");
        //                TimeSpanExtensions.PrintTimeSpan(totalSilenceTime, s);
        //                s.Append(" --");
        //                if (vuMeterWidth + 1 - s.Length > 0)
        //                {
        //                    s.Append(' ', vuMeterWidth + 1 - s.Length);
        //                }
        //            }
        //            else
        //            {
        //                s.Append('▓', thickMeterBars);
        //                if (thinMeterBars > thickMeterBars)
        //                {
        //                    s.Append('░', thinMeterBars - thickMeterBars);
        //                }
        //                if (vuMeterWidth - thinMeterBars > 0)
        //                {
        //                    s.Append(' ', vuMeterWidth - thinMeterBars);
        //                }
        //            }

        //            s.Append("| ");
        //            s.Append(outputFile.Name);
        //            s.Append(" ");
        //            TimeSpanExtensions.PrintTimeSpan(elapsedTime, s);
        //            Console.Write(s.ToString());

        //            // End recording after a long silence
        //            if (totalSilenceTime > endRecordingSilenceTime)
        //            {
        //                encoder.DisconnectInput();
        //                await encoder.Finish(CancellationToken.None, realTime).ConfigureAwait(false);
        //                encoder.Dispose();
        //                await microphone.StopCapture().ConfigureAwait(false);
        //                if (speakerMonitor)
        //                {
        //                    await speakers.StopPlayback().ConfigureAwait(false);
        //                }

        //                Console.WriteLine();
        //                Console.WriteLine("Recording finished!");
        //                return;
        //            }
        //        }
        //    }
        //}
    }
}
