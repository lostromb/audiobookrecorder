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
    public static class TranscribeAudioFiles
    {
        public static async Task SplitSingleAudiobookIntoChapters(string audioPath, ISpeechRecognizerFactory speechReco)
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
                await TranscribeSingleRecording(file, speechReco, format);
            }
        }

        private static async Task TranscribeSingleRecording(FileInfo inputFile, ISpeechRecognizerFactory speechRecoFactory, AudioSampleFormat format)
        {
            inputFile.AssertNonNull(nameof(inputFile));
            if (inputFile.Directory == null)
            {
                throw new ArgumentNullException();
            }

            ILogger logger = new ConsoleLogger("AudiobookTranscriber", LogLevel.All);
            IRealTimeProvider realTime = DefaultRealTimeProvider.Singleton;

            FileInfo currentOutputFile = new FileInfo(Path.Combine(inputFile.Directory.FullName, inputFile.Name + ".txt"));

            IResamplerFactory resamplerFactory = new NativeSpeexResamplerFactory();
            IOpusCodecProvider opusImpl = NativeOpus.CreateOpusAdapterForCurrentPlatform(logger.Clone("OpusCodec"));
            using (FileStream textOutStream = new FileStream(currentOutputFile.FullName, FileMode.Create, FileAccess.Write))
            using (IAudioGraph graph = new AudioGraph(AudioGraphCapabilities.Concurrent))
            using (FfmpegAudioSampleSource fileIn = await FfmpegAudioSampleSource.Create(graph, "Ffmpeg", logger.Clone("Ffmpeg"), inputFile))
            using (ISpeechRecognizer speechReco = await speechRecoFactory.CreateRecognitionStream(
                graph,
                "SpeechReco",
                LanguageCode.EN_US,
                logger.Clone("SpeechReco"),
                CancellationToken.None,
                DefaultRealTimeProvider.Singleton).ConfigureAwait(false))
            using (AudioConformer conformer = new AudioConformer(graph, fileIn.OutputFormat, speechReco.InputFormat, "Conformer", resamplerFactory, logger.Clone("Conformer")))
            using (PassthroughAudioPipe2 pipe = new PassthroughAudioPipe2(graph, speechReco.InputFormat, "Pipe"))
            {
                fileIn.ConnectOutput(conformer);
                conformer.ConnectOutput(pipe);
                pipe.ConnectOutput(speechReco);

                int samplesToReadPerLoop = (int)AudioMath.ConvertTimeSpanToSamplesPerChannel(format.SampleRateHz, TimeSpan.FromMilliseconds(10));

                while (!fileIn.PlaybackFinished)
                {
                    long samples = await pipe.DriveGraph(samplesToReadPerLoop, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);
                    speechReco.
                }
            }
        }
    }
}
