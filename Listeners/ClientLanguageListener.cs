using ChatTranslatorHud.Services;
using ChatTranslatorHud.Utils;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace ChatTranslatorHud.Listeners;

internal sealed class ClientLanguageListener(ChatTranslatorHud plugin, ClientConVarQueryService conVarQueryService, PlayerTranslationService playerTranslationService, PlayerPreferenceService preferenceService, ILogger logger)
{
    private const int LanguageQueryRetries = 15;
    private const string LanguageConVar = "cl_language";
    private readonly Dictionary<int, ulong> _slotSteamIds = [];

    public void OnClientAuthorized(int playerSlot, SteamID steamId)
    {
        if (steamId.IsValid())
            _slotSteamIds[playerSlot] = steamId.SteamId64;

        ScheduleLanguageQuery(playerSlot, LanguageQueryRetries, 0.25f);
    }

    public void OnClientPutInServer(int playerSlot)
    {
        ScheduleLanguageQuery(playerSlot, LanguageQueryRetries, 1.0f);
    }

    public void OnClientDisconnect(int playerSlot)
    {
        if (!_slotSteamIds.Remove(playerSlot, out var steamId64))
            return;

        playerTranslationService.Remove(steamId64);
        preferenceService.Remove(steamId64);
    }

    public void RefreshConnectedPlayers()
    {
        foreach (var player in Utilities.GetPlayers())
            ScheduleLanguageQuery(player.Slot, LanguageQueryRetries, 0.25f);
    }

    public void Clear()
    {
        _slotSteamIds.Clear();
    }

    private void QueryPlayerLanguage(int playerSlot, int retriesRemaining)
    {
        var player = Utilities.GetPlayerFromSlot(playerSlot);
        if (!player.IsRealPlayer())
        {
            RetryLanguageQuery(playerSlot, retriesRemaining, "player is not ready");
            return;
        }

        var controller = player!;
        if (!TryResolveSteamId64(controller, playerSlot, out var steamId64))
        {
            RetryLanguageQuery(playerSlot, retriesRemaining, "SteamID is not ready");
            return;
        }

        _slotSteamIds[playerSlot] = steamId64;

        if (conVarQueryService.Query(controller, steamId64, LanguageConVar, OnLanguageQueryResult))
            return;

        RetryLanguageQuery(playerSlot, retriesRemaining, "client convar query could not be sent");
    }

    private void OnLanguageQueryResult(ClientConVarQueryResult result)
    {
        if (result.Status != ClientConVarQueryStatus.ValueIntact)
        {
            logger.LogWarning("Failed to detect player language from {Source} for player {SteamId64}: {Status}", result.Name, result.SteamId64, result.Status);
            return;
        }

        var language = result.Value.Trim();
        if (string.IsNullOrWhiteSpace(language))
        {
            logger.LogWarning("Failed to detect player language from {Source} for player {SteamId64}: empty value", result.Name, result.SteamId64);
            return;
        }

        ApplyLanguage(result.Player, result.SteamId64, language, result.Name);
    }

    private void ApplyLanguage(CCSPlayerController controller, ulong steamId64, string language, string source)
    {
        playerTranslationService.SetLanguage(steamId64, language);
        if (playerTranslationService.TryGetCultureInfo(language, out var cultureInfo))
        {
            var steamId = controller.AuthorizedSteamID ?? (SteamID)steamId64;
            PlayerLanguageManager.Instance.SetLanguage(steamId, cultureInfo);
            logger.LogDebug("Detected language {Language} from {Source} for player {SteamId64}; target={TargetLanguage}, culture={Culture}", language, source, steamId64, playerTranslationService.GetLanguage(steamId64), cultureInfo.Name);
        }
        else
        {
            logger.LogWarning("Unsupported language value {Language} from {Source} for player {SteamId64}; falling back to {TargetLanguage}", language, source, steamId64, playerTranslationService.GetLanguage(steamId64));
        }
    }

    private void RetryLanguageQuery(int playerSlot, int retriesRemaining, string reason)
    {
        if (retriesRemaining <= 0)
        {
            logger.LogWarning("Failed to detect player language for slot {PlayerSlot}: {Reason}", playerSlot, reason);
            return;
        }

        ScheduleLanguageQuery(playerSlot, retriesRemaining - 1, 1.0f);
    }

    private void ScheduleLanguageQuery(int playerSlot, int retriesRemaining, float delay)
    {
        plugin.AddTimer(delay, () => QueryPlayerLanguage(playerSlot, retriesRemaining), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool TryResolveSteamId64(CCSPlayerController player, int playerSlot, out ulong steamId64)
    {
        if (player.TryGetAuthorizedSteamId64(out steamId64))
            return true;

        return _slotSteamIds.TryGetValue(playerSlot, out steamId64);
    }
}
