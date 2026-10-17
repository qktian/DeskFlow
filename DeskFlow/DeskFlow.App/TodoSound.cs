using System.IO;
using System.Windows.Media;
using DeskFlow.Core.Services;

namespace DeskFlow.App;

/// <summary>播放发布目录下可替换的完成音效。MP3 优先，其次 WAV。</summary>
public static class TodoSound
{
    private static MediaPlayer? _player;

    public static void PlayDone()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Sounds");
        var path = new[] { "mission-completed.mp3", "mission-completed.wav" }
            .Select(name => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
        if (path is null) return;

        try
        {
            _player?.Close();
            var player = new MediaPlayer { Volume = 0.7 };
            _player = player;
            player.MediaEnded += (_, _) =>
            {
                player.Close();
                if (ReferenceEquals(_player, player)) _player = null;
            };
            player.MediaFailed += (_, args) =>
            {
                DiagnosticLog.Error("TodoSound", $"Could not play {Path.GetFileName(path)}", args.ErrorException);
                player.Close();
                if (ReferenceEquals(_player, player)) _player = null;
            };
            player.Open(new Uri(path, UriKind.Absolute));
            player.Play();
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("TodoSound", $"Could not play {Path.GetFileName(path)}", error);
        }
    }
}
