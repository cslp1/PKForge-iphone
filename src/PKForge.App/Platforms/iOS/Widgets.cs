using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App
{
    /// <summary>
    /// iOS stand-in for the Android home-screen widgets (Platforms/Android/HomeWidgets.cs). iOS
    /// widgets are a separate WidgetKit extension written in Swift, so none is placed: the app
    /// still keeps the files the widgets would read (the last save, the partner), and everything
    /// that would paint or pin a widget does nothing.
    /// </summary>
    public static class HomeWidgets
    {
        public static bool HasAny(HomeWidgetKind kind) => false;

        public static void Refresh(HomeWidgetKind kind) { }

        public static void RefreshAll() { }

        public static Task CaptureOpenSaveAsync()
        {
            try
            {
                var services = IPlatformApplication.Current?.Services;
                if (services?.GetService<ISaveSessionService>() is not { Current: { } current }) return Task.CompletedTask;
                var id = current.Document.DocumentId;
                var detected = services.GetService<SavePickerViewModel>()?.Saves.FirstOrDefault(s => s.DocumentId == id);
                var name = detected is null ? current.Document.DisplayName
                    : string.IsNullOrWhiteSpace(detected.TrainerName) ? detected.GameLabel : $"{detected.GameLabel} · {detected.TrainerName}";
                HomeWidgetStore.Save(new LastSave(id, name));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppLog.Warn("widgets", $"Last save not remembered: {error.Message}");
            }
            return Task.CompletedTask;
        }

        public static Task SetPartnerAsync(PartnerChoice? partner)
        {
            HomeWidgetStore.Save(HomeWidgetStore.Settings with { Partner = partner });
            return Task.CompletedTask;
        }

        /// <summary>A partner from loose bytes (a save slot's export, a Bank entry), described by the engine.</summary>
        public static PartnerChoice? PartnerFrom(byte[] data, string? format, string origin, Guid? bankId, string? documentId)
        {
            var engine = IPlatformApplication.Current?.Services.GetService<ISaveEngine>();
            if (engine?.TryDescribeEntity(data, origin, format) is not { } info) return null;
            return new PartnerChoice(info.Nickname, info.Species, info.Form, info.Shiny, info.Level, origin, bankId, documentId,
                info.Format ?? format, Convert.ToBase64String(data));
        }
    }

    /// <summary>iOS stand-in for the Poképark widget publisher: no widget, nothing to publish.</summary>
    public static class PokeparkWidgetPublisher
    {
        public static bool HasActiveWidgets() => false;

        public static Task RefreshAsync(TimeSpan spriteWait, bool always = false) => Task.CompletedTask;
    }
}

namespace PKForge.App.Views
{
    /// <summary>iOS stand-in for the Widgets menu (Views/WidgetMenu.cs is Android only).</summary>
    public static class WidgetMenu
    {
        public static async Task ShowAsync(Grid host, HomeWidgetTab start) =>
            await PadMenu.ShowAsync(host, "Home-screen widgets",
                "The living park, Pokéwalker, partner and egg widgets are Android only for now.", "OK");

        /// <summary>Never offers a widget on iOS.</summary>
        public static Task OfferOnceAsync(Grid host, HomeWidgetKind kind, string line) => Task.CompletedTask;
    }
}
