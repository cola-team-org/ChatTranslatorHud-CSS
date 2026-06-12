using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Modules.Timers;

namespace ChatTranslatorHud.Services;

internal enum ClientConVarQueryStatus
{
    ValueIntact = 0,
    CvarNotFound = 1,
    NotACvar = 2,
    CvarProtected = 3
}

internal sealed record ClientConVarQueryResult(
    CCSPlayerController Player,
    ulong SteamId64,
    ClientConVarQueryStatus Status,
    string Name,
    string Value);

internal sealed class ClientConVarQueryService
{
    private const string RequestMessageName = "GetCvarValue";
    private const string ResponseMessageName = "RespondCvarValue";
    private const int QueryTimeoutSeconds = 10;
    private const int RespondCvarValueWindowsVtableIndex = 38;
    private const int RespondCvarValueLinuxVtableIndex = 40;

    private readonly ChatTranslatorHud _plugin;
    private readonly ILogger _logger;
    private readonly bool _useNativeResponseHook;
    private readonly Dictionary<int, PendingClientConVarQuery> _pendingQueries = [];
    private readonly object _pendingQueriesLock = new();
    private int _nextCookie;
    private int _responseMessageId = -1;
    private bool _responseHookRegistered;
    private bool _responseHookUnavailableLogged;
    private bool _nativeResponseHookRegistered;
    private bool _nativeResponseHookUnavailableLogged;
    private bool _requestMessageInfoLogged;
    private bool _responseObservedLogged;
    private bool _responseTimeoutGuidanceLogged;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _pollTimer;
    private DateTimeOffset _lastMetricsLogTime = DateTimeOffset.UtcNow;

    public ClientConVarQueryService(ChatTranslatorHud plugin, ILogger logger, bool useNativeResponseHook)
    {
        _plugin = plugin;
        _logger = logger;
        _useNativeResponseHook = useNativeResponseHook;
    }

    public bool Query(CCSPlayerController player, ulong steamId64, string name, Action<ClientConVarQueryResult> callback)
    {
        if (player is not { IsValid: true } || player.IsBot)
            return false;

        if (!TryEnsureResponseHook())
            return false;

        PruneExpiredQueries();

        int cookie;
        lock (_pendingQueriesLock)
        {
            do
            {
                cookie = Interlocked.Increment(ref _nextCookie);
            } while (cookie == 0 || _pendingQueries.ContainsKey(cookie));

            _pendingQueries[cookie] = new PendingClientConVarQuery(player.Slot, steamId64, name, DateTimeOffset.UtcNow, callback);
        }

        try
        {
            using var message = UserMessage.FromPartialName(RequestMessageName);
            LogRequestMessageInfo(message);
            message.SetInt("cookie", cookie);
            message.SetString("cvar_name", name);
            message.Send(new RecipientFilter(player));
            return true;
        }
        catch (Exception ex)
        {
            lock (_pendingQueriesLock)
            {
                _pendingQueries.Remove(cookie);
            }

            _logger.LogWarning(ex, "Failed to query client convar {ConVar} for player {SteamId64}", name, steamId64);
            return false;
        }
    }

    public void Clear()
    {
        lock (_pendingQueriesLock)
        {
            _pendingQueries.Clear();
        }

        if (_nativeResponseHookRegistered)
        {
            _pollTimer?.Kill();
            _pollTimer = null;
            _nativeResponseHookRegistered = false;
        }

        if (_responseHookRegistered && _responseMessageId >= 0)
        {
            _plugin.UnhookUserMessage(_responseMessageId, OnRespondCvarValue, HookMode.Pre);
            _responseHookRegistered = false;
            _responseMessageId = -1;
        }
    }

    private HookResult OnRespondCvarValue(UserMessage message)
    {
        return HandleRespondCvarValue(message, "CSSSharp UserMessage hook");
    }

