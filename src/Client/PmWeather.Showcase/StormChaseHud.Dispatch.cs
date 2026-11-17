using Engine.GUI;

internal sealed partial class StormChaseHud
{
    private GuiDocument BuildDispatchDocument(int width, int height, StormChaseCampaignTelemetry career)
    {
        float wide = Math.Min(760, width - 24), tall = Math.Min(668, height - 24);
        float x = (width - wide) * .5f, y = (height - tall) * .5f;
        var ink = new GuiColor(229, 239, 238); var muted = new GuiColor(150, 175, 181); var accent = new GuiColor(75, 225, 188);
        var elements = new List<GuiElement>();
        void Text(string text, float top, float size = 11, GuiColor? color = null, float space = 28) => elements.Add(new GuiLabel {
            Text = text, Translation = new(22, top), Width = GuiLength.Pixels(wide - 44), Height = GuiLength.Pixels(space),
            Style = new(Theme.Font, size, 1.1f, color ?? ink), WrapMode = GuiTextWrapMode.Wrap });
        void Button(string id, string text, float left, float top, float length, bool enabled = true) => elements.Add(new GuiButton {
            Id = id, Text = text, Translation = new(left, top), Width = GuiLength.Pixels(length), Height = GuiLength.Pixels(36), IsEnabled = enabled,
            TextStyle = new(Theme.Font, 11, 1.1f, ink), BackgroundColor = new(20, 45, 52), BorderColor = new(64, 104, 111),
            HoverBackgroundColor = new(31, 78, 81), PressedBackgroundColor = new(39, 108, 99), CornerRadius = 5 });
        var job = StormChaseJobs.Get(career.Job);
        Text(job.Id == StormChaseJobs.Chaser ? "FIELD DISPATCH & COMMUNITY" : job.Department.ToUpperInvariant() + " DISPATCH", 18, 21, accent, 40);
        Text($"${career.Cash:0}   ·   Popularity {career.Popularity}   ·   Content value {StormChaseContracts.ContentValueMultiplier(career.Popularity):0.00}x", 63, 11, accent);
        // Job selection: going on or off duty happens at a service station (the department vehicles are kept there).
        float jobWidth = (wide - 44 - 3 * 8) / 4;
        for (int i = 0; i < StormChaseJobs.All.Length; i++)
        {
            var option = StormChaseJobs.All[i];
            Button("job/" + option.Id, (option.Id == job.Id ? "● " : "") + option.Title.ToUpperInvariant(), 22 + i * (jobWidth + 8), 90, jobWidth, option.Id != job.Id);
        }
        if (career.Mission is { } mission)
        {
            Text(mission.Title + $"  ·  Stop {Math.Min(mission.Step + 1, mission.StepCount)}/{mission.StepCount}", 130, 14);
            Text(mission.Objective, 166, 12, muted, 58);
            Text(mission.RequiredWorkSeconds > 0 ? $"Work completed {mission.WorkSeconds:0}/{mission.RequiredWorkSeconds:0} seconds" : "Follow the yellow marker on your map.", 235, 11, accent);
            Button("contract/interact", "WORK / INTERACT AT STOP", 22, 288, wide - 44);
            Button("contract/cancel", "CANCEL ASSIGNMENT", 22, 337, wide - 44);
        }
        else
        {
            var offers = career.Activities ?? [];
            float row = Math.Clamp((tall - 228) / 8, 27, 49);
            for (int i = 0; i < Math.Min(8, offers.Length); i++)
            {
                var offer = offers[i];
                float top = 136 + i * row;
                Button("contract/accept/" + offer.Id, offer.Title + $"   +${offer.RewardCashCents / 100m:0}", 22, top,
                    wide - 44, offer.Available);
            }
            if (offers.Length == 0) Text("Waiting for the server's dispatch board…", 160, 12, muted);
        }
        Text(career.Message, tall - 90, 10, muted, 34);
        Button("contract/close", "RETURN TO CHASE   ·   O / ESC", 22, tall - 48, wide - 44);
        return new(new GuiOverlayPanel { Width = GuiLength.Pixels(width), Height = GuiLength.Pixels(height), Items = [
            new GuiOverlayPanel { Width = GuiLength.Pixels(width), Height = GuiLength.Pixels(height), BackgroundColor = new(3, 14, 24, 185) },
            new GuiOverlayPanel { Translation = new(x, y), Width = GuiLength.Pixels(wide), Height = GuiLength.Pixels(tall),
                BackgroundColor = new(7, 22, 31, 249), BorderColor = new(49, 99, 104), BorderThickness = 1, CornerRadius = 10, Items = elements }
        ] });
    }
}
