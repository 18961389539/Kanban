namespace Kanban.Contracts.Display;

/// <summary>
/// 过道电视轮播片单。停留时间为建议值的两倍：首页 40s、产线 30s、报警 20s。
/// 点按暂停后 16s 再转；无活跃报警则跳过报警页。
/// </summary>
public static class DisplayCarousel
{
    public const int HomeDwellMs = 40_000;
    public const int LineDwellMs = 30_000;
    public const int AlarmDwellMs = 20_000;
    public const int ResumeAfterInteractionMs = 16_000;
    public const int TickMs = 1_000;

    public const string Home = "Home";
    public const string ProductionLine = "ProductionLine";
    public const string AlarmCenter = "AlarmCenter";

    public static readonly string[] Playlist = [Home, ProductionLine, AlarmCenter];

    public static int DwellMs(string scene) => scene switch
    {
        ProductionLine => LineDwellMs,
        AlarmCenter => AlarmDwellMs,
        _ => HomeDwellMs,
    };

    public static bool IsPlaylistScene(string? scene) =>
        scene is not null && Array.IndexOf(Playlist, scene) >= 0;

    public static string NextScene(string current, bool hasActiveAlarms)
    {
        var i = Array.IndexOf(Playlist, current);
        var start = i < 0 ? 0 : i + 1;
        for (var n = 0; n < Playlist.Length; n++)
        {
            var key = Playlist[(start + n) % Playlist.Length];
            if (key == AlarmCenter && !hasActiveAlarms)
                continue;
            return key;
        }

        return Home;
    }

    public static int SceneIndex(string scene)
    {
        var i = Array.IndexOf(Playlist, scene);
        return i < 0 ? 0 : i;
    }
}

public readonly record struct DisplayCarouselInput(
    DateTime UtcNow,
    int DeltaMs,
    bool Enabled,
    bool HasHighAlarm,
    bool HasActiveAlarms,
    string CurrentPageKey);

public readonly record struct DisplayCarouselStatus(
    bool OverlayVisible,
    bool Paused,
    bool Frozen,
    string Scene,
    int RemainingSeconds,
    string? NavigateTo,
    int SceneIndex,
    int SceneCount)
{
    public static DisplayCarouselStatus Hidden { get; } = new(
        false, false, false, DisplayCarousel.Home, 0, null, 0, DisplayCarousel.Playlist.Length);
}

/// <summary>可单测的轮播时钟：只决定下一页和剩余秒，不碰 UI。</summary>
public sealed class DisplayCarouselClock
{
    private bool _started;
    private string _scene = DisplayCarousel.Home;
    private int _remainingMs = DisplayCarousel.HomeDwellMs;
    private DateTime _pausedUntil;

    public void NoteInteraction(DateTime utcNow) =>
        _pausedUntil = utcNow.AddMilliseconds(DisplayCarousel.ResumeAfterInteractionMs);

    public DisplayCarouselStatus Step(DisplayCarouselInput input)
    {
        if (!input.Enabled)
        {
            _started = false;
            return DisplayCarouselStatus.Hidden;
        }

        if (input.HasHighAlarm)
        {
            StartScene(DisplayCarousel.AlarmCenter);
            _started = true;
            var go = input.CurrentPageKey == DisplayCarousel.AlarmCenter ? null : DisplayCarousel.AlarmCenter;
            return Status(frozen: true, paused: false, go);
        }

        if (!DisplayCarousel.IsPlaylistScene(input.CurrentPageKey))
            return DisplayCarouselStatus.Hidden;

        if (!_started || input.CurrentPageKey != _scene)
        {
            StartScene(input.CurrentPageKey);
            _started = true;
        }

        if (input.UtcNow < _pausedUntil)
            return Status(frozen: false, paused: true, null);

        if (_scene == DisplayCarousel.AlarmCenter && !input.HasActiveAlarms)
        {
            var skipTo = DisplayCarousel.NextScene(_scene, hasActiveAlarms: false);
            StartScene(skipTo);
            return Status(frozen: false, paused: false, skipTo);
        }

        _remainingMs = Math.Max(0, _remainingMs - Math.Max(0, input.DeltaMs));
        if (_remainingMs > 0)
            return Status(frozen: false, paused: false, null);

        var next = DisplayCarousel.NextScene(_scene, input.HasActiveAlarms);
        StartScene(next);
        return Status(frozen: false, paused: false, next);
    }

    private void StartScene(string scene)
    {
        _scene = scene;
        _remainingMs = DisplayCarousel.DwellMs(scene);
    }

    private DisplayCarouselStatus Status(bool frozen, bool paused, string? navigateTo) => new(
        OverlayVisible: true,
        Paused: paused,
        Frozen: frozen,
        Scene: _scene,
        RemainingSeconds: frozen ? 0 : (_remainingMs + 999) / 1000,
        NavigateTo: navigateTo,
        SceneIndex: DisplayCarousel.SceneIndex(_scene),
        SceneCount: DisplayCarousel.Playlist.Length);
}
