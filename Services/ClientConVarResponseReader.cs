using System.Runtime.InteropServices;
using System.Text;

namespace ChatTranslatorHud.Services;

public readonly record struct ClientConVarResponse(int Cookie, int StatusCode, string Name, string Value);

public static class ClientConVarResponseReader
{
    private const int MaxStringBytes = 512;

    private static readonly object _bridgeLock = new();
    private static bool _disposed;
    private static Exception? _nativeBridgeError;
    private static NativeBridge? _nativeBridge;
    private static string? _nativeBridgeDirectory;

    public static void DisposeBridge()
    {
        lock (_bridgeLock)
        {
            _disposed = true;
            var bridge = _nativeBridge;
            _nativeBridge = null;
            _nativeBridgeError = null;
            bridge?.Dispose();
        }
    }

    public static void ResetForReload(string? directory)
    {
        lock (_bridgeLock)
        {
            _disposed = false;
            _nativeBridgeError = null;
            _nativeBridgeDirectory = directory;
        }
    }

    public static bool IsNativeBridgeAvailable(out Exception? error)
    {
        lock (_bridgeLock)
        {
            if (_disposed)
            {
                error = new ObjectDisposedException(nameof(ClientConVarResponseReader));
                return false;
            }

            if (_nativeBridge != null)
            {
                error = null;
                return true;
            }

            NativeBridge? loadedBridge = null;
            try
            {
                loadedBridge = NativeBridge.Load(_nativeBridgeDirectory);
                if (loadedBridge.Version() < 7)
                    throw new InvalidOperationException("ChatTranslatorHud.Native returned an unsupported version.");

                _nativeBridge = loadedBridge;
                _nativeBridgeError = null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                if (loadedBridge != null && _nativeBridge != loadedBridge)
                    loadedBridge.Dispose();

                _nativeBridgeError = ex;
            }

            error = _nativeBridgeError;
            return _nativeBridge != null;
        }
    }

    public static int InitHook(IntPtr targetFunction)
    {
        lock (_bridgeLock)
        {
            if (_disposed || _nativeBridge == null) return -1;
            return _nativeBridge.InitHook(targetFunction);
        }
    }

    public static bool TryPopResponse(out ClientConVarResponse response)
    {
        response = default;

        NativeBridge? bridge;
        lock (_bridgeLock)
        {
            if (_disposed || _nativeBridge == null)
                return false;
            bridge = _nativeBridge;
        }

        if (!IsNativeBridgeAvailable(out _))
            return false;

        // Skip memory allocation and C++ call if the queue is empty
        if (!bridge.HasResponse())
            return false;

        var nativeResponse = NativeConVarResponse.Create();
        if (!bridge.PopResponse(ref nativeResponse))
            return false;

        if (nativeResponse.StatusCode is < 0 or > 3)
            return false;

        var name = ReadUtf8(nativeResponse.Name, nativeResponse.NameLength);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        response = new ClientConVarResponse(
            nativeResponse.Cookie,
            nativeResponse.StatusCode,
            name,
            ReadUtf8(nativeResponse.Value, nativeResponse.ValueLength));

        return true;
    }

    public static bool TryGetMetrics(out int scanCalls, out int scanHits, out int scanExceptions)
    {
        scanCalls = 0;
        scanHits = 0;
        scanExceptions = 0;

        lock (_bridgeLock)
        {
            if (_disposed || _nativeBridge == null)
                return false;

            return _nativeBridge.GetMetrics(out scanCalls, out scanHits, out scanExceptions);
        }
    }

