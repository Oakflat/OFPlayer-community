namespace OFPlayer.WinUI.Core.Playback;

/// <summary>Separates user visibility intent from asynchronous page readiness.</summary>
public sealed class CapsuleLifetime
{
    public bool Ready { get; private set; }
    public bool Closed { get; private set; }
    public bool Requested { get; private set; }
    public bool Visible => !Closed && Ready && Requested;
    public void Show() { if (!Closed) Requested = true; }
    public void Hide() => Requested = false;
    public void Loaded() { if (!Closed) Ready = true; }
    public void Close() { Closed = true; Requested = false; }
}
