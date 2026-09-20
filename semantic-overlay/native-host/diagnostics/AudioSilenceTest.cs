using System;
using System.Threading;
namespace SemanticOverlay.NativeHost
{
    internal static class AudioSilenceTest
    {
        static void Main()
        {
            int chunks = 0;
            using (SystemAudioCaptionCapture capture = new SystemAudioCaptionCapture())
            {
                capture.AudioChunkReady += delegate { chunks++; };
                capture.Start();
                Thread.Sleep(3000);
            }
            if (chunks != 0) throw new Exception("Silence generated audio chunks: " + chunks);
            Console.WriteLine("PASS 3 seconds of system silence produced no uploadable chunks");
        }
    }
}
