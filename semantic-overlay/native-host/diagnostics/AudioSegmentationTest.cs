using System;
using System.Collections.Generic;

namespace SemanticOverlay.NativeHost
{
    internal static class AudioSegmentationTest
    {
        private static List<short> Frame(double amplitude, double phase)
        {
            List<short> samples = new List<short>();
            for (int index = 0; index < 320; index++)
                samples.Add((short)(amplitude * Math.Sin(phase + index * 2 * Math.PI * 440 / 16000)));
            return samples;
        }

        public static int Main()
        {
            using (SystemAudioCaptionCapture capture = new SystemAudioCaptionCapture())
            {
                for (int index = 0; index < 175; index++)
                    if (capture.ProcessMonoSamples(Frame(4200, index)) != null)
                        throw new InvalidOperationException("Speech was cut without a pause.");
                byte[] completed = null;
                for (int index = 0; index < 13; index++)
                    completed = capture.ProcessMonoSamples(Frame(0, 0)) ?? completed;
                if (completed == null || (completed.Length - 44) / 32000.0 > 4.0)
                    throw new InvalidOperationException("Natural pause did not submit speech before the hard cap.");
            }
            using (SystemAudioCaptionCapture capture = new SystemAudioCaptionCapture())
            {
                for (int index = 0; index < 50; index++)
                    capture.ProcessMonoSamples(Frame(4200, index));
                for (int index = 0; index < 13; index++)
                    if (capture.ProcessMonoSamples(Frame(0, 0)) != null)
                        throw new InvalidOperationException("Brief hesitation fragmented a short utterance.");
            }
            using (SystemAudioCaptionCapture capture = new SystemAudioCaptionCapture())
            {
                for (int index = 0; index < 80; index++)
                    if (capture.ProcessMonoSamples(Frame(45, index)) != null)
                        throw new InvalidOperationException("Low noise produced a speech segment.");

                byte[] completed = null;
                for (int index = 0; index < 35; index++)
                    completed = capture.ProcessMonoSamples(Frame(4200, index)) ?? completed;
                if (capture.LastPacketUtc == DateTime.MinValue ||
                    capture.LastVoiceUtc == DateTime.MinValue || capture.RecentLevelBars == 0)
                    throw new InvalidOperationException("Live audio activity was not exposed to status.");
                for (int index = 0; index < 40; index++)
                    completed = capture.ProcessMonoSamples(Frame(0, 0)) ?? completed;
                if (completed == null || completed.Length < 16000)
                    throw new InvalidOperationException("Speech with pre-roll did not produce a valid WAV segment.");
                if (completed[0] != (byte)'R' || completed[8] != (byte)'W')
                    throw new InvalidOperationException("Completed segment is not WAV.");
            }
            using (SystemAudioCaptionCapture capture = new SystemAudioCaptionCapture())
            {
                byte[] first = null, second = null;
                for (int index = 0; index < 850; index++)
                {
                    byte[] chunk = capture.ProcessMonoSamples(Frame(4200, index));
                    if (chunk == null) continue;
                    if (first == null)
                    {
                        first = chunk;
                        if (capture.LastCompletedHasOverlap)
                            throw new InvalidOperationException("First chunk falsely shares audio.");
                    }
                    else
                    {
                        second = chunk;
                        if (!capture.LastCompletedHasOverlap)
                            throw new InvalidOperationException("Continuation lost overlap metadata.");
                        break;
                    }
                }
                if (first == null || second == null)
                    throw new InvalidOperationException("Continuous speech stopped segmenting.");
                for (int offset = 0; offset < 32000; offset++)
                    if (first[first.Length - 32000 + offset] != second[44 + offset])
                        throw new InvalidOperationException("Retained boundary audio was discarded or reordered.");
                for (int index = 0; index < 45; index++)
                    if (capture.ProcessMonoSamples(Frame(0, 0)) != null)
                        throw new InvalidOperationException("Retained voice alone was emitted again on silence.");
            }
            Console.WriteLine("audio-segmentation-ok");
            return 0;
        }
    }
}