    private static string ReadUtf8(byte[] buffer, int length)
    {
        var byteCount = Math.Clamp(length, 0, Math.Min(buffer.Length, MaxStringBytes - 1));
        var terminator = Array.IndexOf(buffer, (byte)0, 0, byteCount);
        if (terminator >= 0)
            byteCount = terminator;

        return byteCount == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, byteCount);
    }

    private sealed class NativeBridge : IDisposable
    {
        private readonly NativeVersionDelegate _version;
        private readonly InitHookDelegate _initHook;
        private readonly PopResponseDelegate _popResponse;
        private readonly ShutdownHookDelegate? _shutdownHook;
        private readonly HasResponseDelegate? _hasResponse;
        private readonly GetMetricDelegate? _getScanCalls;
        private readonly GetMetricDelegate? _getScanHits;
        private readonly GetMetricDelegate? _getScanExceptions;
        private IntPtr _handle;

        private NativeBridge(IntPtr handle)
        {
            _handle = handle;
            _version = Marshal.GetDelegateForFunctionPointer<NativeVersionDelegate>(
                NativeLibrary.GetExport(handle, "ChatTranslatorHud_NativeVersion"));
            _initHook = Marshal.GetDelegateForFunctionPointer<InitHookDelegate>(
                NativeLibrary.GetExport(handle, "ChatTranslatorHud_InitHook"));
            _popResponse = Marshal.GetDelegateForFunctionPointer<PopResponseDelegate>(
                NativeLibrary.GetExport(handle, "ChatTranslatorHud_PopResponse"));

            if (NativeLibrary.TryGetExport(handle, "ChatTranslatorHud_ShutdownHook", out var shutdownHookAddress))
                _shutdownHook = Marshal.GetDelegateForFunctionPointer<ShutdownHookDelegate>(shutdownHookAddress);

            if (NativeLibrary.TryGetExport(handle, "ChatTranslatorHud_HasResponse", out var hasResponseAddress))
                _hasResponse = Marshal.GetDelegateForFunctionPointer<HasResponseDelegate>(hasResponseAddress);

            if (NativeLibrary.TryGetExport(handle, "ChatTranslatorHud_GetScanCalls", out var scanCallsAddress))
                _getScanCalls = Marshal.GetDelegateForFunctionPointer<GetMetricDelegate>(scanCallsAddress);

            if (NativeLibrary.TryGetExport(handle, "ChatTranslatorHud_GetScanHits", out var scanHitsAddress))
                _getScanHits = Marshal.GetDelegateForFunctionPointer<GetMetricDelegate>(scanHitsAddress);

            if (NativeLibrary.TryGetExport(handle, "ChatTranslatorHud_GetScanExceptions", out var scanExceptionsAddress))
                _getScanExceptions = Marshal.GetDelegateForFunctionPointer<GetMetricDelegate>(scanExceptionsAddress);
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    _shutdownHook?.Invoke();
                }
                catch
                {
                    // Ignore exceptions during native resource release to prevent crash on exit/unload
                }
                NativeLibrary.Free(handle);
            }
        }

        public static NativeBridge Load(string? configuredDirectory)
        {
            var path = GetCandidatePaths(configuredDirectory).FirstOrDefault(File.Exists);

            if (path == null)
            {
                var searched = string.Join(", ", GetCandidatePaths(configuredDirectory));
                throw new DllNotFoundException($"Native bridge was not found. Searched: {searched}");
            }

            var handle = NativeLibrary.Load(path);
            try
            {
                return new NativeBridge(handle);
            }
            catch
            {
                NativeLibrary.Free(handle);
                throw;
            }
        }

        private static string GetNativeLibraryName()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "ChatTranslatorHud.Native.dll"
                : "ChatTranslatorHud.Native.so";
        }

        private static IEnumerable<string> GetCandidatePaths(string? configuredDirectory)
        {
            var fileName = GetNativeLibraryName();

            if (!string.IsNullOrWhiteSpace(configuredDirectory))
                yield return Path.Combine(configuredDirectory, fileName);

            var assemblyDirectory = Path.GetDirectoryName(typeof(ClientConVarResponseReader).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
                yield return Path.Combine(assemblyDirectory, fileName);

            if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
                yield return Path.Combine(AppContext.BaseDirectory, fileName);
        }

        public int Version()
        {
            return _version();
        }

        public int InitHook(IntPtr targetFunction)
        {
            return _initHook(targetFunction);
        }

        public bool PopResponse(ref NativeConVarResponse response)
        {
            return _popResponse(ref response);
        }

        public bool HasResponse()
        {
            return _hasResponse?.Invoke() ?? true;
        }

        public bool GetMetrics(out int scanCalls, out int scanHits, out int scanExceptions)
        {
            scanCalls = _getScanCalls?.Invoke() ?? 0;
            scanHits = _getScanHits?.Invoke() ?? 0;
            scanExceptions = _getScanExceptions?.Invoke() ?? 0;
            return _getScanCalls != null;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeVersionDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitHookDelegate(IntPtr targetFunction);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PopResponseDelegate(ref NativeConVarResponse response);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ShutdownHookDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool HasResponseDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetMetricDelegate();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeConVarResponse
    {
        public int Cookie;
        public int StatusCode;
        public int NameLength;
        public int ValueLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxStringBytes)]
        public byte[] Name;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxStringBytes)]
        public byte[] Value;

        public static NativeConVarResponse Create()
        {
            return new NativeConVarResponse
            {
                Name = new byte[MaxStringBytes],
                Value = new byte[MaxStringBytes]
            };
        }
    }
}
