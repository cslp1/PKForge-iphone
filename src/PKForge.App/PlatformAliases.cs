// The background music player is a platform class with a few members beyond IMusicPlayer
// (background pause, autostart, its last error) that shared screens use by name.
#if ANDROID
global using PlatformMusicPlayer = PKForge.App.Platforms.Android.MusicPlayer;
#elif IOS
global using PlatformMusicPlayer = PKForge.App.IosMusicPlayer;
#endif
