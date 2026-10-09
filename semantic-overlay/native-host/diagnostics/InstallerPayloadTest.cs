using System;
using System.IO;
using System.IO.Compression;

namespace RealtimeDictionary.Setup
{
    // Uses an actual newly generated package; does not install, touch shortcuts,
    // stop processes, read credentials or call providers.
    internal static class InstallerPayloadTest
    {
        private static MemoryStream Variant(string path, string omit, bool changeVersion)
        {
            var result = new MemoryStream();
            using (var input = ZipFile.OpenRead(path))
            using (var output = new ZipArchive(result, ZipArchiveMode.Create, true))
            {
                foreach (var entry in input.Entries)
                {
                    if (entry.FullName == omit) continue;
                    var added = output.CreateEntry(entry.FullName);
                    using (var destination = added.Open())
                    {
                        if (changeVersion && entry.FullName == "version.txt")
                        { using (var writer = new StreamWriter(destination)) writer.Write("mismatched-version"); }
                        else using (var source = entry.Open()) source.CopyTo(destination);
                    }
                }
            }
            result.Position = 0;
            return result;
        }
        private static int Main(string[] args)
        {
            try
            {
                if (!Program.VerifyPayload(File.OpenRead(args[0]))) throw new Exception("Current package rejected");
                foreach (string dependency in new[] { "runtime/python.exe", "server.py",
                    "ocr_service.py", "native-host/windows_ocr_worker.ps1", "provider_transport.py" })
                    if (Program.VerifyPayload(Variant(args[0], dependency, false)))
                        throw new Exception("Missing dependency accepted: " + dependency);
                if (Program.VerifyPayload(Variant(args[0], null, true)))
                    throw new Exception("Mismatched version accepted");
                Console.WriteLine("current-package-accepted-missing-dependencies-and-version-rejected-ok");
                return 0;
            }
            catch (Exception error) { Console.WriteLine(error); return 1; }
        }
    }
}