    private void PollNativeQueue()
    {
        while (ClientConVarResponseReader.TryPopResponse(out var response))
        {
            try
            {
                HandleRespondCvarValue(response, "Native Bridge Hook");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling native bridge hook response");
            }
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastMetricsLogTime).TotalSeconds >= 60)
            {
                _lastMetricsLogTime = now;
                if (ClientConVarResponseReader.TryGetMetrics(out int scanCalls, out int scanHits, out int scanExceptions))
                {
                    _logger.LogDebug("[Native Hook Metrics] Scan Calls: {ScanCalls}, Scan Hits: {ScanHits}, Scan Exceptions: {ScanExceptions}",
                        scanCalls, scanHits, scanExceptions);
                }
            }
        }
    }

    private HookResult HandleRespondCvarValue(UserMessage message, string hookSource)
    {
        var response = new ClientConVarResponse(
            ReadInt(message, "cookie"),
            ReadInt(message, "status_code"),
            ReadString(message, "name"),
            ReadString(message, "value"));

        return HandleRespondCvarValue(response, hookSource);
    }

    private HookResult HandleRespondCvarValue(ClientConVarResponse response, string hookSource)
    {
        var cookie = response.Cookie;
        PendingClientConVarQuery? query;
        lock (_pendingQueriesLock)
        {
            if (cookie == 0 || !_pendingQueries.Remove(cookie, out query))
                return HookResult.Continue;
        }

        if (query == null)
            return HookResult.Continue;

        var player = Utilities.GetPlayerFromSlot(query.PlayerSlot);
        if (player is not { IsValid: true } || player.IsBot)
            return HookResult.Continue;

        if (player.AuthorizedSteamID?.SteamId64 != query.SteamId64)
            return HookResult.Continue;

        var status = (ClientConVarQueryStatus)response.StatusCode;
        var name = response.Name;
        var value = response.Value;

        if (!string.Equals(name, query.Name, StringComparison.OrdinalIgnoreCase))
            return HookResult.Continue;

        if (!_responseObservedLogged)
        {
            _logger.LogInformation("Observed client convar response via {HookSource}: {ResponseMessageName}", hookSource, ResponseMessageName);
            _responseObservedLogged = true;
        }

        query.Callback(new ClientConVarQueryResult(player, query.SteamId64, status, name, value));
        return HookResult.Continue;
    }

    private bool TryEnsureResponseHook()
    {
        if (_useNativeResponseHook && TryEnsureNativeResponseHook())
            return true;

        if (_responseHookRegistered)
            return true;

        try
        {
            _responseMessageId = UserMessage.FindIdByName(ResponseMessageName);
            _plugin.HookUserMessage(_responseMessageId, OnRespondCvarValue, HookMode.Pre);
            _responseHookRegistered = true;

            using var responseMessage = UserMessage.FromId(_responseMessageId);
            _logger.LogInformation("Registered client convar response hook for {MessageName} ({MessageId}, type {MessageType})", responseMessage.Name, responseMessage.Id, responseMessage.Type);
            return true;
        }
        catch (Exception ex)
        {
            if (!_responseHookUnavailableLogged)
            {
                _logger.LogWarning(ex, "Failed to register client convar response hook for {MessageName}", ResponseMessageName);
                _responseHookUnavailableLogged = true;
            }

            return false;
        }
    }

    private bool TryEnsureNativeResponseHook()
    {
        if (_nativeResponseHookRegistered)
            return true;

        try
        {
            if (!ClientConVarResponseReader.IsNativeBridgeAvailable(out var bridgeError))
            {
                if (bridgeError != null)
                    throw new InvalidOperationException("ChatTranslatorHud.Native bridge is not available.", bridgeError);

                throw new InvalidOperationException("ChatTranslatorHud.Native bridge is not available.");
            }

            var vtableIndex = GetRespondCvarValueVtableIndex();
            IntPtr vtable = NativeAPI.FindVirtualTable(Addresses.EnginePath, "CServerSideClient");
            if (vtable == IntPtr.Zero)
                throw new InvalidOperationException("Failed to find CServerSideClient vtable.");

            IntPtr targetFunc = Marshal.ReadIntPtr(vtable + (vtableIndex * 8));
            if (targetFunc == IntPtr.Zero)
                throw new InvalidOperationException("Failed to read ProcessRespondCvarValue function pointer from vtable.");

            int initResult = ClientConVarResponseReader.InitHook(targetFunc);
            if (initResult != 0 && initResult != -2) // -2 is ALREADY_INITIALIZED, which is fine on reload
                throw new InvalidOperationException($"ChatTranslatorHud.Native InitHook returned {initResult}. (MinHook failed)");

            _pollTimer = _plugin.AddTimer(0.1f, PollNativeQueue, TimerFlags.REPEAT);
            _nativeResponseHookRegistered = true;

            _logger.LogInformation("Registered client convar native MinHook for CServerSideClient::CLCMsg_RespondCvarValue at vtable index {VtableIndex} in {EnginePath} (Pointer: {Ptr})", vtableIndex, Addresses.EnginePath, targetFunc.ToString("X"));
            return true;
        }
        catch (Exception ex)
        {
            if (!_nativeResponseHookUnavailableLogged)
            {
                _logger.LogWarning(ex, "Failed to register native CServerSideClient::CLCMsg_RespondCvarValue hook; falling back to CSSSharp UserMessage hook");
                _nativeResponseHookUnavailableLogged = true;
            }

            _nativeResponseHookRegistered = false;
            return false;
        }
    }

    private void PruneExpiredQueries()
    {
        var expiresBefore = DateTimeOffset.UtcNow.AddSeconds(-QueryTimeoutSeconds);
        KeyValuePair<int, PendingClientConVarQuery>[] expiredQueries;
        lock (_pendingQueriesLock)
        {
            if (_pendingQueries.Count == 0)
                return;

            expiredQueries = _pendingQueries
                .Where(pair => pair.Value.CreatedAt <= expiresBefore)
                .ToArray();

            foreach (var pair in expiredQueries)
                _pendingQueries.Remove(pair.Key);
        }

        foreach (var pair in expiredQueries)
        {
            var query = pair.Value;

            _logger.LogDebug("Client convar query {ConVar} timed out for player {SteamId64} in slot {PlayerSlot}", query.Name, query.SteamId64, query.PlayerSlot);

            if (!_responseTimeoutGuidanceLogged)
            {
                _logger.LogWarning("Client convar query timed out without {ResponseMessageName}; the active response hook did not receive or match inbound CLCMsg_RespondCvarValue.", ResponseMessageName);
                _responseTimeoutGuidanceLogged = true;
            }
        }
    }

    private void LogRequestMessageInfo(UserMessage message)
    {
        if (_requestMessageInfoLogged)
            return;

        _logger.LogInformation("Prepared client convar request message {MessageName} ({MessageId}, type {MessageType})", message.Name, message.Id, message.Type);
        _requestMessageInfoLogged = true;
    }

    private static int GetRespondCvarValueVtableIndex()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? RespondCvarValueWindowsVtableIndex
            : RespondCvarValueLinuxVtableIndex;
    }

    private static int ReadInt(UserMessage message, string fieldName)
    {
        return message.HasField(fieldName) ? message.ReadInt(fieldName) : 0;
    }

    private static string ReadString(UserMessage message, string fieldName)
    {
        return message.HasField(fieldName) ? message.ReadString(fieldName) : string.Empty;
    }

    private sealed record PendingClientConVarQuery(
        int PlayerSlot,
        ulong SteamId64,
        string Name,
        DateTimeOffset CreatedAt,
        Action<ClientConVarQueryResult> Callback);
}
