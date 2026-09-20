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
                for (int index = 0; index < 80; index++)
                    if (capture.ProcessMonoSamples(Frame(45, index)) != null)
                        throw new InvalidOperationException("Low noise produced a speech segment.");

                byte[] completed = null;
                for (int index = 0; index < 35; index++)
                    completed = capture.ProcessMonoSamples(Frame(4200, index)) ?? completed;
                for (int index = 0; index < 40; index++)
                    completed = capture.ProcessMonoSamples(Frame(0, 0)) ?? completed;
                if (completed == null || completed.Length < 16000)
                    throw new InvalidOperationException("Speech with pre-roll did not produce a valid WAV segment.");
                if (completed[0] != (byte)'R' || completed[8] != (byte)'W')
                    throw new InvalidOperationException("Completed segment is not WAV.");
            }
            Console.WriteLine("audio-segmentation-ok");
            return 0;
        }
    }
}
