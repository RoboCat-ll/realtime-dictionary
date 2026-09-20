using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;

namespace SemanticOverlay.NativeHost
{
    /// <summary>
    /// Small .NET Framework bridge for the documented Windows process-loopback
    /// activation protocol. NAudio 1.x can consume the activated IAudioClient,
    /// but predates the public activation helper.
    /// </summary>
    internal static class ProcessLoopbackAudioClient
    {
        private const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";
        private const ushort VariantBlob = 65;
        private static readonly Guid AudioClientInterfaceId =
            new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

        public static bool IsSupported
        {
            get { return GetWindowsBuild() >= 20348; }
        }

        public static AudioClient Activate(uint processId, TimeSpan timeout)
        {
            if (!IsSupported)
                throw new PlatformNotSupportedException("按进程捕获需要 Windows 10 build 20348 或更高版本。");
            ActivationParameters parameters = new ActivationParameters();
            parameters.ActivationType = ActivationType.ProcessLoopback;
            parameters.Process.ProcessId = processId;
            parameters.Process.Mode = ProcessLoopbackMode.IncludeProcessTree;

            IntPtr parameterMemory = IntPtr.Zero;
            IntPtr variantMemory = IntPtr.Zero;
            IActivateAudioInterfaceAsyncOperation operation = null;
            try
            {
                int size = Marshal.SizeOf(typeof(ActivationParameters));
                parameterMemory = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(parameters, parameterMemory, false);
                BlobVariant variant = new BlobVariant();
                variant.Type = VariantBlob;
                variant.Size = size;
                variant.Data = parameterMemory;
                variantMemory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(BlobVariant)));
                Marshal.StructureToPtr(variant, variantMemory, false);

                ActivationCompletion completion = new ActivationCompletion();
                Guid requested = AudioClientInterfaceId;
                int result = ActivateAudioInterfaceAsync(
                    VirtualProcessLoopbackDevice, ref requested, variantMemory,
                    completion, out operation);
                Marshal.ThrowExceptionForHR(result);
                if (!completion.Wait(timeout))
                    throw new TimeoutException("Windows 打开会议进程音频超时。");
                return WrapForLegacyNAudio(completion.GetActivatedObject());
            }
            finally
            {
                if (operation != null && Marshal.IsComObject(operation))
                    Marshal.ReleaseComObject(operation);
                if (variantMemory != IntPtr.Zero) Marshal.FreeHGlobal(variantMemory);
                if (parameterMemory != IntPtr.Zero) Marshal.FreeHGlobal(parameterMemory);
            }
        }

        private static AudioClient WrapForLegacyNAudio(object activatedObject)
        {
            if (activatedObject == null)
                throw new InvalidOperationException("Windows 未返回进程音频接口。");
            Assembly assembly = typeof(AudioClient).Assembly;
            Type interfaceType = assembly.GetType("NAudio.CoreAudioApi.Interfaces.IAudioClient", true);
            ConstructorInfo constructor = typeof(AudioClient).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new Type[] { interfaceType }, null);
            if (constructor == null)
                throw new MissingMethodException("当前 NAudio 无法包装进程音频接口。");
            IntPtr unknown = Marshal.GetIUnknownForObject(activatedObject);
            try
            {
                object typed = Marshal.GetTypedObjectForIUnknown(unknown, interfaceType);
                return (AudioClient)constructor.Invoke(new object[] { typed });
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        private static int GetWindowsBuild()
        {
            OsVersionInfo info = new OsVersionInfo();
            info.Size = Marshal.SizeOf(typeof(OsVersionInfo));
            return RtlGetVersion(ref info) == 0 ? info.Build : Environment.OSVersion.Version.Build;
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid interfaceId, IntPtr activationParameters,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int RtlGetVersion(ref OsVersionInfo versionInfo);

        [ComImport]
        [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            [PreserveSig]
            int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport]
        [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig]
            int GetActivateResult(out int activationResult,
                [MarshalAs(UnmanagedType.IUnknown)] out object activatedObject);
        }

        [ComImport]
        [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject { }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class ActivationCompletion :
            IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            private readonly ManualResetEventSlim completed = new ManualResetEventSlim(false);
            private int activationResult = unchecked((int)0x80004005);
            private object activatedObject;
            private Exception error;

            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                try
                {
                    int callResult = operation.GetActivateResult(
                        out activationResult, out activatedObject);
                    if (callResult < 0) activationResult = callResult;
                }
                catch (Exception exception)
                {
                    error = exception;
                }
                finally
                {
                    completed.Set();
                }
                return 0;
            }

            public bool Wait(TimeSpan timeout) { return completed.Wait(timeout); }

            public object GetActivatedObject()
            {
                if (error != null) throw error;
                Marshal.ThrowExceptionForHR(activationResult);
                return activatedObject;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlobVariant
        {
            public ushort Type;
            public ushort Reserved1;
            public ushort Reserved2;
            public ushort Reserved3;
            public int Size;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ActivationParameters
        {
            public ActivationType ActivationType;
            public ProcessParameters Process;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessParameters
        {
            public uint ProcessId;
            public ProcessLoopbackMode Mode;
        }

        private enum ActivationType { Default, ProcessLoopback }
        private enum ProcessLoopbackMode { IncludeProcessTree, ExcludeProcessTree }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            public int Size;
            public int Major;
            public int Minor;
            public int Build;
            public int Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ServicePack;
        }
    }
}
