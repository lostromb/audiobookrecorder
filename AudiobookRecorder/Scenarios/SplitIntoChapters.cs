using Durandal.Common.Audio;
using Durandal.Common.Logger;
using Durandal.Common.NLP.Language;
using Durandal.Common.NLP;
using Durandal.Common.Time;
using Durandal.Common.Utils.NativePlatform;
using Durandal.Extensions.Vosk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Durandal.Common.Audio.Codecs.Opus;
using Durandal.Common.Audio.Codecs;
using Durandal.Common.Audio.Components;
using Durandal.Common.Speech.SR;
using Durandal.Common.Utils;
using Durandal.Extensions.NativeAudio.Codecs;
using Durandal.Extensions.NativeAudio.Components;
using Durandal.Extensions.NativeAudio;
using Durandal.Common.Tasks;

namespace AudiobookRecorder.Scenarios
{
    public static class SplitIntoChapters
    {
        public static async Task SplitSingleAudiobookIntoChapters(string audioPath, ISpeechRecognizerFactory? speechReco)
        {
            List<FileInfo> inputFiles = new List<FileInfo>();

            if (File.Exists(audioPath))
            {
                inputFiles.Add(new FileInfo(audioPath));
            }
            else if (Directory.Exists(audioPath))
            {
                inputFiles.AddRange(new DirectoryInfo(audioPath).EnumerateFiles("*", SearchOption.TopDirectoryOnly));
            }

            AudioSampleFormat format = AudioSampleFormat.Mono(48000);
            foreach (FileInfo file in inputFiles)
            {
                await SplitSingleRecordingIntoChapters(file, speechReco, format);
            }
        }

        private static async Task SplitSingleRecordingIntoChapters(FileInfo inputFile, ISpeechRecognizerFactory? speechRecoFactory, AudioSampleFormat format)
        {
            inputFile.AssertNonNull(nameof(inputFile));
            if (inputFile.Directory == null)
            {
                throw new ArgumentNullException();
            }

            int partIdx = 1;
            int encodingKbps = 16;

            ILogger logger = new ConsoleLogger("AudiobookRecorder", LogLevel.All);
            IRealTimeProvider realTime = DefaultRealTimeProvider.Singleton;

            Queue<ChapterBreak> breaks = new Queue<ChapterBreak>(await DetermineChapterBreaks.GetBreaks(inputFile, logger, speechRecoFactory).ConfigureAwait(false));

            string sanitizedOutDirName = Utils.SanitizeDirectoryName(inputFile.Name.Substring(0, inputFile.Name.Length - inputFile.Extension.Length));

            DirectoryInfo outputDir = new DirectoryInfo(Path.Combine(inputFile.Directory.FullName, sanitizedOutDirName));
            outputDir.Create();

            IResamplerFactory resamplerFactory = new NativeSpeexResamplerFactory();
            IOpusCodecProvider opusImpl = NativeOpus.CreateOpusAdapterForCurrentPlatform(logger.Clone("OpusCodec"));
            using (IAudioGraph graph = new AudioGraph(AudioGraphCapabilities.Concurrent))
            using (FfmpegAudioSampleSource fileIn = await FfmpegAudioSampleSource.Create(graph, "Ffmpeg", logger.Clone("Ffmpeg"), inputFile))
            using (AudioConformer conformer = new AudioConformer(graph, fileIn.OutputFormat, format, "Conformer", resamplerFactory, logger.Clone("Conformer")))
            using (AudioGate gate = new AudioGate(graph, format, "Gate", lookAhead: TimeSpan.FromMilliseconds(100)))
            {
                ChapterBreak nextBreakTime;
                if (breaks.Count > 0)
                {
                    nextBreakTime = breaks.Dequeue();
                }
                else
                {
                    nextBreakTime = new ChapterBreak()
                    {
                        Start = TimeSpan.MaxValue,
                        ChapterName = string.Empty,
                    };
                }

                string outputFileName = "01 - Introduction.opus";
                if (nextBreakTime.Start < TimeSpan.FromSeconds(1))
                {
                    if (!string.IsNullOrWhiteSpace(nextBreakTime.ChapterName))
                    {
                        outputFileName = $"01 - {nextBreakTime.ChapterName}.opus";
                    }

                    if (breaks.Count > 0)
                    {
                        nextBreakTime = breaks.Dequeue();
                    }
                    else
                    {
                        nextBreakTime = new ChapterBreak()
                        {
                            Start = TimeSpan.MaxValue,
                            ChapterName = string.Empty,
                        };
                    }
                }

                TimeSpan currentTime = TimeSpan.Zero;
                FileInfo currentOutputFile = new FileInfo(Path.Combine(outputDir.FullName, Utils.SanitizeFileName(outputFileName)));
                FileStream opusOutStream = new FileStream(currentOutputFile.FullName, FileMode.Create, FileAccess.Write);
                OggOpusEncoder encoder = new OggOpusEncoder(graph, format, "OggOpusOut", opusImpl, bitrateKbps: encodingKbps, complexity: 10, oggPageLength: TimeSpan.FromSeconds(1));
                await encoder.Initialize(opusOutStream, true, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);

                fileIn.ConnectOutput(conformer);
                conformer.ConnectOutput(gate);
                gate.ConnectOutput(encoder);

                int samplesToReadPerLoop = (int)AudioMath.ConvertTimeSpanToSamplesPerChannel(format.SampleRateHz, TimeSpan.FromMilliseconds(10));

                while (!fileIn.PlaybackFinished)
                {
                    int samples = await encoder.ReadFromSource(samplesToReadPerLoop, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);
                    currentTime += AudioMath.ConvertSamplesPerChannelToTimeSpan(format.SampleRateHz, samples);

                    if (currentTime >= nextBreakTime.Start)
                    {
                        encoder.DisconnectInput();
                        await encoder.Finish(CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);
                        encoder.Dispose();

                        partIdx++;

                        outputFileName = string.IsNullOrWhiteSpace(nextBreakTime.ChapterName) ?
                            $"{partIdx:D2} - Part {partIdx}.opus" :
                            $"{partIdx:D2} - {nextBreakTime.ChapterName}.opus";
                        currentOutputFile = new FileInfo(Path.Combine(outputDir.FullName, Utils.SanitizeFileName(outputFileName)));
                        opusOutStream = new FileStream(currentOutputFile.FullName, FileMode.Create, FileAccess.Write);
                        encoder = new OggOpusEncoder(graph, format, "OggOpusOut", opusImpl, bitrateKbps: encodingKbps, complexity: 10, oggPageLength: TimeSpan.FromSeconds(1));
                        await encoder.Initialize(opusOutStream, true, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);
                        gate.ConnectOutput(encoder);

                        if (breaks.Count > 0)
                        {
                            nextBreakTime = breaks.Dequeue();
                        }
                        else
                        {
                            nextBreakTime = new ChapterBreak()
                            {
                                Start = TimeSpan.MaxValue,
                                ChapterName = string.Empty,
                            };
                        }

                    }
                }
            }
        }
    }
}
