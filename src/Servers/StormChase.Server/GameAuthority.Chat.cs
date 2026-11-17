using System.Text;
using StormChase.Protocol;

namespace StormChase.Server;

/// <summary>
/// District chat. The host is the authority: it cleans and rate-limits every line, then delivers Local chat to players
/// within earshot and World chat to everyone in the district. Joins and leaves are announced by the district itself.
/// </summary>
public sealed partial class GameAuthority
{
    public const float LocalChatMeters = 120;
    private const float ChatBurst = 5, ChatRefillPerSecond = .5f;
    private readonly Dictionary<uint, (float Tokens, DateTimeOffset At)> chatBudget = [];
    private long chatSequence;

    private void HandleChat(Player sender, ChatSend chat)
    {
        string text = Clean(chat.Text);
        if (text.Length == 0 || !Enum.IsDefined(chat.Channel)) return;
        var now = DateTimeOffset.UtcNow;
        var (tokens, at) = chatBudget.GetValueOrDefault(sender.Connection, (ChatBurst, now));
        tokens = Math.Min(ChatBurst, tokens + (float)(now - at).TotalSeconds * ChatRefillPerSecond);
        if (tokens < 1)
        {
            transport!.Send(sender.Connection, MessageKind.ChatMessage, new ChatMessage(++chatSequence, chat.Channel, 0, "District", "You are sending messages too quickly.", weather.ElapsedSeconds));
            chatBudget[sender.Connection] = (tokens, now); return;
        }
        chatBudget[sender.Connection] = (tokens - 1, now);
        var line = new ChatMessage(++chatSequence, chat.Channel, sender.Id, sender.Character.Name, text, weather.ElapsedSeconds);
        foreach (var p in players.Values)
            if (chat.Channel == ChatChannel.World || HorizontalDistance(p.Feet, sender.Feet) <= LocalChatMeters)
                transport!.Send(p.Connection, MessageKind.ChatMessage, line);
    }

    private void AnnounceToDistrict(string text)
    {
        if (transport is null) return;
        var line = new ChatMessage(++chatSequence, ChatChannel.World, 0, "District", text, weather.ElapsedSeconds);
        foreach (var p in players.Values) transport.Send(p.Connection, MessageKind.ChatMessage, line);
    }

    /// <summary>Printable text only, whitespace collapsed, at most the protocol limit.</summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(Math.Min(text.Length, ProtocolCodec.ChatLimit));
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { space = builder.Length > 0; continue; }
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Surrogate) continue;
            if (space) { builder.Append(' '); space = false; }
            builder.Append(c);
            if (builder.Length >= ProtocolCodec.ChatLimit) break;
        }
        return builder.ToString();
    }
}
