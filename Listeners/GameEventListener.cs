using ChatTranslatorHud.Services;
using ChatTranslatorHud.Utils;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ChatTranslatorHud.Listeners;

internal sealed class GameEventListener(
    ChatTranslatorConfig config,
    TranslationService translationService,
    TranslationCache translationCache,
    PlayerTranslationService playerTranslationService,
    PlayerPreferenceService preferenceService,
    HudDisplayService hudDisplayService,
    RoundMessageContext roundMessageContext,
    ILogger logger)
{
    private readonly ConsoleSayMessageGate _consoleSayGate = new();

    public HookResult OnSayCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        var message = commandInfo.ArgString?.Trim().Trim('"') ?? "";
        var decision = _consoleSayGate.ShouldHandle(
            config.EnableTranslation,
            player is not null,
            commandInfo.CallingContext,
            message,
            DateTime.UtcNow);

        if (!decision.Handle)
            return HookResult.Continue;

        if (!decision.Duplicate)
        {
            _ = ProcessConsoleMessageAsync(message).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    logger.LogError(t.Exception, "Unobserved error in ProcessConsoleMessageAsync");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        return HookResult.Stop;
    }

    public void OnMapStart(string mapName)
    {
        translationCache.SetCurrentMap(string.IsNullOrWhiteSpace(mapName) ? Server.MapName : mapName);
    }

    public void OnMapEnd()
    {
        hudDisplayService.Clear();
        roundMessageContext.Clear();
        translationCache.Flush();
    }

    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        roundMessageContext.Clear();
        return HookResult.Continue;
    }

    public void Clear()
    {
        _consoleSayGate.Clear();
    }

    private async Task ProcessConsoleMessageAsync(string message)
    {
        try
        {
            var recipients = GetRecipients();
            if (recipients.Count == 0)
                return;

            if (MessageParser.IsCountdownOnly(message))
            {
                var parseResult = MessageParser.TryParseMessage(message);
                if (parseResult.IsValid)
                {
                    await ProcessCountdownMessageAsync(message, parseResult, recipients);
                    return;
                }
            }

            await ProcessStaticMessageAsync(message, recipients);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing console say message: {Message}", message);
            try { await SendFallbackOriginalAsync(message); }
            catch (Exception fallbackEx) { logger.LogError(fallbackEx, "Fallback delivery also failed"); }
        }
    }

    private async Task ProcessCountdownMessageAsync(string message, ParseResult parseResult, List<PlayerRecipient> recipients)
    {
        var languageGroups = BuildLanguageGroups(recipients);
        var translations = languageGroups.Count > 0
            ? await TranslateForLanguagesAsync(message, languageGroups.Keys)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (config.UseRoundContext && translations.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
            roundMessageContext.Push(message);

        var templates = BuildCountdownTemplates(parseResult, translations);
        await Server.NextWorldUpdateAsync(() =>
        {
            SendTranslatedChatMessages(message, languageGroups, translations);
            SendFallbackOriginalToUngrouped(message, recipients, languageGroups);

            if (parseResult.Seconds > 5)
            {
                var defaultTranslation = translations.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? message;
                hudDisplayService.AddStaticMessage(defaultTranslation, message, DateTimeOffset.UtcNow);
            }

            hudDisplayService.AddCountdown(parseResult, message, DateTimeOffset.UtcNow, templates);
        });
    }

    private async Task ProcessStaticMessageAsync(string message, List<PlayerRecipient> recipients)
    {
        var languageGroups = BuildLanguageGroups(recipients);
        if (languageGroups.Count == 0)
        {
            await SendFallbackOriginalAsync(message);
            return;
        }

        var translations = await TranslateForLanguagesAsync(message, languageGroups.Keys);
        if (config.UseRoundContext && translations.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
            roundMessageContext.Push(message);

        var defaultTranslation = translations.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? message;
        await Server.NextWorldUpdateAsync(() =>
        {
            SendTranslatedChatMessages(message, languageGroups, translations);
            hudDisplayService.AddStaticMessage(defaultTranslation, message, DateTimeOffset.UtcNow);
        });
    }

    private async Task<Dictionary<string, string?>> TranslateForLanguagesAsync(string message, IEnumerable<string> languages)
    {
        var roundContext = config.UseRoundContext ? roundMessageContext.GetContext() : null;
        var cacheMapName = translationCache.CurrentMapName;
        var translations = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var language in languages)
        {
            if (translationCache.TryGetTranslation(message, language, out var cached))
            {
                translations[language] = cached;
                continue;
            }

            var translated = await translationService.TranslateAsync(message, language, roundContext);
            translations[language] = translated;
            if (!string.IsNullOrWhiteSpace(translated))
                translationCache.SaveTranslation(message, language, translated, cacheMapName);
        }

        return translations;
    }

    private static List<PlayerRecipient> GetRecipients()
    {
        var recipients = new List<PlayerRecipient>();
        foreach (var currentPlayer in Utilities.GetPlayers())
        {
            if (currentPlayer.TryGetAuthorizedSteamId64(out var steamId64))
                recipients.Add(new PlayerRecipient(currentPlayer.Slot, steamId64));
        }

        return recipients;
    }

    private Dictionary<string, List<PlayerRecipient>> BuildLanguageGroups(IEnumerable<PlayerRecipient> recipients)
    {
        var languageGroups = new Dictionary<string, List<PlayerRecipient>>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in recipients)
        {
            var language = playerTranslationService.GetLanguage(recipient.SteamId64);
            if (string.IsNullOrWhiteSpace(language))
                continue;

            if (!languageGroups.TryGetValue(language, out var group))
            {
                group = [];
                languageGroups[language] = group;
            }

            group.Add(recipient);
        }

        return languageGroups;
    }

    private void SendTranslatedChatMessages(
        string message,
        IReadOnlyDictionary<string, List<PlayerRecipient>> languageGroups,
        IReadOnlyDictionary<string, string?> translations)
    {
        foreach (var (language, playerRecipients) in languageGroups)
        {
            translations.TryGetValue(language, out var translatedText);
            var hasDistinctTranslation = !string.IsNullOrWhiteSpace(translatedText)
                && !string.Equals(translatedText, message, StringComparison.Ordinal);

            foreach (var recipient in playerRecipients)
            {
                if (!TryResolveRecipient(recipient, out var player))
                    continue;

                if (hasDistinctTranslation)
                {
                    if (preferenceService.IsOriginalMessageEnabled(player))
                        player.PrintToChat($" {ChatColors.LightRed}Console:{ChatColors.White} {message}");

                    player.PrintToChat($" {ChatColors.LightRed}[Translated]{ChatColors.Green} {translatedText}");
                }
                else
                {
                    player.PrintToChat($" {ChatColors.LightRed}Console:{ChatColors.Green} {message}");
                }
            }
        }
    }

    private static void SendFallbackOriginalToUngrouped(
        string message,
        IEnumerable<PlayerRecipient> recipients,
        IReadOnlyDictionary<string, List<PlayerRecipient>> languageGroups)
    {
        foreach (var recipient in recipients)
        {
            if (!TryResolveRecipient(recipient, out var player))
                continue;

            var isGrouped = languageGroups.Values.Any(group => group.Any(grouped => grouped.SteamId64 == recipient.SteamId64));
            if (!isGrouped)
                player.PrintToChat($" {ChatColors.LightRed}Console:{ChatColors.Green} {message}");
        }
    }

    private static async Task SendFallbackOriginalAsync(string message)
    {
        await Server.NextWorldUpdateAsync(() =>
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (player.IsRealPlayer())
                    player.PrintToChat($" {ChatColors.LightRed}Console:{ChatColors.Green} {message}");
            }
        });
    }

    private static IReadOnlyDictionary<string, CountdownTextTemplate>? BuildCountdownTemplates(
        ParseResult parseResult,
        IReadOnlyDictionary<string, string?> translations)
    {
        var secondsText = parseResult.Seconds.ToString();
        var templates = new Dictionary<string, CountdownTextTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (language, translatedText) in translations)
        {
            if (string.IsNullOrWhiteSpace(translatedText))
                continue;

            var index = translatedText.IndexOf(secondsText, StringComparison.Ordinal);
            if (index < 0)
                continue;

            templates[language] = new CountdownTextTemplate(
                translatedText[..index],
                translatedText[(index + secondsText.Length)..]);
        }

        return templates.Count == 0 ? null : templates;
    }

    /// <summary>
    /// Resolves a player from a saved slot, verifying SteamID matches to guard against
    /// slot reuse by a different player after the original disconnected.
    /// </summary>
    private static bool TryResolveRecipient(PlayerRecipient recipient, out CCSPlayerController player)
    {
        player = Utilities.GetPlayerFromSlot(recipient.Slot)!;
        if (player == null || !player.IsRealPlayer())
            return false;

        if (!player.TryGetAuthorizedSteamId64(out var currentSteamId64))
            return false;

        return currentSteamId64 == recipient.SteamId64;
    }

    private sealed record PlayerRecipient(int Slot, ulong SteamId64);
}
